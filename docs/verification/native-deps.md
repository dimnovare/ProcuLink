# Native-dependency verification (PDF vision / OCR)

The PDF pipeline pulls native libraries that must load on the prod runtime base
image, **`mcr.microsoft.com/dotnet/aspnet:8.0`** (Debian, linux-x64). Because
merging to `main` auto-deploys to Railway, each native dependency is proven with a
throwaway Docker probe **before** the change ships. This file records the probes +
observed results so the "no Dockerfile change needed" / "these apt packages suffice"
claims stay reproducible.

Run any probe from a scratch folder containing the three files, then:

```bash
docker build -f Dockerfile.probe -t probe . && docker run --rm probe
```

---

## Phase 2 — PDF rasterization (PDFtoImage + SkiaSharp) → **no apt packages, no Dockerfile change**

`PDFtoImage` 5.2.1 transitively pulls **`SkiaSharp.NativeAssets.Linux.NoDependencies`**
(self-contained `libSkiaSharp.so`, no fontconfig) + **`bblanchon.PDFium.Linux`**
(`libpdfium.so`). A RID-less `dotnet publish` copies both into
`runtimes/linux-x64/native/`, and they load on the bare base image.

`Probe.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="PDFtoImage" Version="5.2.1" /></ItemGroup>
</Project>
```

`Program.cs` — encode a PNG (SkiaSharp) + render a 1-page PDF (PDFium):
```csharp
using SkiaSharp;
using var bmp = new SKBitmap(64, 64);
using (var c = new SKCanvas(bmp)) c.Clear(SKColors.CornflowerBlue);
using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
if (data is null || data.Size == 0) { Console.Error.WriteLine("SKIA ENCODE FAILED"); return 1; }
Console.WriteLine($"SkiaSharp OK: encoded {data.Size} PNG bytes");
byte[] tinyPdf = Convert.FromBase64String(
  "JVBERi0xLjEKMSAwIG9iajw8L1R5cGUvQ2F0YWxvZy9QYWdlcyAyIDAgUj4+ZW5kb2JqCjIgMCBvYmo8" +
  "PC9UeXBlL1BhZ2VzL0tpZHNbMyAwIFJdL0NvdW50IDE+PmVuZG9iagozIDAgb2JqPDwvVHlwZS9QYWdlL1Bh" +
  "cmVudCAyIDAgUi9NZWRpYUJveFswIDAgMTAwIDEwMF0+PmVuZG9iagp4cmVmCjAgNAowMDAwMDAwMDAwIDY1" +
  "NTM1IGYgCjAwMDAwMDAwMDkgMDAwMDAgbiAKMDAwMDAwMDA1MiAwMDAwMCBuIAowMDAwMDAwMTAxIDAwMDAw" +
  "IG4gCnRyYWlsZXI8PC9TaXplIDQvUm9vdCAxIDAgUj4+CnN0YXJ0eHJlZgoxNzgKJSVFT0YK");
using var page = PDFtoImage.Conversion.ToImage(tinyPdf, page: 0, password: null, options: new PDFtoImage.RenderOptions(Dpi: 200));
Console.WriteLine($"PDFium OK: rendered {page.Width}x{page.Height}");
return 0;
```

`Dockerfile.probe` — **note: no `apt-get`**:
```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Probe.csproj .
RUN dotnet restore
COPY Program.cs .
RUN dotnet publish -c Release -o /out
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /out .
ENTRYPOINT ["dotnet", "Probe.dll"]
```

**Observed (2026-06-05):**
```
SkiaSharp OK: encoded 226 PNG bytes
PDFium OK: rendered 277x277
```
→ Both natives load on bare `aspnet:8.0`. Phase 2 ships with **no Dockerfile change**.

---

## Phase 3 — self-hosted OCR (RapidOcrNet) → requires `libgomp1` + `libfontconfig1`

`RapidOcrNet` 2.0.0 runs PP-OCRv5 via **ONNX Runtime** (needs `libgomp1`) and uses the
**full** `SkiaSharp.NativeAssets.Linux` (needs `libfontconfig1`) — unlike PDFtoImage's
`.NoDependencies` variant. The PP-OCRv5 mobile models (~12 MB) are bundled in the
package and auto-copied to `models/v5/` on publish.

`Probe.csproj`: `<PackageReference Include="RapidOcrNet" Version="2.0.0" />`

`Program.cs` — render text with SkiaSharp, OCR it back:
```csharp
using RapidOcrNet; using SkiaSharp;
const string expected = "PROCULINK OCR 12345";
using var bmp = new SKBitmap(640, 160);
using (var canvas = new SKCanvas(bmp)) {
    canvas.Clear(SKColors.White);
    using var font = new SKFont { Size = 44 };
    using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
    canvas.DrawText(expected, 20, 100, SKTextAlign.Left, font, paint);
}
using var ocr = new RapidOcr(); ocr.InitModels();
var result = ocr.Detect(bmp, RapidOcrOptions.Default);
Console.WriteLine($"Recognized: '{result.StrRes.Trim()}'");
return result.StrRes.Contains("12345") ? 0 : 1;
```

`Dockerfile.probe` — **with the two apt packages**:
```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Probe.csproj .
RUN dotnet restore
COPY Program.cs .
RUN dotnet publish -c Release -o /app/out
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends libgomp1 libfontconfig1 && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/out .
RUN ls -la models/v5
CMD ["dotnet", "Probe.dll"]
```

**Observed (2026-06-05):** `models/v5/*.onnx` present in the image; output
`Recognized: 'PROCULINK OCR 12345'` → PASS. Phase 3 adds **only**
`libgomp1 libfontconfig1` to both Dockerfiles' runtime stages.
