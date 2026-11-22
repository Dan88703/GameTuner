namespace GameTuner.Core.Monitoring;

/// <summary>FPS summary for a set of frames.</summary>
public sealed record FrameStats(double AverageFps, double OnePercentLowFps, int FrameCount)
{
    /// <summary>
    /// Average FPS = frames / total time = 1000 / mean frame time.
    ///
    /// 1% low = the FPS of the slowest 1% of frames, i.e. 1000 / (99th percentile
    /// frame time). It shows stutters: 144 average with 40 "1% low" feels much
    /// worse than a steady 90.
    /// </summary>
    public static FrameStats? Compute(IReadOnlyCollection<double> frameTimesMs)
    {
        if (frameTimesMs.Count == 0) return null;

        double mean = frameTimesMs.Average();
        var sorted = frameTimesMs.OrderBy(x => x).ToArray();
        int index = (int)Math.Ceiling(sorted.Length * 0.99) - 1;
        double p99 = sorted[Math.Clamp(index, 0, sorted.Length - 1)];

        return new FrameStats(
            mean > 0 ? Math.Round(1000 / mean, 1) : 0,
            p99 > 0 ? Math.Round(1000 / p99, 1) : 0,
            sorted.Length);
    }
}

/// <summary>
/// Collects frame times. Thread-safe: PresentMon output is read on a
/// background thread while the UI reads the current FPS.
/// Keeps the last 2 seconds for the live counter and the whole session
/// for the end-of-session summary.
/// </summary>
public sealed class FrameRecorder
{
    private static readonly TimeSpan LiveWindow = TimeSpan.FromSeconds(2);
    // ~8 hours at 144 FPS; protects memory if a session never ends.
    private const int MaxSessionFrames = 4_000_000;

    private readonly object _lock = new();
    private readonly Queue<(DateTime Time, double Ms)> _recent = new();
    private readonly List<double> _session = new();

    public void Add(double frameTimeMs, DateTime? now = null)
    {
        // Ignore garbage: 0 ms or > 5 s are loading screens / alt-tab, not frames.
        if (frameTimeMs <= 0 || frameTimeMs > 5000) return;
        var t = now ?? DateTime.UtcNow;

        lock (_lock)
        {
            _recent.Enqueue((t, frameTimeMs));
            while (_recent.Count > 0 && t - _recent.Peek().Time > LiveWindow) _recent.Dequeue();
            if (_session.Count < MaxSessionFrames) _session.Add(frameTimeMs);
        }
    }

    public FrameStats? Live()
    {
        lock (_lock) return FrameStats.Compute(_recent.Select(r => r.Ms).ToList());
    }

    public FrameStats? Session()
    {
        lock (_lock) return FrameStats.Compute(_session.ToList());
    }

    public void Reset()
    {
        lock (_lock)
        {
            _recent.Clear();
            _session.Clear();
        }
    }
}
