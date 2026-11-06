#!/bin/bash
set -e

CONFIG_DIR="/config"
PLUGIN_DIR="$CONFIG_DIR/plugins/PostgreSQL"
BACKUP_DIR="$CONFIG_DIR/data/PgsqlBackups"

# Wipe existing plugin directory to prevent stale/duplicate assembly conflicts
rm -rf "$PLUGIN_DIR"
mkdir -p "$PLUGIN_DIR"

# Copy plugin files, stripping core DLLs that Jellyfin already loads in memory
cp -rf /opt/jellyfin-pgsql/* "$PLUGIN_DIR/"
find "$PLUGIN_DIR" -type f ! -name "Jellyfin.Plugin.Pgsql*" ! -name "Npgsql*" -delete

# Rotate PostgreSQL backups: keep only the 2 most recent .sql dumps to save SSD space
if [ -d "$BACKUP_DIR" ]; then
    ls -1t "$BACKUP_DIR"/*.sql 2>/dev/null | tail -n +3 | xargs -r rm -f
fi

# Export standard Postgres environment variables for automated pg_dump backups
if [ -n "$POSTGRES_HOST" ]; then
    export PGHOST="${POSTGRES_HOST}"
    export PGPORT="${POSTGRES_PORT:-5432}"
    export PGDATABASE="${POSTGRES_DB:-jellyfin}"
    export PGUSER="${POSTGRES_USER:-jellyfin}"
    export PGPASSWORD="${POSTGRES_PASSWORD}"

    # Only generate database.xml if it does not already exist
    if [ ! -f "$CONFIG_DIR/database.xml" ]; then
        CONN_STR="Host=${POSTGRES_HOST};Port=${PGPORT};Database=${PGDATABASE};Username=${PGUSER};Password=${PGPASSWORD};Pooling=true;Minimum Pool Size=10;Maximum Pool Size=50;Connection Idle Lifetime=300;Command Timeout=120;Options=-c jit=off -c synchronous_commit=off;"
        [ -n "$POSTGRES_SSLMODE" ] && CONN_STR="${CONN_STR}SSL Mode=${POSTGRES_SSLMODE};"
        [ -n "$POSTGRES_TRUSTSERVERCERTIFICATE" ] && CONN_STR="${CONN_STR}Trust Server Certificate=${POSTGRES_TRUSTSERVERCERTIFICATE};"
        [ -n "$POSTGRES_COMMAND_TIMEOUT" ] && CONN_STR="${CONN_STR}Command Timeout=${POSTGRES_COMMAND_TIMEOUT};"

        cat << XML > "$CONFIG_DIR/database.xml"
<?xml version="1.0" encoding="utf-8"?>
<DatabaseConfigurationOptions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <DatabaseType>PLUGIN_PROVIDER</DatabaseType>
  <CustomProviderOptions>
    <PluginAssembly>PostgreSQL/Jellyfin.Plugin.Pgsql.dll</PluginAssembly>
    <PluginName>PostgreSQL</PluginName>
    <ConnectionString>${CONN_STR}</ConnectionString>
  </CustomProviderOptions>
  <LockingBehavior>NoLock</LockingBehavior>
</DatabaseConfigurationOptions>
XML
    fi
fi

exec /jellyfin/jellyfin "$@"
