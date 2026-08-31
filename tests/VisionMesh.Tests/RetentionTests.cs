using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisionMesh.Api;
using VisionMesh.Core.Models;
using VisionMesh.Core.Util;
using VisionMesh.Database;
using VisionMesh.Database.Repositories;
using VisionMesh.Recording;
using VisionMesh.Streaming.Sources;
using Xunit;

namespace VisionMesh.Tests;

/// <summary>
/// Deleting recordings.
///
/// This is the only code in VisionMesh that destroys something a user cannot get back, and it ran
/// untested: an off-by-one on the age check, a wrong path join, or a row deleted without its file
/// all fail silently and are only noticed when footage someone wanted is gone.
///
/// The recordings here are real files in a temporary folder, so a test that claims a file was
/// deleted means the file is actually gone from disk.
/// </summary>
public sealed class RetentionTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly string _dataDirectory;
    private readonly string _recordingsRoot;

    private readonly CameraRepository _cameras;
    private readonly RecordingRepository _recordings;
    private readonly SettingsRepository _settings;
    private readonly EventRepository _events;
    private readonly RecordingIndexer _indexer;

    public RetentionTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "visionmesh-tests", Guid.NewGuid().ToString("N"));
        _recordingsRoot = Path.Combine(_dataDirectory, "recordings");
        Directory.CreateDirectory(_recordingsRoot);

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders());
        services.AddVisionMesh(_dataDirectory);
        _provider = services.BuildServiceProvider();

        _provider.GetRequiredService<VisionMeshDatabase>().Migrate();

        _cameras = _provider.GetRequiredService<CameraRepository>();
        _recordings = _provider.GetRequiredService<RecordingRepository>();
        _settings = _provider.GetRequiredService<SettingsRepository>();
        _events = _provider.GetRequiredService<EventRepository>();
        _indexer = _provider.GetRequiredService<RecordingIndexer>();

        _settings.Set(SettingsRepository.Keys.RecordingsPath, _recordingsRoot);
    }

    // ---- helpers ----

    private Camera AddCamera(int retentionDays = 0, string name = "Front Door")
    {
        var camera = new Camera
        {
            Id = Ids.NewId("cam"),
            Name = name,
            SourceKind = CameraSourceKind.AgentCamera,
            Enabled = true,
            RetentionDays = retentionDays,
        };
        _cameras.Insert(camera);
        return camera;
    }

    /// <summary>Writes a real file on disk and indexes it, as a finished recording would be.</summary>
    private RecordingSegment AddRecording(Camera camera, TimeSpan age, long sizeBytes = 1024)
    {
        var start = DateTimeOffset.UtcNow - age;
        var directory = Path.Combine(_recordingsRoot, camera.Id);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, RecordingPlan.BuildFileName(start.ToLocalTime()));
        File.WriteAllBytes(path, new byte[sizeBytes]);

        var segment = new RecordingSegment
        {
            CameraId = camera.Id,
            FilePath = path,
            StartUtc = start,
            EndUtc = start.AddMinutes(10),
            SizeBytes = sizeBytes,
            Trigger = RecordingTrigger.Continuous,
            Closed = true,
        };
        segment.Id = _recordings.Insert(segment);
        return segment;
    }

    private bool IsIndexed(RecordingSegment segment) => _recordings.GetById(segment.Id) is not null;

    private int CountStorageWarnings() =>
        _events.Count(null, EventType.StorageWarning, null, null);

    // ---- retention window ----

    [Fact]
    public void ARecordingPastItsRetentionWindowIsDeletedFromDiskAndFromTheIndex()
    {
        _settings.SetInt(SettingsRepository.Keys.RetentionDays, 7);
        var camera = AddCamera();
        var old = AddRecording(camera, TimeSpan.FromDays(9));

        _indexer.RunMaintenancePass();

        Assert.False(File.Exists(old.FilePath), "The file should be gone from disk.");
        Assert.False(IsIndexed(old), "The index must not keep claiming a recording exists after its file is deleted.");
    }

    [Fact]
    public void ARecordingInsideItsRetentionWindowIsLeftAlone()
    {
        _settings.SetInt(SettingsRepository.Keys.RetentionDays, 7);
        var camera = AddCamera();
        var recent = AddRecording(camera, TimeSpan.FromDays(2));

        _indexer.RunMaintenancePass();

        Assert.True(File.Exists(recent.FilePath), "Footage inside the retention window must survive.");
        Assert.True(IsIndexed(recent));
    }

    [Fact]
    public void ARecordingOnTheEdgeOfTheWindowIsKept()
    {
        // The boundary is where an off-by-one costs someone the oldest day of footage they asked
        // to keep. Just inside seven days must survive a seven day window.
        _settings.SetInt(SettingsRepository.Keys.RetentionDays, 7);
        var camera = AddCamera();
        var edge = AddRecording(camera, TimeSpan.FromDays(7) - TimeSpan.FromMinutes(5));

        _indexer.RunMaintenancePass();

        Assert.True(File.Exists(edge.FilePath), "A recording just inside the window must not be deleted.");
    }

    [Fact]
    public void KeepForeverDeletesNothing()
    {
        // Zero is a documented choice meaning "keep until the disk fills up". Treating it as
        // "keep for zero days" would delete the entire archive on the next pass.
        _settings.SetInt(SettingsRepository.Keys.RetentionDays, 0);
        var camera = AddCamera();
        var ancient = AddRecording(camera, TimeSpan.FromDays(400));

        _indexer.RunMaintenancePass();

        Assert.True(File.Exists(ancient.FilePath), "Retention of 0 means keep forever.");
        Assert.True(IsIndexed(ancient));
    }

    [Fact]
    public void ACameraWithItsOwnRetentionOverridesTheGlobalOne()
    {
        _settings.SetInt(SettingsRepository.Keys.RetentionDays, 30);

        var shortLived = AddCamera(retentionDays: 1, name: "Doorbell");
        var followsGlobal = AddCamera(retentionDays: 0, name: "Garage");

        var deleted = AddRecording(shortLived, TimeSpan.FromDays(3));
        var kept = AddRecording(followsGlobal, TimeSpan.FromDays(3));

        _indexer.RunMaintenancePass();

        Assert.False(File.Exists(deleted.FilePath), "The camera's own 1 day window should have removed this.");
        Assert.True(File.Exists(kept.FilePath), "A camera with no override follows the 30 day global window.");
    }

    [Fact]
    public void OneCamerasRetentionNeverTouchesAnothersFootage()
    {
        _settings.SetInt(SettingsRepository.Keys.RetentionDays, 1);

        var expiring = AddCamera(name: "Front Door");
        var other = AddCamera(name: "Garage");

        var gone = AddRecording(expiring, TimeSpan.FromDays(5));
        var survives = AddRecording(other, TimeSpan.FromMinutes(30));

        _indexer.RunMaintenancePass();

        Assert.False(File.Exists(gone.FilePath));
        Assert.True(File.Exists(survives.FilePath), "Deleting one camera's old footage must not reach another camera.");
    }

    [Fact]
    public void ARowWhoseFileHasAlreadyGoneIsDroppedFromTheIndex()
    {
        // Someone deleting footage from a file manager is expected: the filesystem is the source
        // of truth. What must not happen is the recordings list going on offering a file that
        // will 404 when played.
        _settings.SetInt(SettingsRepository.Keys.RetentionDays, 7);
        var camera = AddCamera();
        var orphan = AddRecording(camera, TimeSpan.FromDays(9));
        File.Delete(orphan.FilePath);

        _indexer.RunMaintenancePass();

        Assert.False(IsIndexed(orphan));
    }

    // ---- storage cap ----

    [Fact]
    public void TheStorageCapDeletesTheOldestFirstAndSaysSo()
    {
        // The cap deletes footage that is still inside its retention window, which is the one case
        // where VisionMesh removes something the user explicitly asked to keep. Doing that quietly
        // would be indistinguishable from losing it.
        _settings.SetInt(SettingsRepository.Keys.RetentionDays, 365);
        _settings.SetInt(SettingsRepository.Keys.StorageLimitGb, 1);

        var camera = AddCamera();
        const long halfAGigabyte = 512L * 1024 * 1024;

        var oldest = AddRecording(camera, TimeSpan.FromDays(3), sizeBytes: 1024);
        var middle = AddRecording(camera, TimeSpan.FromDays(2), sizeBytes: 1024);
        var newest = AddRecording(camera, TimeSpan.FromDays(1), sizeBytes: 1024);

        // Claim sizes far larger than the bytes actually written, so the cap is exceeded without
        // putting a gigabyte of zeroes on the build agent's disk.
        foreach (var segment in new[] { oldest, middle, newest })
        {
            _recordings.Close(segment.Id, segment.EndUtc!.Value, halfAGigabyte);
        }

        var before = CountStorageWarnings();
        _indexer.RunMaintenancePass();

        Assert.False(File.Exists(oldest.FilePath), "The cap removes the oldest footage first.");
        Assert.True(File.Exists(newest.FilePath), "The newest footage must be the last to go.");

        Assert.True(CountStorageWarnings() > before,
            "Deleting footage to stay under the cap must raise an event, not remove it quietly.");
    }

    [Fact]
    public void NoStorageCapMeansNothingIsDeletedForSpace()
    {
        _settings.SetInt(SettingsRepository.Keys.RetentionDays, 365);
        _settings.SetInt(SettingsRepository.Keys.StorageLimitGb, 0);

        var camera = AddCamera();
        var segment = AddRecording(camera, TimeSpan.FromDays(10), sizeBytes: 4096);
        _recordings.Close(segment.Id, segment.EndUtc!.Value, 900L * 1024 * 1024 * 1024);

        _indexer.RunMaintenancePass();

        Assert.True(File.Exists(segment.FilePath), "A cap of 0 means no cap at all.");
    }

    // ---- the pass as a whole ----

    [Fact]
    public void AMaintenancePassOnAnEmptyInstallationDoesNothingAndDoesNotThrow()
    {
        _settings.SetInt(SettingsRepository.Keys.RetentionDays, 7);

        _indexer.RunMaintenancePass();
        _indexer.RunMaintenancePass();

        Assert.Empty(Directory.GetFiles(_recordingsRoot, "*.mp4", SearchOption.AllDirectories));
    }

    [Fact]
    public void RunningThePassTwiceDeletesNothingExtra()
    {
        _settings.SetInt(SettingsRepository.Keys.RetentionDays, 7);
        var camera = AddCamera();
        var kept = AddRecording(camera, TimeSpan.FromDays(1));
        AddRecording(camera, TimeSpan.FromDays(30));

        _indexer.RunMaintenancePass();
        _indexer.RunMaintenancePass();

        Assert.True(File.Exists(kept.FilePath));
        Assert.Single(Directory.GetFiles(Path.Combine(_recordingsRoot, camera.Id)));
    }

    public void Dispose()
    {
        _provider.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
