using GameTuner.Core.Monitoring;

namespace GameTuner.Core.Tests;

public class MonitoringTests
{
    [Fact]
    public void Parser_WaitsForHeader_ThenReadsFrameTimeColumn()
    {
        var parser = new PresentMonCsvParser();

        Assert.False(parser.TryParse("Started recording...", out _));
        Assert.False(parser.TryParse("Application,ProcessID,SwapChainAddress,MsBetweenPresents,MsInPresentAPI", out _));
        Assert.True(parser.HasHeader);

        Assert.True(parser.TryParse("cs2.exe,1234,0x1,6.944,0.1", out double ms));
        Assert.Equal(6.944, ms, 3);
    }

    [Fact]
    public void Parser_SupportsPresentMon2FrameTimeColumn()
    {
        var parser = new PresentMonCsvParser();
        parser.TryParse("Application,ProcessID,CPUStartTime,FrameTime,CPUBusy", out _);
        Assert.True(parser.TryParse("game.exe,42,1.0,16.667,3.2", out double ms));
        Assert.Equal(16.667, ms, 3);
    }

    [Fact]
    public void FrameStats_AverageAndOnePercentLow()
    {
        // 99 smooth frames at 10 ms (100 FPS) and one 50 ms stutter.
        var frames = Enumerable.Repeat(10.0, 99).Append(50.0).ToList();
        var stats = FrameStats.Compute(frames)!;

        Assert.Equal(1000 / 10.4, stats.AverageFps, 1);   // mean = 10.4 ms
        Assert.Equal(100, stats.OnePercentLowFps, 1);      // p99 is still a 10 ms frame
        Assert.Equal(100, stats.FrameCount);
    }

    [Fact]
    public void FrameStats_StuttersShowInOnePercentLow()
    {
        // 95 frames at 10 ms + 5 frames at 40 ms: the 1% low drops to 25 FPS.
        var frames = Enumerable.Repeat(10.0, 95).Concat(Enumerable.Repeat(40.0, 5)).ToList();
        Assert.Equal(25, FrameStats.Compute(frames)!.OnePercentLowFps, 1);
    }

    [Fact]
    public void FrameRecorder_LiveWindowDropsOldFrames()
    {
        var rec = new FrameRecorder();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        rec.Add(100, t0);                      // 10 FPS, old
        rec.Add(10, t0.AddSeconds(5));         // 100 FPS, recent
        rec.Add(10, t0.AddSeconds(5.01));

        Assert.Equal(2, rec.Live()!.FrameCount);
        Assert.Equal(3, rec.Session()!.FrameCount);
    }

    [Fact]
    public void FrameRecorder_IgnoresGarbage()
    {
        var rec = new FrameRecorder();
        rec.Add(0);
        rec.Add(-5);
        rec.Add(60_000);
        Assert.Null(rec.Session());
    }

    [Fact]
    public void SensorMerge_PrefersDetailedValues()
    {
        var basic = new SensorSnapshot { CpuLoad = 30, GpuLoad = 50, RamPercent = 60 };
        var detailed = new SensorSnapshot { CpuLoad = 32, CpuTempC = 85, GpuLoad = null, GpuTempC = 70 };

        var merged = SensorService.Merge(basic, detailed);

        Assert.Equal(32, merged.CpuLoad);
        Assert.Equal(85, merged.CpuTempC);
        Assert.Equal(50, merged.GpuLoad);   // LHM had no GPU load → basic value kept
        Assert.Equal(60, merged.RamPercent);
        Assert.True(merged.IsDetailed);
    }

    [Theory]
    [InlineData("pid_1234_luid_0x00000000_0x0000D1B5_phys_0_eng_0_engtype_3D", "luid_0x00000000_0x0000D1B5")]
    [InlineData("garbage", "unknown")]
    public void GpuCounter_ExtractsLuid(string instance, string expected) =>
        Assert.Equal(expected, GpuEngineCounter.ExtractLuid(instance));
}
