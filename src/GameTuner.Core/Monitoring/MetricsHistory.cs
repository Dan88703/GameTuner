namespace GameTuner.Core.Monitoring;

/// <summary>One point on the performance charts.</summary>
public sealed record MetricPoint
{
    public DateTime Time { get; init; }
    public double CpuLoad { get; init; }
    public double? GpuLoad { get; init; }
    public double RamPercent { get; init; }
    public double? CpuTempC { get; init; }
    public double? GpuTempC { get; init; }
    public double? Fps { get; init; }

    public static MetricPoint From(SensorSnapshot s, double? fps) => new()
    {
        Time = s.Time,
        CpuLoad = s.CpuLoad,
        GpuLoad = s.GpuLoad,
        RamPercent = s.RamPercent,
        CpuTempC = s.CpuTempC,
        GpuTempC = s.GpuTempC,
        Fps = fps
    };

    /// <summary>
    /// Merges several points into one (used to shrink long sessions).
    /// Loads are averaged; for nullable values only the points that HAVE a
    /// value are averaged, so a few missing samples don't drag the result to 0.
    /// The time is taken from the first point of the group.
    /// </summary>
    public static MetricPoint Average(IReadOnlyList<MetricPoint> points)
    {
        if (points.Count == 0) throw new ArgumentException("No points to average", nameof(points));
        if (points.Count == 1) return points[0];

        return new MetricPoint
        {
            Time = points[0].Time,
            CpuLoad = Math.Round(points.Average(p => p.CpuLoad), 1),
            GpuLoad = AverageOrNull(points.Select(p => p.GpuLoad)),
            RamPercent = Math.Round(points.Average(p => p.RamPercent), 1),
            CpuTempC = AverageOrNull(points.Select(p => p.CpuTempC)),
            GpuTempC = AverageOrNull(points.Select(p => p.GpuTempC)),
            Fps = AverageOrNull(points.Select(p => p.Fps))
        };
    }

    private static double? AverageOrNull(IEnumerable<double?> values)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return present.Count > 0 ? Math.Round(present.Average(), 1) : null;
    }
}

public interface IMetricsHistory
{
    /// <summary>How far back the live history goes.</summary>
    TimeSpan Capacity { get; }

    /// <summary>Points from the last <paramref name="window"/> (copy, safe to use on any thread).</summary>
    IReadOnlyList<MetricPoint> Snapshot(TimeSpan window);

    void Start();
}

/// <summary>
/// Live chart data: the last 10 minutes of sensor readings, one point per second.
///
/// It listens to <see cref="ISensorService.Updated"/> (a background thread) and
/// keeps points in a queue: new points go in at the back, points older than
/// <see cref="Capacity"/> fall out at the front. 10 min × 1 point/s = 600 points,
/// so memory use stays tiny no matter how long the app runs.
/// </summary>
public sealed class MetricsHistory : IMetricsHistory, IDisposable
{
    private readonly ISensorService _sensors;
    private readonly IFpsMonitor _fps;
    private readonly Queue<MetricPoint> _points = new();
    private readonly object _lock = new();
    private bool _started;

    public MetricsHistory(ISensorService sensors, IFpsMonitor fps)
    {
        _sensors = sensors;
        _fps = fps;
    }

    public TimeSpan Capacity { get; } = TimeSpan.FromMinutes(10);

    public void Start()
    {
        if (_started) return;
        _started = true;
        _sensors.Updated += OnSensorsUpdated;
    }

    private void OnSensorsUpdated(object? sender, SensorSnapshot snapshot) =>
        Add(MetricPoint.From(snapshot, _fps.Live?.AverageFps));

    internal void Add(MetricPoint point)
    {
        lock (_lock)
        {
            _points.Enqueue(point);
            while (_points.Count > 0 && point.Time - _points.Peek().Time > Capacity)
                _points.Dequeue();
        }
    }

    public IReadOnlyList<MetricPoint> Snapshot(TimeSpan window)
    {
        lock (_lock)
        {
            if (_points.Count == 0) return Array.Empty<MetricPoint>();
            DateTime newest = _points.Last().Time;
            return _points.Where(p => newest - p.Time <= window).ToList();
        }
    }

    public void Dispose() => _sensors.Updated -= OnSensorsUpdated;
}

/// <summary>
/// Builds a compact timeline for a whole game session, however long it is.
///
/// Problem: a 3-hour session at 1 point/s is 10 800 points, and we keep 50
/// sessions in a JSON file. Solution — adaptive bucketing:
///   - readings are grouped into buckets (starting at 5 s) and each bucket
///     becomes ONE averaged point;
///   - when the timeline exceeds <see cref="MaxPoints"/>, neighbouring points
///     are merged in pairs and the bucket size doubles (5 s → 10 s → 20 s …).
/// So a session never stores more than MaxPoints points, short sessions keep
/// full 5-second detail, and long ones get coarser automatically.
/// </summary>
public sealed class TimelineBuilder
{
    public const int MaxPoints = 720;

    private readonly List<MetricPoint> _points = new();
    private readonly List<MetricPoint> _bucket = new();
    private TimeSpan _bucketSize;

    public TimelineBuilder(TimeSpan? initialBucket = null)
    {
        _bucketSize = initialBucket ?? TimeSpan.FromSeconds(5);
    }

    public TimeSpan BucketSize => _bucketSize;

    public void Add(MetricPoint point)
    {
        // Close the current bucket once it covers the bucket size.
        if (_bucket.Count > 0 && point.Time - _bucket[0].Time >= _bucketSize)
        {
            _points.Add(MetricPoint.Average(_bucket));
            _bucket.Clear();

            if (_points.Count > MaxPoints) Compact();
        }
        _bucket.Add(point);
    }

    /// <summary>Halves the number of points by averaging neighbours in pairs.</summary>
    private void Compact()
    {
        var merged = new List<MetricPoint>(_points.Count / 2 + 1);
        for (int i = 0; i < _points.Count; i += 2)
        {
            merged.Add(i + 1 < _points.Count
                ? MetricPoint.Average(new[] { _points[i], _points[i + 1] })
                : _points[i]);
        }
        _points.Clear();
        _points.AddRange(merged);
        _bucketSize *= 2;
    }

    /// <summary>The timeline so far, including the unfinished last bucket.</summary>
    public List<MetricPoint> Build()
    {
        var result = new List<MetricPoint>(_points);
        if (_bucket.Count > 0) result.Add(MetricPoint.Average(_bucket));
        return result;
    }
}
