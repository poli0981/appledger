using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace AppLedger.Infrastructure.Storage;

/// <summary>
/// Brings the database up to the current schema version. Forward-only, one transaction per script, with a
/// file-copy backup before any upgrade of a database that already holds data (docs/06_DATA_MODEL.md).
/// </summary>
/// <remarks>
/// The scripts are embedded resources rather than files on disk: the install folder is user-writable
/// (docs/11_SAFETY_POLICY.md §Privilege boundary), and an elevated process that reads its migration SQL
/// from a writable location is a much larger hole than the schema is worth.
/// </remarks>
public sealed class SchemaMigrator
{
    /// <summary>The schema version this build understands.</summary>
    public const int CurrentVersion = 1;

    private const string SchemaVersionKey = "schema_version";
    private const string ResourcePrefix = "AppLedger.Infrastructure.Storage.Migrations.";

    private readonly SqliteConnectionFactory _factory;
    private readonly DataRoot _dataRoot;

    /// <summary>Creates a migrator over one database.</summary>
    public SchemaMigrator(SqliteConnectionFactory factory, DataRoot dataRoot)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(dataRoot);
        _factory = factory;
        _dataRoot = dataRoot;
    }

    /// <summary>
    /// Applies every migration the database is missing and returns the version it ends on. A database
    /// already at <see cref="CurrentVersion"/> is untouched, so calling this at every start is free.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The database was written by a newer build. Downgrading a schema silently would corrupt history, so
    /// this refuses rather than guesses.
    /// </exception>
    public int Migrate()
    {
        _dataRoot.EnsureCreated();

        using var connection = _factory.Open();
        var current = ReadVersion(connection);

        if (current > CurrentVersion)
        {
            throw new InvalidOperationException(
                $"The database is at schema {current}, newer than this build understands ({CurrentVersion}).");
        }

        if (current == CurrentVersion)
        {
            return current;
        }

        // Nothing to back up on a first run, and a backup of an empty file would just be noise in the
        // data root. Any later upgrade gets one.
        if (current > 0)
        {
            Backup(connection, current);
        }

        for (var version = current + 1; version <= CurrentVersion; version++)
        {
            Apply(connection, version);
        }

        return CurrentVersion;
    }

    /// <summary>The version recorded in <c>meta</c>, or 0 when the database is empty.</summary>
    public static int ReadVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT value FROM meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", SchemaVersionKey);

        try
        {
            var value = command.ExecuteScalar() as string;
            return value is not null && int.TryParse(value, CultureInfo.InvariantCulture, out var version) ? version : 0;
        }
        catch (SqliteException)
        {
            // No meta table yet: this is a brand-new file.
            return 0;
        }
    }

    private void Backup(SqliteConnection connection, int fromVersion)
    {
        // Checkpoint first: without it the backup copy would miss everything still sitting in the WAL.
        using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }

        var backupPath = string.Create(
            CultureInfo.InvariantCulture,
            $"{_dataRoot.DatabasePath}.bak-{fromVersion}");

        File.Copy(_dataRoot.DatabasePath, backupPath, overwrite: true);
    }

    private static void Apply(SqliteConnection connection, int version)
    {
        var sql = ReadScript(version);

        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        using (var stamp = connection.CreateCommand())
        {
            stamp.Transaction = transaction;
            stamp.CommandText = "INSERT INTO meta(key, value) VALUES($key, $value) "
                + "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            stamp.Parameters.AddWithValue("$key", SchemaVersionKey);
            stamp.Parameters.AddWithValue("$value", version.ToString(CultureInfo.InvariantCulture));
            stamp.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static string ReadScript(int version)
    {
        var name = string.Create(CultureInfo.InvariantCulture, $"{ResourcePrefix}{version:D4}_initial.sql");
        var assembly = typeof(SchemaMigrator).GetTypeInfo().Assembly;

        // A later migration will not be called "_initial", so fall back to a prefix match on the number.
        var resourceName = assembly.GetManifestResourceNames().Contains(name)
            ? name
            : Array.Find(
                assembly.GetManifestResourceNames(),
                n => n.StartsWith(
                    string.Create(CultureInfo.InvariantCulture, $"{ResourcePrefix}{version:D4}_"),
                    StringComparison.Ordinal));

        if (resourceName is null)
        {
            throw new InvalidOperationException($"Migration script {version:D4} is not embedded in this build.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Migration script '{resourceName}' could not be opened.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
