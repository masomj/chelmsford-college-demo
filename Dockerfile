# syntax=docker/dockerfile:1
#
# Chelmsford College website demo: Umbraco 17 on ASP.NET Core / .NET 10.
#
# The image holds the application only. Databases, data-protection keys and
# Umbraco media live on volumes (/app/data, /app/media), and the college's
# tender assets are mounted read-only at /app/college-content at run time.
# See deploy/DEPLOY.md.

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Set automatically by BuildKit (amd64 or arm64). Used to pick the runtime
# identifier so Umbraco's app-local ICU libraries are published for Linux.
ARG TARGETARCH
WORKDIR /src

# Project files first, so the restore layer is cached until a dependency changes.
COPY global.json Directory.Build.props Directory.Packages.props Chelmsford.slnx ./
COPY src/Chelmsford.Core/Chelmsford.Core.csproj src/Chelmsford.Core/
COPY src/Chelmsford.Infrastructure/Chelmsford.Infrastructure.csproj src/Chelmsford.Infrastructure/
COPY src/Chelmsford.Web/Chelmsford.Web.csproj src/Chelmsford.Web/

RUN case "$TARGETARCH" in \
      arm64) echo linux-arm64 > /tmp/rid ;; \
      *)     echo linux-x64   > /tmp/rid ;; \
    esac \
 && dotnet restore src/Chelmsford.Web/Chelmsford.Web.csproj -r "$(cat /tmp/rid)"

COPY src/ src/

# Framework-dependent publish for a Linux RID: the Web csproj then turns on
# UmbracoUseAppLocalIcu and the ICU native libraries are copied alongside.
RUN dotnet publish src/Chelmsford.Web/Chelmsford.Web.csproj \
      -c Release \
      -r "$(cat /tmp/rid)" \
      --self-contained false \
      --no-restore \
      -o /app/publish \
 && rm -f /app/publish/appsettings.Local.json

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl is only for the health check.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build --chown=app:app /app/publish ./

# Mount points. Creating them here, owned by the non-root user, means new
# named volumes start out writable by the app.
RUN mkdir -p /app/data /app/media /app/college-content /app/umbraco \
 && chown app:app /app \
 && chown -R app:app /app/data /app/media /app/umbraco

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_HTTP_PORTS= \
    DOTNET_RUNNING_IN_CONTAINER=true \
    Serilog__WriteTo__0__Name=Console \
    ConnectionStrings__umbracoDbDSN="Data Source=/app/data/Umbraco.sqlite.db;Cache=Shared;Foreign Keys=True;Pooling=True" \
    ConnectionStrings__umbracoDbDSN_ProviderName=Microsoft.Data.Sqlite \
    ConnectionStrings__SiteData="Data Source=/app/data/site.db" \
    ConnectionStrings__CollegeSource="Data Source=/app/data/college-source.db" \
    College__ContentPath=/app/college-content \
    College__DataPath=/app/data \
    Umbraco__CMS__Global__UmbracoMediaPhysicalRootPath=/app/media \
    Umbraco__CMS__Global__UseHttps=true

VOLUME ["/app/data", "/app/media"]

# Built-in non-root user in the .NET images (UID 1654).
USER app

EXPOSE 8080

# First start runs the unattended install and seeds content, so allow time.
HEALTHCHECK --interval=30s --timeout=5s --start-period=180s --retries=3 \
  CMD curl -fsS http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "Chelmsford.Web.dll"]
