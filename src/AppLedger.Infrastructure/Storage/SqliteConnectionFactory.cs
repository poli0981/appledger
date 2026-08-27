using Microsoft.Data.Sqlite;

namespace AppLedger.Infrastructure.Storage;

/// <summary>Which process is opening the database, and therefore which pragmas apply.</summary>
public enum DatabaseRole
{
    /// <summary>The Agent: the single writer of every metric table (docs/06_DATA_MODEL.md §Ownership).</summary>
    Agent,

    /// <summary>The UI: read-only over the metric tables.</summary>
    Reader,
}

/// <summary>Tunables that are provisional or role-dependent.</summary>
/// <param name="Role">Which process this connection belongs to.</param>
/// <param name="CacheSizeKilobytes">
/// The SQLite page cache, in KB. docs/06_DATA_MODEL.md sets 32 MB for the Agent and 8 MB for the UI, and
/// says so provisionally: S1-lite measured a ~75 MB floor before any storage existed, against a 100 MB
/// budget, so a 32 MB page cache would breach it on its own. The value is a parameter precisely so that
/// v0.2 can settle it by measurement instead of by assumption (`docs/20_SPIKES.md` S1-lite Result).
/// </param>
public readonly record struct DatabaseOptions(DatabaseRole Role, int CacheSizeKilobytes)
{
    /// <summary>The Agent profile with the provisional cache size from docs/06.</summary>
    public static DatabaseOptions Agent { get; } = new(DatabaseRole.Agent, 32_000);

    /// <summary>The UI profile.</summary>
    public static DatabaseOptions Reader { get; } = new(DatabaseRole.Reader, 8_000);
}

/// <summary>
/// Opens connections to the AppLedger database with the pragmas docs/06_DATA_MODEL.md specifies.
/// </summary>
/// <remarks>
/// The pragmas live here rather than in the migration script because two of them cannot: <c>journal_mode</c>
/// is a database-level setting that must run outside a transaction, and <c>foreign_keys</c> is per
/// connection and resets on every open. A schema file that appeared to set them would be a lie.
/// </remarks>
public sealed class SqliteConnectionFactory
{
    private readonly DataRoot _dataRoot;
    private readonly DatabaseOptions _options;

    /// <summary>Creates a factory for one data root and role.</summary>
    public SqliteConnectionFactory(DataRoot dataRoot, DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        _dataRoot = dataRoot;
        _options = options;
    }

    /// <summary>The database file this factory opens.</summary>
    public string DatabasePath => _dataRoot.DatabasePath;

    /// <summary>Opens a connection with every pragma applied. The caller disposes it.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(BuildConnectionString());
        connection.Open();

        try
        {
            ApplyPragmas(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private string BuildConnectionString() => new SqliteConnectionStringBuilder
    {
        DataSource = _dataRoot.DatabasePath,

        // The reader never creates the file: if it is missing, the Agent has not run yet, and silently
        // creating an empty database would hide that behind an empty UI.
        Mode = _options.Role == DatabaseRole.Agent ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,

        // WAL lets the UI read while the Agent writes, which is the entire reason for this cache setting.
        Cache = SqliteCacheMode.Private,
        Pooling = false,
        DefaultTimeout = 5,
    }.ToString();

    private void ApplyPragmas(SqliteConnection connection)
    {
        // journal_mode is persistent in the file, so the reader neither needs nor is allowed to set it.
        if (_options.Role == DatabaseRole.Agent)
        {
            // auto_vacuum must come first. SQLite only accepts a change out of "none" while the database
            // is still new, and switching journal_mode writes the header — after which the setting would
            // silently stay at none and the nightly incremental vacuum of docs/06 would do nothing.
            Execute(connection, "PRAGMA auto_vacuum=INCREMENTAL;");
            Execute(connection, "PRAGMA journal_mode=WAL;");
            Execute(connection, "PRAGMA synchronous=NORMAL;");
        }

        Execute(connection, "PRAGMA foreign_keys=ON;");
        Execute(connection, "PRAGMA busy_timeout=5000;");
        Execute(connection, "PRAGMA temp_store=MEMORY;");

        // A negative cache_size is a size in KiB rather than in pages.
        Execute(connection, $"PRAGMA cache_size=-{_options.CacheSizeKilobytes};");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
