using VisionMesh.Database;
using VisionMesh.Database.Repositories;

namespace VisionMesh.Tests;

/// <summary>
/// A real migrated SQLite database in a temporary file, for tests that exercise repositories.
///
/// A file rather than an in-memory database on purpose: WAL mode, the busy timeout and the
/// connection pooling all behave differently in memory, and those are exactly the settings the
/// server depends on.
/// </summary>
public sealed class DatabaseFixture : IDisposable
{
    private readonly string _path;

    public VisionMeshDatabase Database { get; }
    public CameraRepository Cameras { get; }
    public DeviceRepository Devices { get; }
    public RecordingRepository Recordings { get; }
    public EventRepository Events { get; }
    public SettingsRepository Settings { get; }
    public UserRepository Users { get; }

    public DatabaseFixture()
    {
        _path = Path.Combine(Path.GetTempPath(), "visionmesh-tests", $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        Database = new VisionMeshDatabase(_path);
        Database.Migrate();

        Cameras = new CameraRepository(Database);
        Devices = new DeviceRepository(Database);
        Recordings = new RecordingRepository(Database);
        Events = new EventRepository(Database);
        Settings = new SettingsRepository(Database);
        Users = new UserRepository(Database);
    }

    public void Dispose()
    {
        // SQLite pools connections, so the file stays locked until the pool is cleared.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_path + suffix); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
