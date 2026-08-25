using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace VisionMesh.Database;

/// <summary>
/// Owns the SQLite metadata database: connection creation and schema migration.
///
/// SQLite is used for metadata only - devices, cameras, users, events, recording index.
/// Video never touches the database; recordings are files on disk and the table only
/// stores their paths and time ranges.
/// </summary>
public sealed class VisionMeshDatabase
{
    private readonly string _connectionString;
    private readonly ILogger<VisionMeshDatabase> _log;

    public string DatabasePath { get; }

    public VisionMeshDatabase(string databasePath, ILogger<VisionMeshDatabase>? log = null)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<VisionMeshDatabase>.Instance;

        var dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
        }.ToString();
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        // WAL lets the recorder write while the dashboard reads. busy_timeout stops the
        // occasional concurrent writer from failing outright.
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>Applies every migration that has not been applied yet. Safe to run on every start.</summary>
    public void Migrate()
    {
        using var connection = OpenConnection();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL PRIMARY KEY, applied_utc TEXT NOT NULL);";
            create.ExecuteNonQuery();
        }

        int current;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
            current = Convert.ToInt32(read.ExecuteScalar());
        }

        var migrations = Migrations.Schema.All;
        if (current >= migrations.Count)
        {
            _log.LogDebug("Database schema is up to date at version {Version}.", current);
            return;
        }

        for (var version = current + 1; version <= migrations.Count; version++)
        {
            var sql = migrations[version - 1];
            using var transaction = connection.BeginTransaction();
            using (var apply = connection.CreateCommand())
            {
                apply.Transaction = transaction;
                apply.CommandText = sql;
                apply.ExecuteNonQuery();
            }
            using (var stamp = connection.CreateCommand())
            {
                stamp.Transaction = transaction;
                stamp.CommandText = "INSERT INTO schema_version (version, applied_utc) VALUES ($v, $t);";
                stamp.Parameters.AddWithValue("$v", version);
                stamp.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                stamp.ExecuteNonQuery();
            }
            transaction.Commit();
            _log.LogInformation("Applied database migration {Version}.", version);
        }
    }

    /// <summary>True when the database has no users yet, which is what triggers the first-run wizard.</summary>
    public bool IsFirstRun()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM users;";
        return Convert.ToInt64(command.ExecuteScalar()) == 0;
    }
}
