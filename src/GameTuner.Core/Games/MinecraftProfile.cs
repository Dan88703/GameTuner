using System.Diagnostics;
using GameTuner.Core.Configs;
using GameTuner.Core.Performance;

namespace GameTuner.Core.Games;

/// <summary>
/// Minecraft Java Edition: %APPDATA%\.minecraft\options.txt ("key:value" lines).
/// Key names changed between versions, so both old and new variants are listed
/// and only those present in the file are changed.
/// </summary>
public sealed class MinecraftProfile : GameProfile
{
    private static readonly IReadOnlySet<string> VersionSpecificKeys =
        new HashSet<string> { "fancyGraphics", "graphicsMode" };

    public override string Id => "minecraft-java";
    public override string Name => "Minecraft (Java)";
    public override IReadOnlyList<string> ProcessNames { get; } = new[] { "javaw" };

    public override List<string> FindConfigFiles()
    {
        string options = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "options.txt");
        return File.Exists(options) ? new List<string> { options } : new List<string>();
    }

    /// <summary>
    /// javaw.exe runs many Java programs (IDEs, tools), so a running javaw
    /// alone does not mean Minecraft is open — the window title must match too.
    /// </summary>
    public override bool IsRunning() =>
        Process.GetProcessesByName("javaw").Any(p =>
        {
            using (p)
            {
                try { return p.MainWindowTitle.Contains("Minecraft", StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            }
        });

    public override bool MatchesProcess(string processName, Func<string> windowTitle) =>
        processName.Equals("javaw", StringComparison.OrdinalIgnoreCase) &&
        windowTitle().Contains("Minecraft", StringComparison.OrdinalIgnoreCase);

    public override List<SettingChange> GetSettings(PerformanceTier t) => new()
    {
        new("renderDistance", ByTier(t, "6", "10", "14", "20"), "Дальность прорисовки (чанки)"),
        new("simulationDistance", ByTier(t, "5", "8", "10", "12"), "Дальность симуляции"),
        new("graphicsMode", ByTier(t, "0", "1", "1", "1"), "Графика (0 = быстрая, 1 = детальная)"),
        new("fancyGraphics", ByTier(t, "false", "true", "true", "true"), "Детальная графика (старые версии)"),
        new("renderClouds", ByTier(t, "false", "fast", "true", "true"), "Облака"),
        new("entityShadows", ByTier(t, "false", "false", "true", "true"), "Тени сущностей"),
        new("particles", ByTier(t, "2", "1", "0", "0"), "Частицы (0 = все, 2 = минимум)"),
        new("mipmapLevels", ByTier(t, "0", "2", "4", "4"), "Уровни MIP-текстур"),
        new("biomeBlendRadius", ByTier(t, "0", "1", "2", "3"), "Смешивание биомов"),
        new("entityDistanceScaling", ByTier(t, "0.5", "0.75", "1.0", "1.0"), "Дальность сущностей"),
    };

    public override string GetTips(PerformanceTier tier) => tier <= PerformanceTier.Medium
        ? "Поставь Sodium (Fabric) — часто даёт ×2–×3 FPS без потери качества."
        : "При большой дальности прорисовки выдели игре 4–6 ГБ RAM в лаунчере (-Xmx6G).";

    protected override ConfigEditResult Edit(string content, IReadOnlyDictionary<string, string> values) =>
        ConfigEditors.EditColonOptions(content, values, VersionSpecificKeys);
}
