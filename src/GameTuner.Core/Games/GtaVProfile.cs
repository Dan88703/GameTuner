using GameTuner.Core.Configs;
using GameTuner.Core.Performance;

namespace GameTuner.Core.Games;

/// <summary>
/// GTA V: Documents\Rockstar Games\GTA V\settings.xml (and "GTAV Enhanced").
/// Each setting is an element with a value attribute: &lt;TextureQuality value="2" /&gt;
/// </summary>
public sealed class GtaVProfile : GameProfile
{
    public override string Id => "gta5";
    public override string Name => "GTA V";
    public override IReadOnlyList<string> ProcessNames { get; } = new[] { "GTA5", "GTA5_Enhanced" };

    public override List<string> FindConfigFiles()
    {
        // MyDocuments follows OneDrive folder redirection automatically.
        string rockstar = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rockstar Games");

        return new[] { "GTA V", "GTAV Enhanced" }
            .Select(folder => Path.Combine(rockstar, folder, "settings.xml"))
            .Where(File.Exists)
            .ToList();
    }

    /// <summary>
    /// Grass and shadows are GTA V's biggest FPS killers, so they drop first.
    /// MSAA is expensive; lower tiers use cheap FXAA instead.
    /// </summary>
    public override List<SettingChange> GetSettings(PerformanceTier t) => new()
    {
        new("TextureQuality", ByTier(t, "0", "1", "2", "2"), "Текстуры"),
        new("ShaderQuality", ByTier(t, "0", "1", "2", "2"), "Шейдеры"),
        new("ShadowQuality", ByTier(t, "1", "1", "2", "3"), "Тени"),
        new("ReflectionQuality", ByTier(t, "0", "1", "2", "3"), "Отражения"),
        new("ReflectionMSAA", "0", "MSAA отражений: выкл"),
        new("WaterQuality", ByTier(t, "0", "1", "2", "2"), "Вода"),
        new("ParticleQuality", ByTier(t, "0", "1", "2", "3"), "Частицы"),
        new("GrassQuality", ByTier(t, "0", "0", "1", "2"), "Трава"),
        new("PostFX", ByTier(t, "0", "1", "2", "3"), "Постобработка"),
        new("Tessellation", ByTier(t, "0", "1", "2", "3"), "Тесселяция"),
        new("SSAO", ByTier(t, "0", "1", "2", "2"), "SSAO"),
        new("AnisotropicFiltering", ByTier(t, "2", "4", "8", "16"), "Анизотропная фильтрация"),
        new("MSAA", ByTier(t, "0", "0", "2", "4"), "MSAA"),
        new("FXAA", ByTier(t, "1", "1", "0", "0"), "FXAA"),
        new("LodScale", ByTier(t, "0.000000", "0.300000", "0.700000", "1.000000"), "Дальность детализации"),
        new("UltraShadows_Enabled", "false", "Ultra-тени: выкл"),
        new("Lighting_FogVolumes", ByTier(t, "false", "true", "true", "true"), "Объёмный туман"),
    };

    public override string GetTips(PerformanceTier tier) =>
        "Если в игре есть DLSS/FSR — включи в режиме «Качество»: это +20–40% FPS почти бесплатно.";

    protected override ConfigEditResult Edit(string content, IReadOnlyDictionary<string, string> values) =>
        ConfigEditors.EditXmlValueAttributes(content, values);
}
