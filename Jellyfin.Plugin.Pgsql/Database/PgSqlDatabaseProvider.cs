using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// Configures Jellyfin to use a PostgreSQL database.
/// </summary>
[JellyfinDatabaseProviderKey("Jellyfin-PgSql")]
public sealed class PgSqlDatabaseProvider : IJellyfinDatabaseProvider
{
    private const string BackupFolderName = "PgsqlBackups";
    private static int _optimizationsVerified;
    private readonly ILogger<PgSqlDatabaseProvider> _logger;
    private readonly IApplicationPaths _applicationPaths;
    private DatabaseConfigurationOptions? _databaseConfiguration;

    /// <summary>
    /// Initializes a new instance of the <see cref="PgSqlDatabaseProvider"/> class.
    /// </summary>
    /// <param name="applicationPaths">Service to construct the backup paths.</param>
    /// <param name="logger">A logger.</param>
    public PgSqlDatabaseProvider(IApplicationPaths applicationPaths, ILogger<PgSqlDatabaseProvider> logger)
    {
        AppContext.SetSwitch("Npgsql.DisableDateTimeInfinityConversions", true);

        try
        {
            SQLitePCL.Batteries_V2.Init();
        }
        catch
        {
            // Ignore if already initialized by host.
        }

        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <inheritdoc/>
    public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        _databaseConfiguration = databaseConfiguration;
        var customOptions = databaseConfiguration.CustomProviderOptions?.Options;

        var connectionBuilder = GetConnectionBuilder(databaseConfiguration);
        connectionBuilder.ApplicationName = $"jellyfin+{FileVersionInfo.GetVersionInfo(Assembly.GetEntryAssembly()!.Location).FileVersion}";

        var safeConn = new NpgsqlConnectionStringBuilder(connectionBuilder.ToString()) { Password = null }.ToString();
        _logger.LogInformation("PostgreSQL connection string: {ConnectionString}", safeConn);

        EnsurePostgresOptimizations(connectionBuilder.ToString());

        options
            .UseNpgsql(connectionBuilder.ToString(), pgSqlOptions =>
            {
                pgSqlOptions.MigrationsAssembly(GetType().Assembly.FullName);
                pgSqlOptions.ExecutionStrategy(dependencies => new BufferingExecutionStrategy(dependencies));
                pgSqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                pgSqlOptions.GetType()
                    .GetMethod("ReverseNullOrdering", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    ?.Invoke(pgSqlOptions, new object[] { true });
                pgSqlOptions.CommandTimeout(connectionBuilder.CommandTimeout);
            })
            .AddInterceptors(ForceSplitQueryInterceptor.Instance)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.MultipleCollectionIncludeWarning));

        var enableSensitiveDataLogging = GetCustomDatabaseOption(customOptions, "EnableSensitiveDataLogging", e => e.Equals(bool.TrueString, StringComparison.OrdinalIgnoreCase), () => false);
        if (enableSensitiveDataLogging)
        {
            options.EnableSensitiveDataLogging(true);
            _logger.LogInformation("EnableSensitiveDataLogging is enabled on PostgreSQL connection");
        }
    }

    /// <inheritdoc/>
    public async Task RunScheduledOptimisation(CancellationToken cancellationToken)
    {
        if (DbContextFactory is null)
        {
            return;
        }

        var context = await DbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            if (context.Database.IsNpgsql())
            {
                var hasItems = await context.BaseItems.AnyAsync(e => !e.IsFolder && e.Type != "PLACEHOLDER", cancellationToken).ConfigureAwait(false);
                await context.Database.ExecuteSqlRawAsync(hasItems ? "VACUUM ANALYZE" : "VACUUM", cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("PostgreSQL database optimized successfully");
            }
        }
    }

    /// <inheritdoc/>
    public void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UseCollation("C");

        var utcConverter = new ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Utc ? v : (v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc)),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(utcConverter);
                }
            }
        }
    }

    /// <inheritdoc/>
    public Task RunShutdownTask(CancellationToken cancellationToken)
    {
        NpgsqlConnection.ClearAllPools();
        _logger.LogInformation("PostgreSQL connection pools cleared on shutdown");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
    }

    /// <inheritdoc/>
    public async Task<string> MigrationBackupFast(CancellationToken cancellationToken)
    {
        var key = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var backupFolder = Path.Combine(_applicationPaths.DataPath, BackupFolderName);
        Directory.CreateDirectory(backupFolder);

        var connectionBuilder = GetConnectionBuilder(_databaseConfiguration);
        var backupFile = Path.Combine(backupFolder, $"{key}_{connectionBuilder.Database}.sql");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "pg_dump",
                Arguments = $"--host={connectionBuilder.Host} --port={connectionBuilder.Port} --username={connectionBuilder.Username} --dbname={connectionBuilder.Database} --file=\"{backupFile}\" --no-password --clean --if-exists",
                Environment = { ["PGPASSWORD"] = connectionBuilder.Password ?? string.Empty, ["PGSSLMODE"] = ToLibpqSslMode(connectionBuilder.SslMode) },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        _logger.LogInformation("Starting PostgreSQL backup: {BackupFile}", backupFile);
        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdOutTask, stdErrTask, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var error = await stdErrTask.ConfigureAwait(false);
            _logger.LogError("pg_dump failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
            throw new InvalidOperationException($"pg_dump failed: {error}");
        }

        _logger.LogInformation("PostgreSQL backup completed successfully: {BackupFile}", backupFile);
        return key;
    }

    /// <inheritdoc/>
    public async Task RestoreBackupFast(string key, CancellationToken cancellationToken)
    {
        NpgsqlConnection.ClearAllPools();

        var connectionBuilder = GetConnectionBuilder(_databaseConfiguration);
        var backupFile = Path.Combine(_applicationPaths.DataPath, BackupFolderName, $"{key}_{connectionBuilder.Database}.sql");

        if (!File.Exists(backupFile))
        {
            _logger.LogCritical("Tried to restore a backup that does not exist: {Key}", key);
            return;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "psql",
                Arguments = $"--host={connectionBuilder.Host} --port={connectionBuilder.Port} --username={connectionBuilder.Username} --dbname={connectionBuilder.Database} --no-password --quiet --set=ON_ERROR_STOP=1 --single-transaction --command=\"DROP SCHEMA public CASCADE; CREATE SCHEMA public AUTHORIZATION pg_database_owner; GRANT USAGE ON SCHEMA public TO PUBLIC;\" --file=\"{backupFile}\"",
                Environment = { ["PGPASSWORD"] = connectionBuilder.Password ?? string.Empty, ["PGSSLMODE"] = ToLibpqSslMode(connectionBuilder.SslMode) },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        _logger.LogInformation("Starting PostgreSQL restore from: {BackupFile}", backupFile);
        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdOutTask, stdErrTask, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var error = await stdErrTask.ConfigureAwait(false);
            _logger.LogError("psql restore failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
            throw new InvalidOperationException($"psql restore failed: {error}");
        }

        _logger.LogInformation("PostgreSQL restore completed successfully from: {BackupFile}", backupFile);
    }

    /// <inheritdoc/>
    public Task DeleteBackup(string key)
    {
        var connectionBuilder = GetConnectionBuilder(_databaseConfiguration);
        var backupFolder = Path.Combine(_applicationPaths.DataPath, BackupFolderName);
        var backupFile = Path.Combine(backupFolder, $"{key}_{connectionBuilder.Database}.sql");

        if (!File.Exists(backupFile))
        {
            _logger.LogCritical("Tried to delete a backup that does not exist: {Key}", key);
            return Task.CompletedTask;
        }

        foreach (var olderBackup in Directory.EnumerateFiles(backupFolder, $"*_{connectionBuilder.Database}.sql"))
        {
            if (!string.Equals(olderBackup, backupFile, StringComparison.Ordinal))
            {
                File.Delete(olderBackup);
                _logger.LogInformation("Deleted older backup file: {BackupFile}", olderBackup);
            }
        }

        _logger.LogInformation("Keeping latest pre-migration backup: {BackupFile}", backupFile);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames)
    {
        ArgumentNullException.ThrowIfNull(tableNames);
        var truncateAllQuery = string.Join('\n', tableNames.Select(t => $"TRUNCATE TABLE \"{t}\" RESTART IDENTITY CASCADE;"));
        await dbContext.Database.ExecuteSqlRawAsync(truncateAllQuery).ConfigureAwait(false);
        _logger.LogInformation("PostgreSQL database tables purged successfully");
    }

    private static string ToLibpqSslMode(SslMode sslMode) => sslMode switch
    {
        SslMode.Disable => "disable",
        SslMode.Allow => "allow",
        SslMode.Require => "require",
        SslMode.VerifyCA => "verify-ca",
        SslMode.VerifyFull => "verify-full",
        _ => "prefer"
    };

    private static T? GetCustomDatabaseOption<T>(ICollection<CustomDatabaseOption>? options, string key, Func<string, T> converter, Func<T>? defaultValue = null)
    {
        var value = options?.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return value is not null ? converter(value.Value) : (defaultValue is not null ? defaultValue() : default);
    }

    private static NpgsqlConnectionStringBuilder GetConnectionBuilder(DatabaseConfigurationOptions? databaseConfiguration)
    {
        var options = databaseConfiguration?.CustomProviderOptions?.Options;
        var rawConnStr = databaseConfiguration?.CustomProviderOptions?.ConnectionString;
        var cb = string.IsNullOrWhiteSpace(rawConnStr) ? new NpgsqlConnectionStringBuilder() : new NpgsqlConnectionStringBuilder(rawConnStr);

        var envHost = Environment.GetEnvironmentVariable("POSTGRES_HOST");
        var envPort = Environment.GetEnvironmentVariable("POSTGRES_PORT");
        var envDb = Environment.GetEnvironmentVariable("POSTGRES_DB");
        var envUser = Environment.GetEnvironmentVariable("POSTGRES_USER");
        var envPass = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");
        var envSsl = Environment.GetEnvironmentVariable("POSTGRES_SSLMODE");
        var envTimeout = Environment.GetEnvironmentVariable("POSTGRES_COMMAND_TIMEOUT");

        if (!string.IsNullOrWhiteSpace(envHost)) cb.Host = envHost;
        else if (string.IsNullOrWhiteSpace(cb.Host)) cb.Host = "jellyfin";

        if (int.TryParse(envPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)) cb.Port = port;
        else if (cb.Port <= 0) cb.Port = 5432;

        if (!string.IsNullOrWhiteSpace(envDb)) cb.Database = envDb;
        else if (string.IsNullOrWhiteSpace(cb.Database)) cb.Database = "jellyfin";

        if (!string.IsNullOrWhiteSpace(envUser)) cb.Username = envUser;
        else if (string.IsNullOrWhiteSpace(cb.Username)) cb.Username = "jellyfin";

        if (!string.IsNullOrWhiteSpace(envPass)) cb.Password = envPass;
        if (string.IsNullOrEmpty(cb.Password)) throw new InvalidOperationException("PostgreSQL password must be provided via POSTGRES_PASSWORD or database.xml");

        if (!string.IsNullOrWhiteSpace(envSsl) && Enum.TryParse<SslMode>(envSsl, true, out var sslMode)) cb.SslMode = sslMode;
        if (int.TryParse(envTimeout, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cmdTimeout)) cb.CommandTimeout = cmdTimeout;
        if (cb.GssEncryptionMode == GssEncryptionMode.Prefer) cb.GssEncryptionMode = GssEncryptionMode.Disable;

        cb.Pooling = true;
        if (cb.MinPoolSize < 10) cb.MinPoolSize = 10;
        if (cb.MaxPoolSize == 100) cb.MaxPoolSize = 50;
        if (cb.CommandTimeout == 30) cb.CommandTimeout = 120;
        if (string.IsNullOrEmpty(cb.Options)) cb.Options = "-c jit=off -c synchronous_commit=off";

        if (GetCustomDatabaseOption(options, "IncludeErrorDetail", e => e.Equals(bool.TrueString, StringComparison.OrdinalIgnoreCase), () => false)) cb.IncludeErrorDetail = true;
        if (GetCustomDatabaseOption(options, "LogParameters", e => e.Equals(bool.TrueString, StringComparison.OrdinalIgnoreCase), () => false)) cb.LogParameters = true;

        return cb;
    }

    private sealed class ForceSplitQueryInterceptor : IQueryExpressionInterceptor
    {
        public static readonly ForceSplitQueryInterceptor Instance = new();

        public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
            => StripSingleQueryVisitor.Visitor.Visit(queryExpression);

        private sealed class StripSingleQueryVisitor : ExpressionVisitor
        {
            public static readonly StripSingleQueryVisitor Visitor = new();

            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                if (node.Method.Name == nameof(RelationalQueryableExtensions.AsSingleQuery) &&
                    node.Method.DeclaringType == typeof(RelationalQueryableExtensions))
                {
                    return Visit(node.Arguments[0]);
                }

                if (node.Method.Name == nameof(DbFunctionsExtensions.Like) &&
                    node.Method.DeclaringType == typeof(DbFunctionsExtensions))
                {
                    var paramTypes = node.Method.GetParameters().Select(p => p.ParameterType).ToArray();
                    var ilike = typeof(NpgsqlDbFunctionsExtensions).GetMethod(nameof(NpgsqlDbFunctionsExtensions.ILike), paramTypes);
                    if (ilike is not null)
                    {
                        return Expression.Call(null, ilike, Visit(node.Arguments));
                    }
                }

                return base.VisitMethodCall(node);
            }
        }
    }

    private void EnsurePostgresOptimizations(string connectionString)
    {
        if (Interlocked.CompareExchange(ref _optimizationsVerified, 1, 0) != 0)
        {
            return;
        }

        try
        {
            using var conn = new NpgsqlConnection(connectionString);
            conn.Open();

            using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'BaseItemProviders');";
                if (checkCmd.ExecuteScalar() is not true)
                {
                    Interlocked.Exchange(ref _optimizationsVerified, 0);
                    return;
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = 120;
                cmd.CommandText = @"
                    CREATE OR REPLACE FUNCTION public.uuid_smaller(uuid, uuid) RETURNS uuid LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS 'SELECT CASE WHEN $1 < $2 THEN $1 ELSE $2 END';
                    CREATE OR REPLACE FUNCTION public.uuid_larger(uuid, uuid) RETURNS uuid LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS 'SELECT CASE WHEN $1 > $2 THEN $1 ELSE $2 END';

                    CREATE OR REPLACE FUNCTION trg_fn_clean_duplicate_providers() RETURNS TRIGGER AS $$
                    BEGIN
                        PERFORM pg_advisory_xact_lock(hashtext(NEW.""ItemId""::text), hashtext(NEW.""ProviderId""));
                        DELETE FROM ""BaseItemProviders"" WHERE ""ItemId"" = NEW.""ItemId"" AND ""ProviderId"" = NEW.""ProviderId"";
                        RETURN NEW;
                    END; $$ LANGUAGE plpgsql;

                    CREATE OR REPLACE FUNCTION trg_fn_clean_duplicate_keyframedata() RETURNS TRIGGER AS $$
                    BEGIN
                        PERFORM pg_advisory_xact_lock(hashtext(NEW.""ItemId""::text), 1);
                        DELETE FROM ""KeyframeData"" WHERE ""ItemId"" = NEW.""ItemId"";
                        RETURN NEW;
                    END; $$ LANGUAGE plpgsql;

                    CREATE OR REPLACE FUNCTION trg_fn_clean_duplicate_userdata() RETURNS TRIGGER AS $$
                    BEGIN
                        PERFORM pg_advisory_xact_lock(hashtext(NEW.""ItemId""::text || NEW.""CustomDataKey""), hashtext(NEW.""UserId""::text));
                        DELETE FROM ""UserData"" WHERE ""ItemId"" = NEW.""ItemId"" AND ""UserId"" = NEW.""UserId"" AND ""CustomDataKey"" = NEW.""CustomDataKey"";
                        RETURN NEW;
                    END; $$ LANGUAGE plpgsql;

                    DO $$
                    DECLARE
                        tbl RECORD;
                    BEGIN
                        IF coalesce(obj_description('public.trg_fn_clean_duplicate_providers()'::regprocedure, 'pg_proc'), '') <> 'jf_inf_clean_v1' THEN
                            FOR tbl IN
                                SELECT c.table_name,
                                    string_agg(format('%I = CASE WHEN %I = ''-infinity'' THEN ''0001-01-01 00:00:00+00''::%s WHEN %I = ''infinity'' THEN ''9999-12-31 23:59:59.999999+00''::%s ELSE %I END', c.column_name, c.column_name, c.data_type, c.column_name, c.data_type, c.column_name), ', ') AS sets,
                                    string_agg(format('%I IN (''-infinity'', ''infinity'')', c.column_name), ' OR ') AS wheres
                                FROM information_schema.columns c
                                JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name
                                WHERE c.table_schema = 'public' AND t.table_type = 'BASE TABLE' AND c.data_type IN ('timestamp with time zone', 'timestamp without time zone')
                                GROUP BY c.table_name
                            LOOP
                                EXECUTE format('UPDATE public.%I SET %s WHERE %s;', tbl.table_name, tbl.sets, tbl.wheres);
                            END LOOP;
                            COMMENT ON FUNCTION public.trg_fn_clean_duplicate_providers() IS 'jf_inf_clean_v1';
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM pg_proc WHERE proname = 'min' AND oidvectortypes(proargtypes) = 'uuid') THEN
                            CREATE AGGREGATE public.min(uuid) (SFUNC = public.uuid_smaller, STYPE = uuid, COMBINEFUNC = public.uuid_smaller, SORTOP = <, PARALLEL = SAFE);
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM pg_proc WHERE proname = 'max' AND oidvectortypes(proargtypes) = 'uuid') THEN
                            CREATE AGGREGATE public.max(uuid) (SFUNC = public.uuid_larger, STYPE = uuid, COMBINEFUNC = public.uuid_larger, SORTOP = >, PARALLEL = SAFE);
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'KeyframeData') THEN
                            IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_clean_duplicate_keyframedata') THEN
                                CREATE TRIGGER trg_clean_duplicate_keyframedata BEFORE INSERT ON ""KeyframeData"" FOR EACH ROW EXECUTE FUNCTION trg_fn_clean_duplicate_keyframedata();
                            END IF;
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_clean_duplicate_providers') THEN
                            CREATE TRIGGER trg_clean_duplicate_providers BEFORE INSERT ON ""BaseItemProviders"" FOR EACH ROW EXECUTE FUNCTION trg_fn_clean_duplicate_providers();
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'UserData') THEN
                            IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_clean_duplicate_userdata') THEN
                                CREATE TRIGGER trg_clean_duplicate_userdata BEFORE INSERT ON ""UserData"" FOR EACH ROW EXECUTE FUNCTION trg_fn_clean_duplicate_userdata();
                            END IF;
                            IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND c.relname = 'UserData' AND c.reloptions IS NOT NULL) THEN
                                ALTER TABLE ""UserData"" SET (autovacuum_vacuum_scale_factor = 0.02, autovacuum_vacuum_cost_limit = 1000);
                            END IF;
                        END IF;
                    END $$;";
                cmd.ExecuteNonQuery();
            }

            var invalidIndexes = new List<string>();
            using (var invCmd = conn.CreateCommand())
            {
                invCmd.CommandText = @"
                    SELECT c.relname
                    FROM pg_index x
                    JOIN pg_class c ON c.oid = x.indexrelid
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'public'
                      AND NOT x.indisvalid
                      AND NOT EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid = x.indexrelid);";
                using var reader = invCmd.ExecuteReader();
                while (reader.Read())
                {
                    invalidIndexes.Add(reader.GetString(0));
                }
            }

            foreach (var invalidIdx in invalidIndexes)
            {
                using var dropCmd = conn.CreateCommand();
                dropCmd.CommandTimeout = 300;
                dropCmd.CommandText = $"DROP INDEX CONCURRENTLY IF EXISTS \"{invalidIdx.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
                dropCmd.ExecuteNonQuery();
                _logger.LogWarning("[PgSQL] Dropped invalid index {IndexName} so it can be rebuilt cleanly.", invalidIdx);
            }

            string[] indexCommands =
            {
                @"CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_peoplebaseitemmap_people_item ON ""PeopleBaseItemMap"" (""PeopleId"", ""ItemId"");",
                @"CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_ancestorids_parent_item ON ""AncestorIds"" (""ParentItemId"", ""ItemId"");",
                @"CREATE INDEX CONCURRENTLY IF NOT EXISTS ""IX_BaseItems_ExtraType_OwnerId"" ON ""BaseItems"" (""ExtraType"", ""OwnerId"") WHERE ""ExtraType"" IS NOT NULL;",
                @"CREATE INDEX CONCURRENTLY IF NOT EXISTS ""IX_BaseItems_OwnerId"" ON ""BaseItems"" (""OwnerId"") WHERE ""OwnerId"" IS NOT NULL;",
                @"CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_baseitems_type_name ON ""BaseItems"" (""Type"", ""Name"");"
            };

            foreach (var indexSql in indexCommands)
            {
                using var idxCmd = conn.CreateCommand();
                idxCmd.CommandTimeout = 300;
                idxCmd.CommandText = indexSql;
                idxCmd.ExecuteNonQuery();
            }

            _logger.LogInformation("[PgSQL] Verified PostgreSQL triggers, UUID aggregates, autovacuum settings, and composite indexes.");
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _optimizationsVerified, 0);
            _logger.LogWarning(ex, "[PgSQL] Could not automatically verify PostgreSQL optimizations.");
        }
    }
}
