using Microsoft.Win32;

namespace GameTuner.Core.Tuning;

public interface IWindowsGameSettings
{
    bool IsGameModeEnabled();
    void SetGameMode(bool enabled);

    /// <summary>Hardware-accelerated GPU scheduling: true/false, or null if unsupported.</summary>
    bool? IsGpuSchedulingEnabled();
}

/// <summary>
/// Windows-level gaming switches stored in the registry.
/// Game Mode lives in HKCU (no admin). GPU scheduling lives in HKLM and needs
/// admin + reboot to change, so it is only read and the user is told where to switch it.
/// </summary>
public sealed class WindowsGameSettings : IWindowsGameSettings
{
    private const string GameBarKey = @"Software\Microsoft\GameBar";

    public bool IsGameModeEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(GameBarKey);
        // A missing value means the Windows default, which is "on".
        return key?.GetValue("AutoGameModeEnabled") is not int v || v == 1;
    }

    public void SetGameMode(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(GameBarKey);
        key.SetValue("AutoGameModeEnabled", enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    public bool? IsGpuSchedulingEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            return key?.GetValue("HwSchMode") is int v ? v == 2 : null;
        }
        catch (Exception) { return null; }
    }
}
