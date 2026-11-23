using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameTuner.App.Services;
using GameTuner.Core;
using GameTuner.Core.Monitoring;
using GameTuner.Core.Settings;
using GameTuner.Core.Tuning;

namespace GameTuner.App.ViewModels;

/// <summary>
/// "Настройки" page. Every property writes straight into AppSettings and
/// saves, so there is no "Save" button to forget.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IFpsMonitor _fps;
    private readonly IDialogService _dialogs;
    private readonly bool _ready;

    public SettingsViewModel(ISettingsService settings, IFpsMonitor fps, IDialogService dialogs)
    {
        _settings = settings;
        _fps = fps;
        _dialogs = dialogs;

        var s = settings.Current;
        AutoModeEnabled = s.AutoModeEnabled;
        BoostPowerOnGameStart = s.BoostPowerOnGameStart;
        RaiseGamePriority = s.RaiseGamePriority;
        CaptureFps = s.CaptureFps;
        CloseAppsOnGameStart = s.CloseAppsOnGameStart;
        AppsToCloseText = string.Join(", ", s.AppsToClose);
        ExtraGamesText = string.Join(", ", s.ExtraGameExecutables);
        StartWithWindows = s.StartWithWindows;
        MinimizeToTray = s.MinimizeToTray;
        ShowNotifications = s.ShowNotifications;
        HasDesktopShortcut = DesktopShortcut.Exists;

        // Settings can also change from the tray menu (auto mode on/off).
        settings.Changed += (_, _) =>
        {
            if (AutoModeEnabled != settings.Current.AutoModeEnabled)
                App.MainWindow?.DispatcherQueue.TryEnqueue(() => AutoModeEnabled = settings.Current.AutoModeEnabled);
        };

        RefreshPresentMon();
        _ready = true;
    }

    // ---------- Auto mode ----------
    [ObservableProperty] private bool _autoModeEnabled;
    [ObservableProperty] private bool _boostPowerOnGameStart;
    [ObservableProperty] private bool _raiseGamePriority;
    [ObservableProperty] private bool _captureFps;
    [ObservableProperty] private bool _closeAppsOnGameStart;
    [ObservableProperty] private string _appsToCloseText = "";
    [ObservableProperty] private string _extraGamesText = "";

    // ---------- App ----------
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _showNotifications;
    [ObservableProperty] private bool _hasDesktopShortcut;

    /// <summary>Warn when shortcut/autostart would point into bin\Debug (see DesktopShortcut).</summary>
    public bool IsRunningFromBuildFolder { get; } = DesktopShortcut.IsRunningFromBuildFolder;

    // ---------- Admin & PresentMon ----------
    public bool IsElevated { get; } = Elevation.IsElevated;
    public bool IsNotElevated => !IsElevated;
    public string ElevationText => IsElevated
        ? "GameTuner запущен от администратора: температуры, частоты и FPS доступны."
        : "Обычный режим. Температуры, частоты и счётчик FPS требуют прав администратора.";

    [ObservableProperty] private string _presentMonStatus = "";
    [ObservableProperty] private bool _canInstallPresentMon;
    [ObservableProperty] private bool _isInstalling;

    public string Version =>
        "GameTuner " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev");

    public string DataFolder => AppPaths.DataFolder;

    // Each generated OnXChanged hook copies the value into AppSettings and saves.
    partial void OnAutoModeEnabledChanged(bool value) => Save(s => s.AutoModeEnabled = value);
    partial void OnBoostPowerOnGameStartChanged(bool value) => Save(s => s.BoostPowerOnGameStart = value);
    partial void OnRaiseGamePriorityChanged(bool value) => Save(s => s.RaiseGamePriority = value);
    partial void OnCaptureFpsChanged(bool value) => Save(s => s.CaptureFps = value);
    partial void OnCloseAppsOnGameStartChanged(bool value) => Save(s => s.CloseAppsOnGameStart = value);
    partial void OnAppsToCloseTextChanged(string value) => Save(s => s.AppsToClose = SplitList(value));
    partial void OnExtraGamesTextChanged(string value) => Save(s => s.ExtraGameExecutables = SplitList(value));
    partial void OnStartWithWindowsChanged(bool value) => Save(s => s.StartWithWindows = value);
    partial void OnMinimizeToTrayChanged(bool value) => Save(s => s.MinimizeToTray = value);
    partial void OnShowNotificationsChanged(bool value) => Save(s => s.ShowNotifications = value);

    /// <summary>The shortcut is a file on the desktop, not a setting: create or delete it directly.</summary>
    partial void OnHasDesktopShortcutChanged(bool value)
    {
        if (!_ready) return;
        try
        {
            if (value) DesktopShortcut.Create();
            else DesktopShortcut.Remove();
        }
        catch (Exception ex)
        {
            _ = _dialogs.ShowAsync("Не удалось изменить ярлык", ex.Message);
        }
    }

    [RelayCommand]
    private void RestartAsAdmin()
    {
        if (Elevation.RestartElevated(App.ElevatedRestartArg))
            ((App)Microsoft.UI.Xaml.Application.Current).ExitApp();
    }

    [RelayCommand]
    private async Task InstallPresentMonAsync()
    {
        if (!await _dialogs.ConfirmAsync("Скачать PresentMon?",
                "PresentMon — бесплатная утилита Intel с открытым кодом (MIT) для измерения FPS.\n" +
                "Она будет скачана с официальной страницы релизов на GitHub (github.com/GameTechDev/PresentMon) " +
                $"в папку {AppPaths.ToolsFolder}.", "Скачать"))
            return;

        IsInstalling = true;
        PresentMonStatus = "Скачиваю…";
        bool ok = await _fps.InstallAsync();
        IsInstalling = false;
        RefreshPresentMon();

        if (!ok) await _dialogs.ShowAsync("Не удалось скачать", "Проверь интернет и попробуй ещё раз. Подробности — в логах.");
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        Directory.CreateDirectory(AppPaths.DataFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataFolder}\"") { UseShellExecute = true });
    }

    private void RefreshPresentMon()
    {
        CanInstallPresentMon = !_fps.IsInstalled;
        PresentMonStatus = _fps.IsInstalled
            ? (IsElevated ? "PresentMon установлен, FPS записывается автоматически во время игры."
                          : "PresentMon установлен. Для записи FPS перезапусти GameTuner от администратора.")
            : "PresentMon не установлен — счётчик FPS недоступен.";
    }

    private void Save(Action<AppSettings> apply)
    {
        if (!_ready) return;   // ignore the initial assignments in the constructor
        apply(_settings.Current);
        _settings.Save();
    }

    private static List<string> SplitList(string text) =>
        text.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? x[..^4] : x)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
