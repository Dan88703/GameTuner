using GameTuner.Core.Configs;

namespace GameTuner.Core.Tests;

public class ConfigEditorsTests
{
    // ---------------- Valve KeyValues (CS2) ----------------

    private const string Cs2Video =
        "\"video.cfg\"\r\n{\r\n\t\"Version\"\t\t\"14\"\r\n\t\"setting.msaa_samples\"\t\t\"8\"\r\n\t\"setting.mat_vsync\"\t\t\"1\"\r\n}\r\n";

    [Fact]
    public void Valve_ChangesExistingKeys_KeepsFormatting()
    {
        var result = ConfigEditors.EditValveKeyValues(Cs2Video, new Dictionary<string, string>
        {
            ["setting.msaa_samples"] = "2",
            ["setting.mat_vsync"] = "0"
        });

        Assert.Equal(2, result.Changed);
        Assert.Contains("\t\"setting.msaa_samples\"\t\t\"2\"\r\n", result.Content);
        Assert.Contains("\t\"setting.mat_vsync\"\t\t\"0\"\r\n", result.Content);
        Assert.Contains("\"Version\"\t\t\"14\"", result.Content); // untouched
        Assert.Empty(result.MissingKeys);
    }

    [Fact]
    public void Valve_UnknownKey_IsReportedNotAdded()
    {
        var result = ConfigEditors.EditValveKeyValues(Cs2Video, new Dictionary<string, string> { ["setting.new_thing"] = "1" });

        Assert.Equal(0, result.Changed);
        Assert.Equal(Cs2Video, result.Content);
        Assert.Equal(new[] { "setting.new_thing" }, result.MissingKeys);
    }

    [Fact]
    public void Valve_SameValue_CountsAsNoChange()
    {
        var result = ConfigEditors.EditValveKeyValues(Cs2Video, new Dictionary<string, string> { ["setting.msaa_samples"] = "8" });
        Assert.Equal(0, result.Changed);
        Assert.Empty(result.MissingKeys);
    }

    // ---------------- Colon options (Minecraft) ----------------

    private const string McOptions =
        "version:3955\nrenderDistance:12\nrenderClouds:\"true\"\nkey_key.attack:key.mouse.left\ngraphicsMode:1\n";

    [Fact]
    public void Colon_ChangesValues_PreservesQuotesAndColonsInValues()
    {
        var result = ConfigEditors.EditColonOptions(McOptions, new Dictionary<string, string>
        {
            ["renderDistance"] = "6",
            ["renderClouds"] = "false"
        });

        Assert.Equal(2, result.Changed);
        Assert.Contains("renderDistance:6\n", result.Content);
        Assert.Contains("renderClouds:\"false\"\n", result.Content);       // quotes kept
        Assert.Contains("key_key.attack:key.mouse.left\n", result.Content); // untouched
    }

    [Fact]
    public void Colon_AlternativeKeys_AreNotReportedMissing()
    {
        var result = ConfigEditors.EditColonOptions(McOptions,
            new Dictionary<string, string> { ["graphicsMode"] = "0", ["fancyGraphics"] = "false", ["particles"] = "2" },
            new HashSet<string> { "graphicsMode", "fancyGraphics" });

        Assert.Equal(1, result.Changed);
        Assert.Equal(new[] { "particles" }, result.MissingKeys);
    }

    [Theory]
    [InlineData("\"fast\"", "true", "\"true\"")]
    [InlineData("fast", "true", "true")]
    [InlineData("12", "\"6\"", "6")]
    public void MatchQuotes(string oldValue, string newValue, string expected) =>
        Assert.Equal(expected, ConfigEditors.MatchQuotes(oldValue, newValue));

    // ---------------- XML (GTA V) ----------------

    private const string GtaSettings =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Settings>\n  <graphics>\n    <MSAA value=\"4\" />\n    <GrassQuality value=\"3\" />\n  </graphics>\n</Settings>";

    [Fact]
    public void Xml_ChangesValueAttributes_KeepsDeclaration()
    {
        var result = ConfigEditors.EditXmlValueAttributes(GtaSettings, new Dictionary<string, string>
        {
            ["MSAA"] = "0",
            ["GrassQuality"] = "0",
            ["Tessellation"] = "1"
        });

        Assert.Equal(2, result.Changed);
        Assert.StartsWith("<?xml", result.Content);
        Assert.Contains("<MSAA value=\"0\" />", result.Content);
        Assert.Contains("<GrassQuality value=\"0\" />", result.Content);
        Assert.Equal(new[] { "Tessellation" }, result.MissingKeys);
    }
}
