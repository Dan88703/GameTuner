using System.Text.Json;
using GameTuner.Core.Performance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace GameTuner.Core.Settings;

/// <summary>User settings, saved as JSON in %LOCALAPPDATA%\GameTuner\settings.json.</summary>
public sealed class AppSettings
{
    // --- Auto mode ---
    public bool AutoModeEnabled { get; set; } = true;
    public bool BoostPowerOnGameStart { get; set; } = true;
    public bool RaiseGamePriority { get; set; } = true;
    public bool CaptureFps { get; set; } = true;
    public bool CloseAppsOnGameStart { get; set; }
    public List<string> AppsToClose { get; set; } = new();

    /// <summary>Extra executables (without .exe) treated as games, e.g. "eldenring".</summary>
    public List<string> ExtraGameExecutables { get; set; } = new();

    // --- App behaviour ---
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;

    /// <summary>Manual preset; null = use the calculated tier.</summary>
    public PerformanceTier? TierOverride { get; set; }
}

public interface ISettingsService
{
    AppSettings Current { get; }
    void Save();
    event EventHandler? Changed;
}

/// <summary>
/// Loads settings once at startup and saves on demand.
/// A broken or missing file never stops the app: defaults are used instead.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly ILogger<SettingsService> _log;

    public SettingsService(ILogger<SettingsService>? log = null, string? path = null)
    {
        _log = log ?? NullLogger<SettingsService>.Instance;
        _path = path ?? AppPaths.SettingsFile;
        Current = Load();
    }

    public AppSettings Current { get; }
    public event EventHandler? Changed;

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, Json));
            AutoStart.Apply(Current.StartWithWindows);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to save settings");
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Settings file is corrupted, using defaults");
        }
        return new AppSettings();
    }
}

/// <summary>Start with Windows via HKCU\...\Run (per-user, no admin needed).</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GameTuner";

    /// <summary>Command-line flag the app checks to start hidden in the tray.</summary>
    public const string MinimizedArg = "--minimized";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        string? exe = Environment.ProcessPath;
        if (enabled && exe != null)
            key.SetValue(ValueName, $"\"{exe}\" {MinimizedArg}");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
