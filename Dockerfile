FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY DynamicVideoCompressor.slnx Directory.Build.props Directory.Packages.props ./
COPY src/Dvc.Api/Dvc.Api.csproj src/Dvc.Api/
COPY src/Dvc.Core/Dvc.Core.csproj src/Dvc.Core/
RUN dotnet restore src/Dvc.Api/Dvc.Api.csproj

COPY src/Dvc.Api/ src/Dvc.Api/
COPY src/Dvc.Core/ src/Dvc.Core/
RUN dotnet publish src/Dvc.Api/Dvc.Api.csproj -c Release -o /app/publish --no-restore

# Static, dependency-free ffmpeg/ffprobe with libx264 - Debian's own apt
# package doesn't include libx264, so this sidesteps needing a non-free repo
# or a switch to an Ubuntu-based .NET image.
FROM mwader/static-ffmpeg:9.0.2 AS ffmpeg

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# curl is only here for the HEALTHCHECK below - the base runtime image has
# no shell HTTP client by default.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY --from=ffmpeg /ffmpeg /ffprobe /usr/local/bin/
COPY --from=build /app/publish .

# Runs as the base image's non-root user. /data holds the database and
# files; a new named volume mounted there takes on this ownership.
RUN mkdir /data && chown $APP_UID /data
USER $APP_UID

# Migrations run on startup, since this image has no dotnet-ef to apply
# them separately.
ENV ConnectionStrings__DefaultConnection="Data Source=/data/dvc.db" \
    Storage__InputDirectory=/data/Storage/Input \
    Storage__OutputDirectory=/data/Storage/Output \
    Database__AutoMigrate=true

EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Dvc.Api.dll"]
