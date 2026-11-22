using GameTuner.Core.Hardware;
using GameTuner.Core.Performance;

namespace GameTuner.Core.Tests;

public class TierCalculatorTests
{
    private static SystemInfo Pc(string gpu, double vram, double ram, int threads, string cpu = "Generic CPU")
    {
        var info = new SystemInfo { CpuName = cpu, CpuThreads = threads, RamGb = ram };
        info.Gpus.Add(new GpuInfo { Name = gpu, VramGb = vram, IsIntegrated = HardwareScanner.IsIntegratedGpu(gpu) });
        return info;
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090 Laptop GPU", 95)]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", 65)]
    [InlineData("NVIDIA GeForce RTX 3050 Ti Laptop GPU", 42)]
    [InlineData("NVIDIA GeForce RTX 5070 Laptop GPU", 83)]
    [InlineData("NVIDIA GeForce GTX 1650", 35)]
    [InlineData("NVIDIA GeForce MX450", 15)]
    [InlineData("AMD Radeon RX 7600S", 58)]
    [InlineData("AMD Radeon RX 6800M", 78)]
    [InlineData("AMD Radeon 780M Graphics", 30)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", 15)]
    [InlineData("Intel(R) UHD Graphics 620", 8)]
    public void ScoreGpu_KnownModels(string name, int expected)
    {
        var gpu = new GpuInfo { Name = name, IsIntegrated = HardwareScanner.IsIntegratedGpu(name) };
        Assert.Equal(expected, TierCalculator.ScoreGpu(gpu));
    }

    [Theory]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", false)]
    [InlineData("Intel(R) Arc(TM) Graphics", true)]
    [InlineData("AMD Radeon(TM) Graphics", true)]
    [InlineData("AMD Radeon RX 6600M", false)]
    [InlineData("NVIDIA GeForce RTX 4060", false)]
    public void IsIntegratedGpu(string name, bool expected) =>
        Assert.Equal(expected, HardwareScanner.IsIntegratedGpu(name));

    [Fact]
    public void GamingLaptop_IsHigh()
    {
        var result = new TierCalculator().Calculate(Pc("NVIDIA GeForce RTX 4060 Laptop GPU", 8, 15.7, 16));
        Assert.Equal(PerformanceTier.High, result.Tier);
    }

    [Fact]
    public void Flagship_IsUltra()
    {
        var result = new TierCalculator().Calculate(Pc("NVIDIA GeForce RTX 4090 Laptop GPU", 16, 31.8, 32));
        Assert.Equal(PerformanceTier.Ultra, result.Tier);
    }

    [Fact]
    public void OfficeLaptop_IsLow()
    {
        var result = new TierCalculator().Calculate(Pc("Intel(R) UHD Graphics 620", 1, 7.8, 8, "Intel(R) Core(TM) i5-8250U CPU"));
        Assert.Equal(PerformanceTier.Low, result.Tier);
    }

    [Fact]
    public void IntegratedGpu_IsCappedAtMedium()
    {
        // Strong CPU + lots of RAM would score "High", but the iGPU limits it.
        var result = new TierCalculator().Calculate(Pc("AMD Radeon 780M Graphics", 2, 31.3, 16));
        Assert.Equal(PerformanceTier.Medium, result.Tier);
        Assert.Contains(result.Notes, n => n.Contains("встроенная", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LowPowerCpu_GetsPenalty()
    {
        var u = new SystemInfo { CpuName = "Intel(R) Core(TM) i7-1255U", CpuThreads = 12 };
        var h = new SystemInfo { CpuName = "Intel(R) Core(TM) i7-12700H", CpuThreads = 12 };
        Assert.True(TierCalculator.ScoreCpu(u) < TierCalculator.ScoreCpu(h));
    }
}
