using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using VisionMesh.Core.Models;
using VisionMesh.Core.Util;
using VisionMesh.Database;
using VisionMesh.Database.Migrations;
using VisionMesh.Database.Repositories;
using Xunit;

namespace VisionMesh.Tests;

/// <summary>
/// Upgrading an existing database.
///
/// Every user who updates VisionMesh runs this code against a database full of their own data,
/// and it had no test at all: <c>DatabaseFixture</c> only ever creates an empty one.
///
/// The invariant worth defending hardest is that migrations are append-only. Editing an applied
/// migration is silent and asymmetric: a fresh install gets the edited schema, an existing install
/// has already stamped that version and never re-runs it, so the two diverge and only the second
/// one starts failing — usually much later, on a query that mentions a column that is there for
/// some people and not for others.
/// </summary>
public class DatabaseMigrationTests
{
    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), "visionmesh-tests", $"{Guid.NewGuid():N}.db");

    private static void Cleanup(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(path + suffix); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Checksum(string sql)
    {
        // Line endings differ between a checkout on Windows and one on Linux, and that is not a
        // schema change. Normalise before hashing so this fails for real edits only.
        var normalised = sql.Replace("\r\n", "\n").Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)))[..16];
    }

    /// <summary>
    /// Fingerprints of every migration that has shipped. Append to this list when you add a
    /// migration; never edit an existing entry to make this pass.
    /// </summary>
    private static readonly string[] ShippedChecksums =
    [
        "F0D4EC7E9584D480",
    ];

    [Fact]
    public void AnAlreadyShippedMigrationIsNeverEdited()
    {
        Assert.True(
            Schema.All.Count >= ShippedChecksums.Length,
            "A migration has been removed. Migrations are append-only: every database in the "
            + "world has already recorded which versions it applied.");

        for (var index = 0; index < ShippedChecksums.Length; index++)
        {
            Assert.Equal(ShippedChecksums[index], Checksum(Schema.All[index]));
        }
    }

    [Fact]
    public void ANewMigrationIsAddedRatherThanFoldedIntoAnOldOne()
    {
        // The counterpart to the check above: adding a migration is fine and expected, but the
        // new checksum has to be recorded here so the next edit to it is caught.
        Assert.Equal(ShippedChecksums.Length, Schema.All.Count);
    }

    [Fact]
    public void MigratingTwiceAppliesNothingTheSecondTime()
    {
        var path = NewDatabasePath();
        try
        {
            var database = new VisionMeshDatabase(path);
            database.Migrate();
            var afterFirst = AppliedVersions(database);

            database.Migrate();

            Assert.Equal(afterFirst, AppliedVersions(database));
            Assert.Equal(Schema.All.Count, afterFirst.Count);
            Assert.Equal(Enumerable.Range(1, Schema.All.Count), afterFirst);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ReopeningADatabaseKeepsEverythingInIt()
    {
        // The upgrade path in miniature: data written by one run of VisionMesh has to be intact
        // after the next start-up runs Migrate over it.
        var path = NewDatabasePath();
        try
        {
            string cameraId;
            {
                var database = new VisionMeshDatabase(path);
                database.Migrate();

                var cameras = new CameraRepository(database);
                var users = new UserRepository(database);
                var settings = new SettingsRepository(database);

                cameraId = Ids.NewId("cam");
                cameras.Insert(new Camera
                {
                    Id = cameraId,
                    Name = "Front Door",
                    SourceKind = CameraSourceKind.Rtsp,
                    Enabled = true,
                    RetentionDays = 14,
                });
                users.Insert(new User
                {
                    Id = Ids.NewId("usr"),
                    Username = "admin",
                    PasswordHash = PasswordHasher.Hash("correct-horse-battery"),
                    Role = UserRole.Administrator,
                });
                settings.Set(SettingsRepository.Keys.RetentionDays, "14");
            }

            SqliteConnection.ClearAllPools();

            {
                // A second VisionMeshDatabase over the same file is exactly what the next start-up
                // does.
                var database = new VisionMeshDatabase(path);
                database.Migrate();

                var camera = new CameraRepository(database).GetById(cameraId);
                Assert.NotNull(camera);
                Assert.Equal("Front Door", camera!.Name);
                Assert.Equal(14, camera.RetentionDays);
                Assert.Equal(CameraSourceKind.Rtsp, camera.SourceKind);

                Assert.Single(new UserRepository(database).GetAll());
                Assert.Equal("14", new SettingsRepository(database).Get(SettingsRepository.Keys.RetentionDays));
                Assert.False(database.IsFirstRun(), "A database with a user in it is not a first run.");
            }
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ADatabaseWrittenByANewerVersionIsLeftAlone()
    {
        // Downgrading is not supported, but it must not be destructive: someone who tries an
        // older build against a newer database should get an error later, not a schema quietly
        // rebuilt underneath their data.
        var path = NewDatabasePath();
        try
        {
            var database = new VisionMeshDatabase(path);
            database.Migrate();

            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "INSERT INTO schema_version (version, applied_utc) VALUES ($v, $t);";
                command.Parameters.AddWithValue("$v", Schema.All.Count + 5);
                command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                command.ExecuteNonQuery();
            }

            var before = AppliedVersions(database);
            database.Migrate();

            Assert.Equal(before, AppliedVersions(database));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void EverySchemaVersionIsStampedExactlyOnce()
    {
        var path = NewDatabasePath();
        try
        {
            var database = new VisionMeshDatabase(path);
            database.Migrate();
            database.Migrate();

            var versions = AppliedVersions(database);
            Assert.Equal(versions.Distinct().Count(), versions.Count);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void AFreshDatabaseIsAFirstRun()
    {
        var path = NewDatabasePath();
        try
        {
            var database = new VisionMeshDatabase(path);
            database.Migrate();
            Assert.True(database.IsFirstRun());
        }
        finally { Cleanup(path); }
    }

    private static List<int> AppliedVersions(VisionMeshDatabase database)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_version ORDER BY version;";
        using var reader = command.ExecuteReader();

        var versions = new List<int>();
        while (reader.Read()) versions.Add(reader.GetInt32(0));
        return versions;
    }
}
