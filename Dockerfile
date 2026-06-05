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

# Native deps for the self-hosted OCR engine (RapidOcrNet / PP-OCRv5):
#   libgomp1       — OpenMP runtime required by ONNX Runtime (libonnxruntime.so)
#   libfontconfig1 — required by RapidOcrNet's SkiaSharp.NativeAssets.Linux (full variant)
# Docker-verified on this base image; see docs/verification/native-deps.md.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgomp1 libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/out .

EXPOSE 8080

# Railway injects PORT at runtime; fall back to 8080 for local docker run
CMD ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} dotnet ProcuLink.Api.dll"]
