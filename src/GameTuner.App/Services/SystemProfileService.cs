using GameTuner.Core.Hardware;
using GameTuner.Core.Performance;
using GameTuner.Core.Settings;

namespace GameTuner.App.Services;

/// <summary>
/// Runs the hardware scan once (on a background thread, WMI is slow) and
/// shares the result with every page.
/// </summary>
public sealed class SystemProfileService
{
    private readonly Lazy<Task<(SystemInfo Info, TierResult Tier)>> _scan;
    private readonly ISettingsService _settings;

    public SystemProfileService(IHardwareScanner scanner, ITierCalculator calculator, ISettingsService settings)
    {
        _settings = settings;
        // Lazy + Task: the scan starts on first request, and every later
        // caller awaits the same task instead of scanning again.
        _scan = new Lazy<Task<(SystemInfo, TierResult)>>(() => Task.Run(() =>
        {
            var info = scanner.Scan();
            return (info, calculator.Calculate(info));
        }));
    }

    public Task<(SystemInfo Info, TierResult Tier)> GetAsync() => _scan.Value;

    /// <summary>The tier to apply: the user's manual choice, or the calculated one.</summary>
    public async Task<PerformanceTier> GetEffectiveTierAsync() =>
        _settings.Current.TierOverride ?? (await GetAsync()).Tier.Tier;
}
