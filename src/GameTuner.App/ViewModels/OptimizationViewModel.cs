using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameTuner.App.Services;
using GameTuner.Core.Tuning;

namespace GameTuner.App.ViewModels;

/// <summary>A row in the heavy-process list, with a checkbox.</summary>
public partial class ProcessItem : ObservableObject
{
    public ProcessItem(ProcessEntry entry)
    {
        Name = entry.Name;
        Details = $"{entry.MemoryMb:0} МБ" +
                  (entry.Count > 1 ? $" · процессов: {entry.Count}" : "") +
                  (entry.Title.Length > 0 ? $" · {Shorten(entry.Title)}" : "");
    }

    public string Name { get; }
    public string Details { get; }
    [ObservableProperty] private bool _isSelected;

    private static string Shorten(string s) => s.Length > 60 ? s[..60] + "…" : s;
}

/// <summary>"Оптимизация" page: power, Windows game settings, background apps.</summary>
public partial class OptimizationViewModel : ObservableObject
{
    private readonly IPowerManager _power;
    private readonly IWindowsGameSettings _windows;
    private readonly IProcessManager _processes;
    private readonly IDialogService _dialogs;
    private readonly IQuietModeController _quiet;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private bool _loading;

    public OptimizationViewModel(IPowerManager power, IWindowsGameSettings windows,
                                 IProcessManager processes, IDialogService dialogs, IQuietModeController quiet)
    {
        _power = power;
        _windows = windows;
        _processes = processes;
        _dialogs = dialogs;
        _quiet = quiet;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        // StateChanged comes from a background thread: update bindings on the UI thread.
        _quiet.StateChanged += (_, _) => _dispatcher.TryEnqueue(RefreshQuiet);
        RefreshQuiet();
        RefreshSystem();
        _ = RefreshProcessesAsync();
    }

    [ObservableProperty] private string _powerPlan = "";
    [ObservableProperty] private bool _isBoosted;
    [ObservableProperty] private string _lastAction = "";
    [ObservableProperty] private bool _gameModeEnabled;
    [ObservableProperty] private string _gpuSchedulingText = "";
    [ObservableProperty] private bool _isLoadingProcesses;
    public ObservableCollection<ProcessItem> Processes { get; } = new();

    // ---------- Quiet mode ----------
    [ObservableProperty] private string _quietStatus = "";
    [ObservableProperty] private bool _isQuietActive;
    [ObservableProperty] private string _fullPowerButtonText = "Полная мощность на время";

    [RelayCommand]
    private async Task ToggleFullPowerAsync()
    {
        // powercfg takes a moment: keep the UI responsive.
        if (_quiet.IsPaused) await Task.Run(_quiet.Resume);
        else await Task.Run(_quiet.Pause);
        RefreshQuiet();
        RefreshSystem();
    }

    private void RefreshQuiet()
    {
        QuietStatus = _quiet.Status;
        IsQuietActive = _quiet.IsActive;
        FullPowerButtonText = _quiet.IsPaused ? "Вернуть тихий режим" : "Полная мощность на время";
    }

    [RelayCommand]
    private void Boost()
    {
        // Leave quiet mode first, so the boost saves the user's real power settings.
        _quiet.Pause();
        LastAction = _power.EnableMaxPerformance();
        _windows.SetGameMode(true);
        RefreshSystem();
    }

    [RelayCommand]
    private void RestorePower()
    {
        LastAction = _power.Restore();
        _quiet.Resume();
        RefreshSystem();
    }

    partial void OnGameModeEnabledChanged(bool value)
    {
        if (!_loading) _windows.SetGameMode(value);
    }

    [RelayCommand]
    private async Task RefreshProcessesAsync()
    {
        IsLoadingProcesses = true;
        // Enumerating processes with window titles takes ~0.5 s: keep it off the UI thread.
        var list = await Task.Run(() => _processes.GetHeavyProcesses());
        Processes.Clear();
        foreach (var p in list) Processes.Add(new ProcessItem(p));
        IsLoadingProcesses = false;
    }

    [RelayCommand]
    private async Task CloseSelectedAsync()
    {
        var names = Processes.Where(p => p.IsSelected).Select(p => p.Name).ToList();
        if (names.Count == 0)
        {
            await _dialogs.ShowAsync("Ничего не выбрано", "Отметь галочками программы, которые нужно закрыть.");
            return;
        }

        if (!await _dialogs.ConfirmAsync("Закрыть программы?",
                $"{string.Join(", ", names)}\n\nНесохранённые данные в них могут потеряться.", "Закрыть"))
            return;

        int closed = await Task.Run(() => names.Sum(n => _processes.CloseByName(n)));
        LastAction = $"Закрыто процессов: {closed}.";
        await RefreshProcessesAsync();
    }

    private void RefreshSystem()
    {
        _loading = true;   // don't write Game Mode back while we're only reading it
        PowerPlan = _power.GetActivePlanName();
        IsBoosted = _power.IsBoosted;
        GameModeEnabled = _windows.IsGameModeEnabled();
        GpuSchedulingText = _windows.IsGpuSchedulingEnabled() switch
        {
            true => "Аппаратное планирование GPU: включено ✔",
            false => "Аппаратное планирование GPU: выключено. Включи: Параметры → Система → Дисплей → Графика (нужна перезагрузка).",
            null => "Аппаратное планирование GPU: не поддерживается этой видеокартой или драйвером."
        };
        _loading = false;
    }
}
