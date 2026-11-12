# Stage 1: Build and prune the PostgreSQL Plugin
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS builder

WORKDIR /src
COPY . .

WORKDIR /src/Jellyfin.Plugin.Pgsql
RUN dotnet restore && \
    dotnet publish -c Release --no-restore -o /out/plugin && \
    find /out/plugin -type f ! -name "Jellyfin.Plugin.Pgsql*" ! -name "Npgsql*" -delete

# Stage 2: Target Jellyfin 12.4 Base Image
FROM jellyfin/jellyfin:12.4

RUN apt-get update && \
    apt-get install -y --no-install-recommends curl ca-certificates gnupg lsb-release && \
    install -d /etc/apt/keyrings && \
    curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc | gpg --dearmor -o /etc/apt/keyrings/postgresql.gpg && \
    echo "deb [signed-by=/etc/apt/keyrings/postgresql.gpg] http://apt.postgresql.org/pub/repos/apt $(lsb_release -cs)-pgdg main" > /etc/apt/sources.list.d/pgdg.list && \
    apt-get update && \
    apt-get install -y --no-install-recommends postgresql-client-18 && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

COPY --from=builder /out/plugin /opt/jellyfin-pgsql
COPY docker/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
