using AppLedger.Infrastructure.Storage;
using AppLedger.Infrastructure.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace AppLedger.Infrastructure.Tests.Storage;

/// <summary>
/// Adapter smoke test for the schema and its migrator (docs/19_TESTING.md §Layers).
/// </summary>
public sealed class SchemaMigratorTests
{
    /// <summary>Every table docs/06_DATA_MODEL.md §Schema declares.</summary>
    private static readonly string[] ExpectedTables =
    [
        "app_overrides", "app_versions", "apps", "catalog_state", "disk_locations", "disk_snapshots",
        "disk_top_files", "dns_records", "events", "health_minutes", "ip_names", "meta", "metrics_1d",
        "metrics_1h", "metrics_1m", "net_hosts_daily", "process_instances", "settings", "usage_daily",
    ];

    [Fact]
    public void Migrate_FreshDatabase_ReachesTheCurrentSchemaVersion()
    {
        using var database = new TemporaryDatabase(migrate: false);

        database.Migrator.Migrate().ShouldBe(SchemaMigrator.CurrentVersion);
        database.Scalar("SELECT value FROM meta WHERE key = 'schema_version';").ShouldBe("1");
    }

    [Fact]
    public void Migrate_FreshDatabase_CreatesEveryTableTheDataModelDeclares()
    {
        using var database = new TemporaryDatabase();

        foreach (var table in ExpectedTables)
        {
            database.Tables().ShouldContain(table);
        }
    }

    /// <summary>
    /// Running the migrator at every start must be free. If this ever stopped being idempotent, the Agent
    /// would re-run DDL on a populated database on every logon.
    /// </summary>
    [Fact]
    public void Migrate_RunTwice_IsANoOp()
    {
        using var database = new TemporaryDatabase();

        database.Migrator.Migrate().ShouldBe(SchemaMigrator.CurrentVersion);
        database.Migrator.Migrate().ShouldBe(SchemaMigrator.CurrentVersion);
    }

    /// <summary>
    /// docs/06 writes <c>metrics_1h</c> and <c>metrics_1d</c> as "LIKE metrics_1m", which SQLite has no
    /// syntax for — the columns are repeated by hand in the script. This is what keeps the three from
    /// drifting apart, which would break the rollup SQL in a way no unit test of the maths would catch.
    /// </summary>
    [Theory]
    [InlineData("metrics_1h")]
    [InlineData("metrics_1d")]
    public void Migrate_CoarserMetricTiers_HaveExactlyTheMinuteTableColumns(string table)
    {
        using var database = new TemporaryDatabase();

        database.ColumnsOf(table).ShouldBe(database.ColumnsOf("metrics_1m"));
    }

    /// <summary>
    /// <c>COMMIT</c> is a SQLite keyword, so a column of that name would not parse unquoted
    /// (docs/24_ADR.md §Findings). The rename is easy to undo by accident when transcribing docs/06.
    /// </summary>
    [Fact]
    public void Migrate_CommitColumn_IsNamedCommitBytes()
    {
        using var database = new TemporaryDatabase();

        var columns = database.ColumnsOf("metrics_1m");

        columns.ShouldContain("commit_bytes");
        columns.ShouldNotContain("commit");
    }

    [Fact]
    public void Open_AgentConnection_AppliesTheDocumentedPragmas()
    {
        using var database = new TemporaryDatabase();

        database.Scalar("PRAGMA journal_mode;").ShouldBe("wal", StringCompareShould.IgnoreCase);
        database.Scalar("PRAGMA foreign_keys;").ShouldBe("1");
        database.Scalar("PRAGMA auto_vacuum;").ShouldBe("2");   // 2 = INCREMENTAL
        database.Scalar("PRAGMA busy_timeout;").ShouldBe("5000");
    }

    /// <summary>
    /// The page cache is provisional, not settled (docs/06 §Pragmas): S1-lite measured a ~75 MB floor
    /// against a 100 MB budget, so 32 MB of cache would breach it on its own. What this asserts is that
    /// the value is a knob v0.2 can turn, not a constant baked into a connection string.
    /// </summary>
    [Fact]
    public void Open_CacheSize_ComesFromOptionsRatherThanBeingHardCoded()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.Root, new DatabaseOptions(DatabaseRole.Agent, 1_234));

        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA cache_size;";

        // Negative means "this many KiB" rather than "this many pages".
        command.ExecuteScalar()?.ToString().ShouldBe("-1234");
    }

    /// <summary>
    /// The UI opens the same file read-only and must never create it: a missing database means the Agent
    /// has not run, and silently creating an empty one would hide that behind an empty UI.
    /// </summary>
    [Fact]
    public void Open_ReaderRole_RefusesToCreateAMissingDatabase()
    {
        using var database = new TemporaryDatabase(migrate: false);
        var factory = new SqliteConnectionFactory(database.Root, DatabaseOptions.Reader);

        Should.Throw<Microsoft.Data.Sqlite.SqliteException>(() => factory.Open().Dispose());
    }

    [Fact]
    public void ReadVersion_EmptyDatabase_IsZero()
    {
        using var database = new TemporaryDatabase(migrate: false);
        using var connection = database.Factory.Open();

        SchemaMigrator.ReadVersion(connection).ShouldBe(0);
    }

    /// <summary>
    /// A database written by a newer build is refused rather than downgraded. Running old DDL over a newer
    /// schema is the one migration mistake that destroys history instead of failing.
    /// </summary>
    [Fact]
    public void Migrate_DatabaseFromANewerBuild_IsRefused()
    {
        using var database = new TemporaryDatabase();

        using (var connection = database.Factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE meta SET value = '99' WHERE key = 'schema_version';";
            command.ExecuteNonQuery();
        }

        Should.Throw<InvalidOperationException>(() => database.Migrator.Migrate());
    }

    /// <summary>
    /// Foreign keys are on, so deleting an app row must take its metrics with it. This is the mechanism
    /// docs/12_PRIVACY_AND_RETENTION.md §Purge semantics relies on for "purge one app".
    /// </summary>
    [Fact]
    public void Schema_DeletingAnApp_CascadesToItsMetrics()
    {
        using var database = new TemporaryDatabase();

        using (var connection = database.Factory.Open())
        {
            Execute(connection,
                "INSERT INTO apps (app_id, display_name, source, confidence, first_seen_utc, last_seen_utc) "
                + "VALUES ('cat:test', 'Test', 'cat', 0.95, 1, 2);");
            Execute(connection, "INSERT INTO metrics_1m (app_id, ts, runtime_s) VALUES ('cat:test', 60, 60);");
            Execute(connection, "DELETE FROM apps WHERE app_id = 'cat:test';");
        }

        database.Scalar("SELECT COUNT(*) FROM metrics_1m;").ShouldBe("0");
    }

    private static void Execute(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
