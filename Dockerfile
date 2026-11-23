# syntax=docker/dockerfile:1

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore first (cached layer): only project and props files.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/FiapX.VideoApi.Domain/FiapX.VideoApi.Domain.csproj src/FiapX.VideoApi.Domain/
COPY src/FiapX.VideoApi.Application/FiapX.VideoApi.Application.csproj src/FiapX.VideoApi.Application/
COPY src/FiapX.VideoApi.Infrastructure/FiapX.VideoApi.Infrastructure.csproj src/FiapX.VideoApi.Infrastructure/
COPY src/FiapX.VideoApi.Api/FiapX.VideoApi.Api.csproj src/FiapX.VideoApi.Api/
RUN dotnet restore src/FiapX.VideoApi.Api/FiapX.VideoApi.Api.csproj
COPY src/ src/
RUN dotnet publish src/FiapX.VideoApi.Api/FiapX.VideoApi.Api.csproj -c Release --no-restore -o /app

# ---- runtime: chiseled image, no shell, non-root (uid 1654) ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "FiapX.VideoApi.Api.dll"]
