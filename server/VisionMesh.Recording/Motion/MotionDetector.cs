namespace VisionMesh.Recording.Motion;

/// <summary>Outcome of comparing one frame against the previous one.</summary>
/// <param name="Evaluated">False when the frame could not be decoded and no decision was made.</param>
/// <param name="ChangedRatio">Fraction of the low-resolution frame whose brightness changed, 0-1.</param>
/// <param name="Motion">True when the change exceeded the configured threshold.</param>
public readonly record struct MotionResult(bool Evaluated, double ChangedRatio, bool Motion)
{
    public static readonly MotionResult NotEvaluated = new(false, 0, false);
}

/// <summary>
/// Frame-difference motion detection over 1/8-scale luma.
///
/// The approach is deliberately simple - compare block brightness against the previous frame,
/// count how much changed - because that is what actually works on surveillance footage without
/// a model, a GPU, or per-camera tuning. Two guards keep it from crying wolf:
///
///  * a global-shift rejection, so a light switching on or a camera auto-exposing does not read
///    as motion across the whole frame;
///  * a consecutive-frame requirement, so sensor noise in one frame cannot trigger a recording.
///
/// This is intentionally not object or person detection. Calling a bright patch "a person"
/// without a model behind it would be a lie the events list would then repeat forever.
/// </summary>
public sealed class MotionDetector(int sensitivity = 50)
{
    /// <summary>Per-cell brightness change that counts as "this cell moved", derived from sensitivity.</summary>
    private readonly int _cellThreshold = MapSensitivityToCellThreshold(sensitivity);
    /// <summary>Fraction of changed cells needed to call it motion, derived from sensitivity.</summary>
    private readonly double _areaThreshold = MapSensitivityToArea(sensitivity);

    private byte[]? _previous;
    private int _previousWidth;
    private int _previousHeight;
    private int _consecutive;

    /// <summary>How many consecutive frames must show change before motion is reported.</summary>
    public int RequiredConsecutiveFrames { get; init; } = 2;

    /// <summary>Last computed change ratio, exposed so the UI can show a live sensitivity meter.</summary>
    public double LastChangedRatio { get; private set; }

    public MotionResult Evaluate(ReadOnlySpan<byte> jpeg)
    {
        var thumbnail = JpegDcDecoder.TryDecodeLuma(jpeg);
        if (thumbnail is null) return MotionResult.NotEvaluated;
        return Evaluate(thumbnail);
    }

    public MotionResult Evaluate(LumaThumbnail thumbnail)
    {
        var current = thumbnail.Pixels;

        // A resolution change invalidates the comparison; treat it as a fresh start.
        if (_previous is null || _previousWidth != thumbnail.Width || _previousHeight != thumbnail.Height)
        {
            _previous = current;
            _previousWidth = thumbnail.Width;
            _previousHeight = thumbnail.Height;
            _consecutive = 0;
            return MotionResult.NotEvaluated;
        }

        var previous = _previous;
        var length = Math.Min(current.Length, previous.Length);
        if (length == 0) return MotionResult.NotEvaluated;

        // Mean signed difference approximates a global brightness shift (auto-exposure, a light
        // turning on). Subtracting it means only genuinely local change is counted.
        var sum = 0L;
        for (var i = 0; i < length; i++) sum += current[i] - previous[i];
        var meanShift = (int)Math.Round((double)sum / length);

        var changed = 0;
        for (var i = 0; i < length; i++)
        {
            var difference = Math.Abs(current[i] - previous[i] - meanShift);
            if (difference > _cellThreshold) changed++;
        }

        var ratio = (double)changed / length;
        LastChangedRatio = ratio;

        _previous = current;

        if (ratio >= _areaThreshold)
        {
            _consecutive++;
            if (_consecutive >= RequiredConsecutiveFrames) return new MotionResult(true, ratio, true);
        }
        else
        {
            _consecutive = 0;
        }

        return new MotionResult(true, ratio, false);
    }

    /// <summary>Forgets history, so a camera that reconnects does not compare against a stale frame.</summary>
    public void Reset()
    {
        _previous = null;
        _consecutive = 0;
        LastChangedRatio = 0;
    }

    /// <summary>
    /// Sensitivity 1-100 maps to a per-cell brightness threshold. High sensitivity means a small
    /// brightness change is enough; low sensitivity demands an obvious one.
    /// </summary>
    private static int MapSensitivityToCellThreshold(int sensitivity)
    {
        var clamped = Math.Clamp(sensitivity, 1, 100);
        return (int)Math.Round(40 - (clamped * 32.0 / 100.0));   // 39 at 1, 8 at 100
    }

    /// <summary>Sensitivity also lowers how much of the frame must change, from 8% down to 0.5%.</summary>
    private static double MapSensitivityToArea(int sensitivity)
    {
        var clamped = Math.Clamp(sensitivity, 1, 100);
        return 0.08 - (clamped * 0.075 / 100.0);
    }
}
