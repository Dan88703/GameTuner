using System.Diagnostics;
using System.Text.RegularExpressions;
using GameTuner.Core.Automation;
using GameTuner.Core.Interop;
using GameTuner.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTuner.Core.Tuning;

/// <summary>When the quiet mode is allowed to run.</summary>
public enum QuietModePolicy
{
    Off = 0,
    OnBattery = 1,
    Always = 2
}

/// <summary>Thin wrapper around powercfg.exe, shared by the power features.</summary>
internal static class Powercfg
{
    // Aliases understood by powercfg, so we don't need the long GUIDs.
    public const string ProcessorSubgroup = "SUB_PROCESSOR";
    public const string BoostSetting = "PERFBOOSTMODE";   // 0 = disabled, 1 = enabled, 2 = aggressive, ...

    private static readonly Regex GuidPattern =
        new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    private static readonly Regex HexIndex = new(@"0x([0-9a-fA-F]{8})", RegexOptions.Compiled);

    public static bool Run(string args, out string output)
    {
        output = "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p == null) return false;
            output = p.StandardOutput.ReadToEnd();
            return p.WaitForExit(5000) && p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string? ActiveSchemeGuid() =>
        Run("/getactivescheme", out string output) && GuidPattern.Match(output) is { Success: true } m ? m.Value : null;

    /// <summary>
    /// Parses the output of "powercfg /query SCHEME SUB_PROCESSOR PERFBOOSTMODE".
    /// The text is localized ("Current AC Power Setting Index" / "Текущий индекс
    /// параметра питания от сети…"), so we don't match words. The only hex
    /// numbers in that output are the two current values, always AC first, then DC.
    /// </summary>
    internal static (int Ac, int Dc)? ParseCurrentIndexes(string queryOutput)
    {
        var matches = HexIndex.Matches(queryOutput);
        if (matches.Count < 2) return null;
        int ac = Convert.ToInt32(matches[^2].Groups[1].Value, 16);
        int dc = Convert.ToInt32(matches[^1].Groups[1].Value, 16);
        return (ac, dc);
    }
}

public interface IQuietModeController : IDisposable
{
    /// <summary>True while turbo boost is limited by GameTuner.</summary>
    bool IsActive { get; }

    /// <summary>Short human-readable state for the UI.</summary>
    string Status { get; }

    /// <summary>True while the user asked for full power by hand.</summary>
    bool IsPaused { get; }

    event EventHandler? StateChanged;

    void Start();

    /// <summary>Checks the current situation (game? exception app? settings?) and switches if needed.</summary>
    void Reevaluate();

    /// <summary>"Full power for now": disables quiet mode until <see cref="Resume"/>.</summary>
    void Pause();

    void Resume();
}

/// <summary>
/// "Quiet mode": outside games the CPU runs without turbo boost, so the laptop
/// stays cooler, quieter and lasts longer on battery.
///
/// Why turbo boost: in normal work the CPU often jumps to its max boost clock
/// for a few milliseconds of work (opening a tab, a background update). Those
/// spikes produce most of the heat and fan noise. Without boost a Ryzen 7 5800H
/// still runs at its 3.2 GHz base clock — plenty for browsing, coding or video.
/// Nothing can crash because of this: tasks only take a bit longer.
///
/// How:
///   - the hidden power-plan setting PERFBOOSTMODE is set to 0 in the active
///     plan. Windows stores separate values for "on charger" (AC) and "on
///     battery" (DC), so "only on battery" = change only the DC value, and
///     Windows switches automatically when you plug in or unplug;
///   - in "Always" mode the Windows power mode slider is also set to
///     "Best power efficiency".
///   - the original values are saved to a file FIRST, so they can be restored
///     even after a crash (the next start sees the file and puts them back).
///
/// When it turns off: while a game runs, while an app from the "full power"
/// list runs, while the user pressed "full power for now", or when the app exits.
/// </summary>
public sealed class QuietModeController : IQuietModeController
{
    private static readonly Guid OverlayBestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    private readonly ISettingsService _settings;
    private readonly IGameWatcher _games;
    private readonly IPowerManager _power;
    private readonly ILogger<QuietModeController> _log;
    private readonly string _stateFile;
    private readonly object _lock = new();
    private Timer? _timer;
    private bool _paused;
    private string _status = "Выключен";

    public QuietModeController(ISettingsService settings, IGameWatcher games, IPowerManager power,
                               ILogger<QuietModeController>? log = null, string? stateFile = null)
    {
        _settings = settings;
        _games = games;
        _power = power;
        _log = log ?? NullLogger<QuietModeController>.Instance;
        _stateFile = stateFile ?? Path.Combine(AppPaths.DataFolder, "quiet_backup.txt");
    }

    public bool IsActive => File.Exists(_stateFile);
    public bool IsPaused => _paused;
    public string Status => _status;
    public event EventHandler? StateChanged;

    public void Start()
    {
        // Off the UI thread: powercfg calls take ~100 ms each.
        _settings.Changed += (_, _) => Task.Run(Reevaluate);
        // The timer catches what has no event: an exception app started or closed.
        _timer ??= new Timer(_ => Reevaluate(), null, TimeSpan.Zero, CheckInterval);
    }

    public void Pause()
    {
        _paused = true;
        Reevaluate();
    }

    public void Resume()
    {
        _paused = false;
        Reevaluate();
    }

    /// <summary>
    /// Decides what the state SHOULD be and applies it only if it differs.
    /// Called from several threads (timer, game watcher, UI), hence the lock.
    /// </summary>
    public void Reevaluate()
    {
        lock (_lock)
        {
            try
            {
                var policy = _settings.Current.QuietMode;
                string? reasonOff =
                    policy == QuietModePolicy.Off ? "Выключен в настройках" :
                    _paused ? "Полная мощность (включено вручную)" :
                    _games.Current != null ? $"Игра: {_games.Current.DisplayName} — полная мощность" :
                    _power.IsBoosted ? "Включена максимальная производительность" :
                    RunningFullPowerApp() is string app ? $"Запущена {app} — полная мощность" :
                    null;

                if (reasonOff != null)
                {
                    if (IsActive) Disable();
                    SetStatus(reasonOff);
                    return;
                }

                // Switching between "on battery" and "always" needs a re-apply.
                if (IsActive && SavedPolicy() != policy) Disable();
                if (!IsActive) Enable(policy == QuietModePolicy.OnBattery);

                if (IsActive)
                {
                    SetStatus(policy == QuietModePolicy.OnBattery
                        ? "Включён: от батареи турбо-буст выключен"
                        : "Включён: турбо-буст выключен, режим энергоэффективности");
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Quiet mode re-evaluation failed");
            }
        }
    }

    private void Enable(bool batteryOnly)
    {
        string? scheme = Powercfg.ActiveSchemeGuid();
        if (scheme == null) { SetStatus("Не удалось прочитать схему питания"); return; }

        if (!Powercfg.Run($"/query {scheme} {Powercfg.ProcessorSubgroup} {Powercfg.BoostSetting}", out string query) ||
            Powercfg.ParseCurrentIndexes(query) is not (int ac, int dc))
        {
            SetStatus("Схема питания не поддерживает управление турбо-бустом");
            return;
        }

        string overlay = "";
        try
        {
            if (Native.PowerGetEffectiveOverlayScheme(out Guid g) == 0) overlay = g.ToString();
        }
        catch (Exception) { /* old Windows */ }

        // 1. Save the originals BEFORE changing anything.
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        File.WriteAllLines(_stateFile, new[] { scheme, ac.ToString(), dc.ToString(), overlay, batteryOnly ? "battery" : "always" });

        // 2. Apply.
        bool ok = Powercfg.Run($"/setdcvalueindex {scheme} {Powercfg.ProcessorSubgroup} {Powercfg.BoostSetting} 0", out _);
        if (!batteryOnly)
        {
            ok &= Powercfg.Run($"/setacvalueindex {scheme} {Powercfg.ProcessorSubgroup} {Powercfg.BoostSetting} 0", out _);
            try { Native.PowerSetActiveOverlayScheme(OverlayBestEfficiency); }
            catch (Exception) { /* no power mode slider on this PC */ }
        }
        // Changed values only take effect when the scheme is (re)activated.
        ok &= Powercfg.Run($"/setactive {scheme}", out _);

        if (!ok)
        {
            Disable();   // put back whatever was changed
            SetStatus("Windows не дал изменить схему питания — попробуй режим администратора");
            return;
        }
        _log.LogInformation("Quiet mode enabled (batteryOnly={BatteryOnly}), original boost AC={Ac} DC={Dc}", batteryOnly, ac, dc);
    }

    private void Disable()
    {
        if (!File.Exists(_stateFile)) return;
        string[] s = File.ReadAllLines(_stateFile);
        if (s.Length >= 3)
        {
            string scheme = s[0];
            Powercfg.Run($"/setacvalueindex {scheme} {Powercfg.ProcessorSubgroup} {Powercfg.BoostSetting} {s[1]}", out _);
            Powercfg.Run($"/setdcvalueindex {scheme} {Powercfg.ProcessorSubgroup} {Powercfg.BoostSetting} {s[2]}", out _);

            // Re-apply only if that scheme is still the active one; otherwise the
            // restored values simply take effect the next time it becomes active.
            if (string.Equals(Powercfg.ActiveSchemeGuid(), scheme, StringComparison.OrdinalIgnoreCase))
                Powercfg.Run($"/setactive {scheme}", out _);

            bool wasAlways = s.Length >= 5 && s[4] == "always";
            if (wasAlways && s.Length >= 4 && Guid.TryParse(s[3], out Guid overlay))
            {
                try { Native.PowerSetActiveOverlayScheme(overlay); }
                catch (Exception) { }
            }
        }
        File.Delete(_stateFile);
        _log.LogInformation("Quiet mode disabled, original power settings restored");
    }

    private QuietModePolicy SavedPolicy()
    {
        string[] s = File.ReadAllLines(_stateFile);
        return s.Length >= 5 && s[4] == "battery" ? QuietModePolicy.OnBattery : QuietModePolicy.Always;
    }

    /// <summary>Name of a running app from the user's "needs full power" list, if any.</summary>
    private string? RunningFullPowerApp()
    {
        foreach (string raw in _settings.Current.FullPowerApps)
        {
            string name = raw.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (name.Length == 0) continue;

            var found = Process.GetProcessesByName(name);
            foreach (var p in found) p.Dispose();
            if (found.Length > 0) return name;
        }
        return null;
    }

    private void SetStatus(string status)
    {
        if (_status == status) return;
        _status = status;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>On exit the laptop goes back to exactly how it was before GameTuner.</summary>
    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        lock (_lock)
        {
            try { Disable(); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not restore power settings on exit"); }
        }
    }
}
