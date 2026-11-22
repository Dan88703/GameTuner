namespace GameTuner.Core.Performance;

/// <summary>
/// Overall quality level the laptop can handle. Every game profile maps
/// this tier to its own concrete settings.
/// </summary>
public enum PerformanceTier
{
    Low = 0,
    Medium = 1,
    High = 2,
    Ultra = 3
}

/// <summary>
/// Result of the hardware evaluation: the tier plus a human-readable explanation.
/// </summary>
public sealed class TierResult
{
    public PerformanceTier Tier { get; set; }
    public int GpuScore { get; set; }
    public int CpuScore { get; set; }
    public int RamScore { get; set; }
    public List<string> Notes { get; set; } = new();
}
