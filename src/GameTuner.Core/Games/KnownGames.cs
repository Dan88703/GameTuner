namespace GameTuner.Core.Games;

/// <summary>
/// Popular games detected by auto mode out of the box, so a fresh install on
/// another PC works without typing process names by hand.
///
/// These games get auto mode (power, priority, FPS, session history) but no
/// config presets — for presets a full <see cref="GameProfile"/> is needed.
/// Key = process name without ".exe" (what Task Manager → Details shows).
/// </summary>
public static class KnownGames
{
    private static readonly Dictionary<string, string> Games = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FortniteClient-Win64-Shipping"] = "Fortnite",
        ["VALORANT-Win64-Shipping"] = "Valorant",
        ["GenshinImpact"] = "Genshin Impact",
        ["YuanShen"] = "Genshin Impact",
        ["StarRail"] = "Honkai: Star Rail",
        ["ZenlessZoneZero"] = "Zenless Zone Zero",
        ["r5apex"] = "Apex Legends",
        ["r5apex_dx12"] = "Apex Legends",
        ["dota2"] = "Dota 2",
        ["League of Legends"] = "League of Legends",
        ["Overwatch"] = "Overwatch 2",
        ["RobloxPlayerBeta"] = "Roblox",
        ["RainbowSix"] = "Rainbow Six Siege",
        ["RainbowSix_Vulkan"] = "Rainbow Six Siege",
        ["RustClient"] = "Rust",
        ["TslGame"] = "PUBG",
        ["Marvel-Win64-Shipping"] = "Marvel Rivals",
        ["project8"] = "Deadlock",
        ["destiny2"] = "Destiny 2",
        ["Warframe.x64"] = "Warframe",
        ["eldenring"] = "Elden Ring",
        ["Cyberpunk2077"] = "Cyberpunk 2077",
        ["RDR2"] = "Red Dead Redemption 2",
        ["witcher3"] = "The Witcher 3",
        ["RocketLeague"] = "Rocket League",
        ["FC25"] = "EA SPORTS FC 25",
        ["FC26"] = "EA SPORTS FC 26",
        ["cod"] = "Call of Duty",
        ["Palworld-Win64-Shipping"] = "Palworld",
        ["Terraria"] = "Terraria",
    };

    /// <summary>Display name for a known game process, or null.</summary>
    public static string? Find(string processName) =>
        Games.TryGetValue(processName, out string? name) ? name : null;

    public static int Count => Games.Count;
}
