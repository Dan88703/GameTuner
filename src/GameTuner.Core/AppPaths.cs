namespace GameTuner.Core;

/// <summary>Where GameTuner keeps its own files (all per-user, no admin needed).</summary>
public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\GameTuner</summary>
    public static string DataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameTuner");

    public static string LogsFolder => Path.Combine(DataFolder, "logs");
    public static string ToolsFolder => Path.Combine(DataFolder, "tools");
    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");
    public static string SessionsFile => Path.Combine(DataFolder, "sessions.json");
    public static string PowerStateFile => Path.Combine(DataFolder, "power_backup.txt");
}
