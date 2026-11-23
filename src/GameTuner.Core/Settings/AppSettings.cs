using System.Runtime.InteropServices;
using System.Text.Json;
using GameTuner.Core.Performance;
using GameTuner.Core.Tuning;
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

    // --- Quiet mode (outside games) ---
    public QuietModePolicy QuietMode { get; set; } = QuietModePolicy.Always;

    /// <summary>Apps that need full CPU power (e.g. "devenv", "blender"): quiet mode pauses while they run.</summary>
    public List<string> FullPowerApps { get; set; } = new();

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

/// <summary>
/// Desktop shortcut (GameTuner.lnk) pointing to the running exe.
///
/// How: .lnk files are a binary format, so instead of writing one by hand we
/// ask Windows to do it through the "WScript.Shell" COM object (the same one
/// installers and scripts use). <c>dynamic</c> lets us call its methods
/// without generating COM interop classes.
/// </summary>
public static class DesktopShortcut
{
    public static string ShortcutPath => Path.Combine(
        // DesktopDirectory follows OneDrive "Desktop" redirection.
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "GameTuner.lnk");

    public static bool Exists => File.Exists(ShortcutPath);

    /// <summary>
    /// True when the app runs straight from the build output (bin\Debug or
    /// bin\Release, e.g. via "dotnet run"). A shortcut or autostart entry made
    /// from there breaks as soon as the project is cleaned or rebuilt elsewhere.
    /// </summary>
    public static bool IsRunningFromBuildFolder
    {
        get
        {
            string exe = Environment.ProcessPath ?? "";
            return exe.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
                || exe.Contains(@"\bin\x64\Debug\", StringComparison.OrdinalIgnoreCase)
                || exe.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase)
                || exe.Contains(@"\bin\x64\Release\", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Create()
    {
        string exe = Environment.ProcessPath
                     ?? throw new InvalidOperationException("Cannot determine the path of GameTuner.exe");
        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
                         ?? throw new PlatformNotSupportedException("WScript.Shell is not available");

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(ShortcutPath);
            try
            {
                link.TargetPath = exe;
                link.WorkingDirectory = Path.GetDirectoryName(exe);
                link.IconLocation = exe + ",0";   // icon #0 embedded in the exe (ApplicationIcon)
                link.Description = "GameTuner — game optimizer";
                link.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
        finally
        {
            // COM objects are not garbage-collected promptly: release them explicitly.
            Marshal.FinalReleaseComObject(shell);
        }
    }

    public static void Remove()
    {
        if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
    }
}
