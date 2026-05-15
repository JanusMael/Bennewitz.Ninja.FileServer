FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 5550
EXPOSE 5551
# curl is used by the HEALTHCHECK; install it explicitly since the aspnet slim image omits it.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
# You MUST supply FILE_SERVER_ROOT. Choose one approach:
#
#   HTTP only (no certificate):
#     docker run -e FILE_SERVER_ROOT=/data -v /host/path:/data -p 5550:5550 <image>
#
#   HTTPS (with certificate PFX):
#     docker run \
#       -e FILE_SERVER_ROOT=/data \
#       -e FILE_SERVER_CERT_PATH=/certs/server.pfx \
#       -e FILE_SERVER_CERT_PASSWORD=yourpassword \
#       -v /host/path:/data \
#       -v /host/certs:/certs:ro \
#       -p 5550:5550 -p 5551:5551 \
#       <image>
#
#   Or bind-mount a settings.json instead of using env vars:
#     -v /host/settings.json:/app/settings.json

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/Bennewitz.Ninja.FileServer/Bennewitz.Ninja.FileServer.csproj", "src/Bennewitz.Ninja.FileServer/"]
RUN dotnet restore "src/Bennewitz.Ninja.FileServer/Bennewitz.Ninja.FileServer.csproj"
COPY . .
WORKDIR "/src/"
RUN dotnet build "src/Bennewitz.Ninja.FileServer/Bennewitz.Ninja.FileServer.csproj" -c Release -o /app/build

FROM build AS publish
WORKDIR /src
RUN dotnet publish "src/Bennewitz.Ninja.FileServer/Bennewitz.Ninja.FileServer.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
# Run as unprivileged user — the app is read-only and needs no home directory
RUN adduser --disabled-password --no-create-home appuser
USER appuser
COPY --from=publish /app/publish .
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD curl -sf http://localhost:5550/ || exit 1
ENTRYPOINT ["dotnet", "Bennewitz.Ninja.FileServer.dll"]
