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
COPY --from=build /app/out .

EXPOSE 8080

# Railway injects PORT at runtime; fall back to 8080 for local docker run
CMD ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} dotnet ProcuLink.Api.dll"]
