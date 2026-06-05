FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ProcuLink.Core/ProcuLink.Core.csproj ProcuLink.Core/
COPY ProcuLink.Infrastructure/ProcuLink.Infrastructure.csproj ProcuLink.Infrastructure/
COPY ProcuLink.Transform/ProcuLink.Transform.csproj ProcuLink.Transform/
COPY ProcuLink.Api/ProcuLink.Api.csproj ProcuLink.Api/

RUN dotnet restore ProcuLink.Api/ProcuLink.Api.csproj

COPY . .

RUN dotnet publish ProcuLink.Api/ProcuLink.Api.csproj --no-restore -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# NOTE: the OCR/vision native system deps (libgomp1 for ONNX Runtime, libfontconfig1 for
# the full SkiaSharp) are intentionally NOT installed in the API image, and the direct
# RapidOcrNet ref (the ~12 MB PP-OCRv5 models) is dropped from ProcuLink.Api.csproj. PDF
# parsing / OCR / page rasterization run ONLY in the Worker (the sole Hangfire executor) —
# the API just creates a stub + enqueues, so it never dlopen's libonnxruntime / libSkiaSharp
# / libpdfium (those .so still flow transitively but are never loaded in-process). This
# removes the models + apt layer from the API image. If the API ever loads any of those
# natives in-process (a Hangfire server, a synchronous parse path, OR any SkiaSharp/ONNX/
# image work), restore this apt layer (and the csproj model ref).

COPY --from=build /app/out .

EXPOSE 8080

# Railway injects PORT at runtime; fall back to 8080 for local docker run
CMD ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} dotnet ProcuLink.Api.dll"]
