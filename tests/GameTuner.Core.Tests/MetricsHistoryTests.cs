using GameTuner.Core.Monitoring;

namespace GameTuner.Core.Tests;

public class MetricsHistoryTests
{
    private static readonly DateTime T0 = new(2026, 11, 23, 20, 0, 0);

    private static MetricPoint P(double seconds, double cpu = 10, double? gpuTemp = null, double? fps = null) =>
        new() { Time = T0.AddSeconds(seconds), CpuLoad = cpu, GpuTempC = gpuTemp, Fps = fps };

    [Fact]
    public void Average_IgnoresMissingValues()
    {
        var avg = MetricPoint.Average(new[]
        {
            P(0, cpu: 20, gpuTemp: 60),
            P(1, cpu: 40, gpuTemp: null),   // missing GPU temp must not count as 0
            P(2, cpu: 60, gpuTemp: 80)
        });

        Assert.Equal(40, avg.CpuLoad);
        Assert.Equal(70, avg.GpuTempC);
        Assert.Null(avg.Fps);
        Assert.Equal(T0, avg.Time);
    }

    [Fact]
    public void History_DropsPointsOlderThanCapacity()
    {
        var history = new MetricsHistory(null!, null!);   // Add() doesn't use the dependencies
        for (int s = 0; s <= 15 * 60; s++) history.Add(P(s));

        var all = history.Snapshot(TimeSpan.FromHours(1));
        Assert.True((all[^1].Time - all[0].Time) <= history.Capacity);
        Assert.Equal(601, all.Count);   // 10 minutes of 1-second points, both ends included
    }

    [Fact]
    public void History_SnapshotReturnsRequestedWindow()
    {
        var history = new MetricsHistory(null!, null!);
        for (int s = 0; s < 300; s++) history.Add(P(s));

        Assert.Equal(61, history.Snapshot(TimeSpan.FromMinutes(1)).Count);
    }

    [Fact]
    public void Timeline_ShortSessionKeepsFiveSecondDetail()
    {
        var builder = new TimelineBuilder();
        for (int s = 0; s < 60; s++) builder.Add(P(s));

        var timeline = builder.Build();
        Assert.Equal(12, timeline.Count);                 // 60 s / 5 s buckets
        Assert.Equal(TimeSpan.FromSeconds(5), builder.BucketSize);
    }

    [Fact]
    public void Timeline_LongSessionNeverExceedsMaxPoints()
    {
        var builder = new TimelineBuilder();
        for (int s = 0; s < 4 * 3600; s++) builder.Add(P(s, cpu: s % 100));   // 4 hours

        var timeline = builder.Build();
        Assert.True(timeline.Count <= TimelineBuilder.MaxPoints + 1);
        Assert.True(builder.BucketSize > TimeSpan.FromSeconds(5));
        // Points stay in chronological order after compaction.
        Assert.True(timeline.Zip(timeline.Skip(1)).All(pair => pair.First.Time < pair.Second.Time));
    }
}
