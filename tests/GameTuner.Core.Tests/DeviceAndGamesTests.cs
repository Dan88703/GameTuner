using GameTuner.Core.Games;
using GameTuner.Core.Hardware;

namespace GameTuner.Core.Tests;

public class DeviceAndGamesTests
{
    [Theory]
    [InlineData(new[] { 10 }, false, DeviceKind.Laptop)]    // Notebook
    [InlineData(new[] { 31 }, true, DeviceKind.Laptop)]     // Convertible
    [InlineData(new[] { 3 }, false, DeviceKind.Desktop)]    // Desktop
    [InlineData(new[] { 7 }, true, DeviceKind.Desktop)]     // Tower with a UPS "battery" is still a desktop
    [InlineData(new[] { 2 }, true, DeviceKind.Laptop)]      // Unknown chassis + battery → laptop
    [InlineData(new int[0], false, DeviceKind.Desktop)]     // nothing reported, no battery → desktop
    public void DetectDeviceKind(int[] chassis, bool battery, DeviceKind expected) =>
        Assert.Equal(expected, HardwareScanner.DetectDeviceKind(chassis, battery));

    [Theory]
    [InlineData("FortniteClient-Win64-Shipping", "Fortnite")]
    [InlineData("genshinimpact", "Genshin Impact")]          // case-insensitive
    [InlineData("League of Legends", "League of Legends")]
    [InlineData("notepad", null)]
    public void KnownGames_Find(string process, string? expected) =>
        Assert.Equal(expected, KnownGames.Find(process));
}
