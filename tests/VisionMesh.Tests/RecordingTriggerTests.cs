using VisionMesh.Core.Models;
using Xunit;

namespace VisionMesh.Tests;

/// <summary>
/// The recording trigger has to survive the round trip from "why we started" to "what the
/// timeline shows".
///
/// This exists because of a real bug found by recording a live camera: the indexer labelled every
/// segment Continuous, because an ffmpeg segment file carries nothing that says what caused it.
/// A motion clip therefore appeared in the recordings list and on the timeline as continuous
/// footage, which is precisely the sort of confidently-wrong detail this project refuses to show.
/// </summary>
public class RecordingTriggerTests
{
    [Fact]
    public void RecordingsRoundTripTheirTriggerThroughTheDatabase()
    {
        using var fixture = new DatabaseFixture();
        var recordings = fixture.Recordings;

        var start = DateTimeOffset.UtcNow;
        var triggers = new[]
        {
            RecordingTrigger.Continuous,
            RecordingTrigger.Motion,
            RecordingTrigger.Manual,
            RecordingTrigger.Schedule,
            RecordingTrigger.Event,
        };

        foreach (var (trigger, index) in triggers.Select((t, i) => (t, i)))
        {
            recordings.Insert(new RecordingSegment
            {
                CameraId = "cam_test",
                FilePath = $"/recordings/cam_test/segment-{index}.mp4",
                StartUtc = start.AddMinutes(index),
                EndUtc = start.AddMinutes(index + 1),
                SizeBytes = 1024 * (index + 1),
                Trigger = trigger,
                Closed = true,
            });
        }

        var stored = recordings.Query("cam_test", null, null, 100, 0)
            .OrderBy(segment => segment.StartUtc)
            .ToList();

        Assert.Equal(triggers.Length, stored.Count);
        Assert.Equal(triggers, stored.Select(segment => segment.Trigger));
    }

    [Fact]
    public void ATriggerThatIsNotContinuousSurvivesBeingReadBack()
    {
        // The specific regression: a motion recording must not come back as Continuous.
        using var fixture = new DatabaseFixture();

        var id = fixture.Recordings.Insert(new RecordingSegment
        {
            CameraId = "cam_motion",
            FilePath = "/recordings/cam_motion/20260831-102926.mp4",
            StartUtc = DateTimeOffset.UtcNow,
            SizeBytes = 4_388_527,
            Trigger = RecordingTrigger.Motion,
            Closed = true,
        });

        var stored = fixture.Recordings.GetById(id);

        Assert.NotNull(stored);
        Assert.Equal(RecordingTrigger.Motion, stored!.Trigger);
        Assert.NotEqual(RecordingTrigger.Continuous, stored.Trigger);
    }

    [Fact]
    public void ClosingASegmentKeepsItsTrigger()
    {
        // Closing writes the end time and size. It must not disturb why the recording happened.
        using var fixture = new DatabaseFixture();

        var start = DateTimeOffset.UtcNow;
        var id = fixture.Recordings.Insert(new RecordingSegment
        {
            CameraId = "cam_open",
            FilePath = "/recordings/cam_open/open.mp4",
            StartUtc = start,
            Trigger = RecordingTrigger.Manual,
            Closed = false,
        });

        fixture.Recordings.Close(id, start.AddMinutes(3), 2_048_000);

        var stored = fixture.Recordings.GetById(id);
        Assert.NotNull(stored);
        Assert.Equal(RecordingTrigger.Manual, stored!.Trigger);
        Assert.True(stored.Closed);
        Assert.Equal(2_048_000, stored.SizeBytes);
    }
}
