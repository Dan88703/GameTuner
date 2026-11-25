using GameTuner.Core.Health;
using GameTuner.Core.Security;

namespace GameTuner.Core.Tests;

public class SecurityAndHealthTests
{
    // ---------------- StartupRules ----------------

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --minimized", "C:\\Program Files\\App\\app.exe")]
    [InlineData("C:\\Program Files\\Tool Kit\\tool.exe /silent", "C:\\Program Files\\Tool Kit\\tool.exe")]
    [InlineData("C:\\Scripts\\run.bat", "C:\\Scripts\\run.bat")]
    public void ExtractPath_FromCommandLine(string command, string expected) =>
        Assert.Equal(expected, StartupRules.ExtractPath(command));

    [Fact]
    public void ExtractPath_FindsBareSystemPrograms()
    {
        // "rundll32.exe" has no folder in the command but lives in System32.
        string? path = StartupRules.ExtractPath("rundll32.exe shell32.dll,Control_RunDLL");
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Rules_MissingFile_IsWarning()
    {
        var (findings, risk) = StartupRules.Evaluate("C:\\Gone\\old.exe", "C:\\Gone\\old.exe", fileExists: false, publisher: null);
        Assert.Equal(RiskLevel.Warning, risk);
        Assert.Contains(findings, f => f.Contains("не найден"));
    }

    [Fact]
    public void Rules_TempFolder_And_ScriptHost_AreWarnings()
    {
        string tempExe = Path.Combine(Path.GetTempPath(), "x1y2z3.exe");
        Assert.Equal(RiskLevel.Warning, StartupRules.Evaluate(tempExe, tempExe, true, "Someone").Risk);

        var (findings, risk) = StartupRules.Evaluate(
            "wscript.exe C:\\Users\\a\\update.vbs", "C:\\Windows\\System32\\wscript.exe", true, null);
        Assert.Equal(RiskLevel.Warning, risk);
        Assert.Contains(findings, f => f.Contains("wscript"));
    }

    [Fact]
    public void Rules_SignedProgram_IsClean()
    {
        var (findings, risk) = StartupRules.Evaluate(
            "\"C:\\Program Files\\Steam\\steam.exe\" -silent", "C:\\Program Files\\Steam\\steam.exe", true, "Valve Corp.");
        Assert.Equal(RiskLevel.None, risk);
        Assert.Empty(findings);
    }

    [Fact]
    public void StartupApproved_EnabledDisabledFlags()
    {
        Assert.True(StartupManager.IsApprovedEnabled(null));
        Assert.True(StartupManager.IsApprovedEnabled(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
        Assert.False(StartupManager.IsApprovedEnabled(new byte[] { 3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }));

        byte[] disabled = StartupManager.ApprovedValue(false, DateTime.UtcNow);
        Assert.Equal(12, disabled.Length);
        Assert.False(StartupManager.IsApprovedEnabled(disabled));
        Assert.True(StartupManager.IsApprovedEnabled(StartupManager.ApprovedValue(true, DateTime.UtcNow)));
    }

    // ---------------- Defender helpers ----------------

    [Fact]
    public void Defender_CleansResourcePrefix() =>
        Assert.Equal(@"C:\Users\a\Downloads\bad.exe", DefenderService.CleanResource(@"file:_C:\Users\a\Downloads\bad.exe"));

    // ---------------- Health ----------------

    [Fact]
    public void Score_SubtractsPenalties()
    {
        var items = new[]
        {
            new HealthItem("a", "", "", HealthStatus.Problem),
            new HealthItem("b", "", "", HealthStatus.Warning),
            new HealthItem("c", "", "", HealthStatus.Info),
            new HealthItem("d", "", "", HealthStatus.Ok)
        };
        Assert.Equal(100 - 20 - 8 - 2, HealthCheckService.ComputeScore(items));
    }

    [Fact]
    public void Score_NeverBelowZero() =>
        Assert.Equal(0, HealthCheckService.ComputeScore(Enumerable.Repeat(new HealthItem("p", "", "", HealthStatus.Problem), 10)));

    [Theory]
    [InlineData(5, HealthStatus.Problem)]
    [InlineData(12, HealthStatus.Warning)]
    [InlineData(40, HealthStatus.Ok)]
    public void DiskSpace_Thresholds(double freePercent, HealthStatus expected) =>
        Assert.Equal(expected, HealthCheckService.DiskSpaceStatus(freePercent));

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", "nvidia.com")]
    [InlineData("AMD Radeon(TM) Graphics", "amd.com")]
    [InlineData("Intel(R) Iris(R) Xe Graphics", "intel.com")]
    public void DriverLink_PicksVendor(string gpu, string host) =>
        Assert.Contains(host, HealthCheckService.DriverLink(gpu).Link.Host);
}
