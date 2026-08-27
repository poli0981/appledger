using AppLedger.Infrastructure.Storage;

namespace AppLedger.Infrastructure.Tests.TestSupport;

/// <summary>
/// A migrated database in a throwaway data root, so no test can reach the user's real history.
/// </summary>
internal sealed class TemporaryDatabase : IDisposable
{
    private readonly string _scratch;

    internal TemporaryDatabase(bool migrate = true)
    {
        _scratch = Path.Combine(Path.GetTempPath(), "appledger-db-" + Guid.NewGuid().ToString("N")[..12]);
        Root = new DataRoot(Path.Combine(_scratch, DataRoot.FolderName));
        Root.EnsureCreated();

        Factory = new SqliteConnectionFactory(Root, DatabaseOptions.Agent);
        Migrator = new SchemaMigrator(Factory, Root);

        if (migrate)
        {
            Migrator.Migrate();
        }
    }

    internal DataRoot Root { get; }

    internal SqliteConnectionFactory Factory { get; }

    internal SchemaMigrator Migrator { get; }

    /// <summary>The column names of a table, in declaration order.</summary>
    internal IReadOnlyList<string> ColumnsOf(string table)
    {
        using var connection = Factory.Open();
        using var command = connection.CreateCommand();

        // A table name cannot be parameterised in a PRAGMA, so it is validated instead: only the tables
        // this schema declares are ever asked for.
        command.CommandText = $"PRAGMA table_info({table});";

        var columns = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    /// <summary>The names of every table in the database.</summary>
    internal IReadOnlyList<string> Tables()
    {
        using var connection = Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";

        var tables = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    /// <summary>Reads a single scalar, for the pragma read-backs.</summary>
    internal string? Scalar(string sql)
    {
        using var connection = Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }

    public void Dispose()
    {
        // SQLite keeps the file mapped until every pooled handle is gone; pooling is off, but the finalizer
        // queue can still be holding one on a fast test run.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
