using GameTuner.Core.Configs;
using GameTuner.Core.Performance;
using Microsoft.Win32;

namespace GameTuner.Core.Games;

/// <summary>
/// Counter-Strike 2. Video settings:
///   &lt;Steam&gt;\userdata\&lt;accountId&gt;\730\local\cfg\cs2_video.txt   (730 = CS2 app id)
/// One file per Steam account that ever played on this PC — all are updated.
/// </summary>
public sealed class Cs2Profile : GameProfile
{
    public override string Id => "cs2";
    public override string Name => "Counter-Strike 2";
    public override IReadOnlyList<string> ProcessNames { get; } = new[] { "cs2" };

    public override List<string> FindConfigFiles()
    {
        var files = new List<string>();
        foreach (string steam in SteamLocator.FindSteamFolders())
        {
            string userdata = Path.Combine(steam, "userdata");
            if (!Directory.Exists(userdata)) continue;

            foreach (string account in Directory.GetDirectories(userdata))
            {
                string cfg = Path.Combine(account, "730", "local", "cfg", "cs2_video.txt");
                if (File.Exists(cfg)) files.Add(cfg);
            }
        }
        return files;
    }

    /// <summary>
    /// CS2 is competitive: the goal is high and STABLE FPS, not beauty.
    /// V-Sync stays off and low latency on for every tier; dynamic shadows
    /// stay on because they show enemy shadows (a gameplay advantage).
    /// </summary>
    public override List<SettingChange> GetSettings(PerformanceTier t) => new()
    {
        new("setting.mat_vsync", "0", "V-Sync: выкл"),
        new("setting.r_low_latency", "1", "Низкая задержка (Reflex): вкл"),
        new("setting.msaa_samples", ByTier(t, "0", "2", "4", "4"), "MSAA"),
        new("setting.shaderquality", ByTier(t, "0", "0", "1", "1"), "Качество шейдеров"),
        new("setting.r_texturefilteringquality", ByTier(t, "0", "1", "3", "5"), "Фильтрация текстур"),
        new("setting.videocfg_shadow_quality", ByTier(t, "0", "1", "2", "3"), "Качество теней"),
        new("setting.videocfg_dynamic_shadows", "1", "Динамические тени: все"),
        new("setting.videocfg_texture_detail", ByTier(t, "0", "1", "2", "2"), "Детализация текстур"),
        new("setting.videocfg_particle_detail", ByTier(t, "0", "1", "2", "3"), "Детализация частиц"),
        new("setting.videocfg_ao_detail", ByTier(t, "0", "0", "1", "2"), "Ambient occlusion"),
        new("setting.videocfg_fsr_detail", ByTier(t, "2", "0", "0", "0"), "FidelityFX Super Resolution"),
    };

    public override string GetTips(PerformanceTier tier) =>
        "Параметры запуска в Steam: -novid. Если FPS скачет — ограничь его командой fps_max (например, частота экрана × 2).";

    protected override ConfigEditResult Edit(string content, IReadOnlyDictionary<string, string> values) =>
        ConfigEditors.EditValveKeyValues(content, values);
}

/// <summary>Finds Steam install folders (registry + default path).</summary>
public static class SteamLocator
{
    public static IEnumerable<string> FindSteamFolders()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string p && p.Length > 0)
                result.Add(Path.GetFullPath(p.Replace('/', '\\')));   // Steam stores it with forward slashes
        }
        catch (Exception) { /* no Steam */ }

        string defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        if (Directory.Exists(defaultPath)) result.Add(defaultPath);
        return result;
    }
}
