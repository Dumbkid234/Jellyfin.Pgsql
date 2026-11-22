# Jellyfin.Pgsql (Jellyfin 12.4 / .NET 10 / PostgreSQL 18)

A production-hardened fork of [JPVenson/Jellyfin.Pgsql](https://github.com/JPVenson/Jellyfin.Pgsql) built on top of the stock official `jellyfin/jellyfin:12.4` image with `.NET 10` and `Npgsql 10.0.3`, profiled and tested against a 227,000+ item library on PostgreSQL 18.

## Pre-Built Docker Images

* **Rolling 12.4 (recommended):** `ghcr.io/dumbkid234/jellyfin.pgsql:12.4`
* **Pinned releases:** `ghcr.io/dumbkid234/jellyfin.pgsql:12.4-4` (see [Releases](https://github.com/Dumbkid234/Jellyfin.Pgsql/releases))

~~~bash
docker pull ghcr.io/dumbkid234/jellyfin.pgsql:12.4
~~~

*(GitHub Actions checks Docker Hub daily and automatically rebuilds `:12.4` only when the upstream `jellyfin/jellyfin:12.4` digest changes).*

## Key Fixes & Optimizations Included

* **Jellyfin 12.4 & .NET 10 Native:** Compiled against Jellyfin `12.4.0` packages and `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3`, with build-time DLL deduplication and `postgresql-client-18` for `pg_dump` / `psql`.
* **Non-MARS Query Buffering (`BufferingExecutionStrategy`):** Buffers `NpgsqlDataReader` results while still allowing Jellyfin's user-initiated `BeginTransaction()` calls, preventing `NpgsqlOperationInProgressException` during migrations (such as `MigrateRatingLevels`) and runtime loops.
* **Adaptive `SingleQuery` / `SplitQuery` + `ILIKE` Interceptor:** Runs bounded `Take(...)` paged queries with up to 4 lightweight includes (`UserData`, `Images`, `LinkedChildren`, `BaseItemProviders`) as `SingleQuery` (5x–12x faster paged sweeps for Maintainerr and library browsing), while forcing `SplitQuery` (stripping hardcoded `.AsSingleQuery()`) on unpaged queries or heavy includes (`MediaStreams`, `Chapters`, `Trickplay`, `People`, `ItemValues`, `Attachments`) to prevent Cartesian OOM spikes. Also rewrites `EF.Functions.Like` to `NpgsqlDbFunctionsExtensions.ILike` for case-insensitive trigram searches.
* **SQLite Compatibility & `STRICT` UUID Aggregates:** Enables Npgsql `ReverseNullOrdering` (`NULLS LAST` on `DatePlayed` / `NextUp`), disables `-infinity`/`infinity` `DateTime` conversions (with a one-time automatic startup migration for legacy `-infinity` rows), fixes `DateTimeKind.Unspecified` UTC conversion, and ensures `STRICT` PostgreSQL `min(uuid)` / `max(uuid)` aggregates (4x–10x faster `GROUP BY` deduplication).
* **SQLite Sibling Plugin Bridge:** Calls `SQLitePCL.Batteries_V2.Init()` on provider startup so SQLite-based plugins like **Playback Reporting** run cleanly alongside PostgreSQL.
* **Self-Healing Startup Engine:** Automatically verifies advisory-locked `BEFORE INSERT` deduplication triggers (`BaseItemProviders`, `UserData`, `KeyframeData`), drops any `INVALID` non-constraint indexes before creation, tunes `UserData` autovacuum, adds `BaseItems` extended statistics (`stat_baseitems_dedup`), and creates 9 `CONCURRENTLY` indexes covering `PeopleBaseItemMap`, `AncestorIds`, `MediaSegments`, `Peoples` (`lower("Name")`), and `BaseItems` (`ExtraType`, `OwnerId`, `SeasonId`, `(Type, Name)`, and the `PresentationUniqueKey` dedup covering index).
* **Safe, Portable Backups & Restores:** Drains `pg_dump` / `psql` pipes concurrently to prevent 64 KB pipe deadlocks, omits role ownership (`--no-owner --no-privileges`) so non-superuser roles can restore, restores atomically inside a single transaction, disables Npgsql Kerberos/GSS probes, and rotates old backups automatically.

## Docker Compose Example

~~~yaml
services:
  postgres:
    image: postgres:18
    shm_size: 4g # Important: Docker's 64MB default breaks Postgres parallel queries on large libraries
    environment:
      - POSTGRES_DB=jellyfin
      - POSTGRES_USER=jellyfin
      - POSTGRES_PASSWORD=changeme
    volumes:
      - ./postgres-data:/var/lib/postgresql
      - /tmp/pg-socket:/var/run/postgresql # Optional: Unix domain socket for lowest latency
    restart: unless-stopped

  jellyfin:
    image: ghcr.io/dumbkid234/jellyfin.pgsql:12.4
    environment:
      - POSTGRES_HOST=/var/run/postgresql # Use "postgres" for TCP, or "/var/run/postgresql" for Unix socket
      - POSTGRES_PORT=5432
      - POSTGRES_DB=jellyfin
      - POSTGRES_USER=jellyfin
      - POSTGRES_PASSWORD=changeme
      # Optional tuning overrides:
      # - POSTGRES_COMMAND_TIMEOUT=120
      # - POSTGRES_SSLMODE=Prefer
      # - POSTGRES_JIT=off
      # - POSTGRES_SYNCHRONOUS_COMMIT=off
    volumes:
      - ./config:/config
      - ./cache:/cache
      - ./media:/media
      - /tmp/pg-socket:/var/run/postgresql # Optional: Unix domain socket
    ports:
      - 8096:8096
    restart: unless-stopped
~~~

## Environment Variables

| Variable | Default | Description |
| :--- | :--- | :--- |
| `POSTGRES_HOST` | `jellyfin` | PostgreSQL hostname, IP, or Unix socket directory (e.g. `/var/run/postgresql`) |
| `POSTGRES_PORT` | `5432` | PostgreSQL port |
| `POSTGRES_DB` | `jellyfin` | Database name |
| `POSTGRES_USER` | `jellyfin` | PostgreSQL role/username |
| `POSTGRES_PASSWORD` | *(required)* | Database password (never written to `database.xml`) |
| `POSTGRES_SSLMODE` | `Prefer` | `Disable`, `Allow`, `Prefer`, `Require`, `VerifyCA`, `VerifyFull` |
| `POSTGRES_COMMAND_TIMEOUT` | `120` | Query timeout in seconds (`0` = unlimited) |
| `POSTGRES_JIT` | `off` | Set to `on` to re-enable PostgreSQL JIT |
| `POSTGRES_SYNCHRONOUS_COMMIT` | `off` | Set to `on` for strict per-transaction synchronous WAL flushes |
