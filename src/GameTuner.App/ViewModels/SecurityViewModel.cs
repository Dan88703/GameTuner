using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameTuner.App.Services;
using GameTuner.Core.Security;
using GameTuner.Core.Tuning;
using Microsoft.UI.Xaml.Controls;

namespace GameTuner.App.ViewModels;

public sealed record ThreatRow(string Name, string Details, bool IsActive);

/// <summary>One autostart entry with an on/off switch.</summary>
public partial class StartupItemViewModel : ObservableObject
{
    private readonly IStartupManager _manager;
    private StartupEntry _entry;
    private bool _reverting;

    public StartupItemViewModel(StartupEntry entry, IStartupManager manager)
    {
        _entry = entry;
        _manager = manager;
        _isEnabled = entry.IsEnabled;
    }

    public string Name => _entry.Name;
    public string Path => _entry.FilePath ?? _entry.Command;
    public string Publisher => _entry.Publisher ?? "без цифровой подписи";
    public string Findings => string.Join("\n", _entry.Findings.Select(f => "• " + f));
    public bool HasFindings => _entry.Findings.Count > 0;
    public bool IsSuspicious => _entry.Risk == RiskLevel.Warning;

    /// <summary>All-users entries need admin to switch.</summary>
    public bool CanToggle => !_entry.RequiresAdmin || Elevation.IsElevated;

    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (_reverting) return;
        if (_manager.SetEnabled(_entry, value))
        {
            _entry = _entry with { IsEnabled = value };
        }
        else
        {
            // Windows refused: flip the switch back so the UI tells the truth.
            _reverting = true;
            IsEnabled = !value;
            _reverting = false;
        }
    }
}

/// <summary>
/// "Безопасность" page: Microsoft Defender status, scans and threats, plus the
/// autostart review. All the heavy lifting is in Core (DefenderService,
/// StartupManager); this class only turns results into bindable rows.
/// </summary>
public partial class SecurityViewModel : ObservableObject
{
    private readonly IDefenderService _defender;
    private readonly IStartupManager _startup;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cts;

    public SecurityViewModel(IDefenderService defender, IStartupManager startup, IDialogService dialogs)
    {
        _defender = defender;
        _startup = startup;
        _dialogs = dialogs;
        _ = RefreshAllAsync();
    }

    public bool IsElevated { get; } = Elevation.IsElevated;
    public bool ShowAdminHint => !IsElevated;

    // ---------- Protection ----------
    [ObservableProperty] private string _protectionTitle = "Проверяю защиту…";
    [ObservableProperty] private string _protectionDetail = "";
    [ObservableProperty] private InfoBarSeverity _protectionSeverity = InfoBarSeverity.Informational;
    [ObservableProperty] private bool _defenderActive;

    // ---------- Scan ----------
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle)),
     NotifyCanExecuteChangedFor(nameof(QuickScanCommand), nameof(FullScanCommand), nameof(UpdateSignaturesCommand), nameof(RemoveThreatsCommand))]
    private bool _isBusy;
    [ObservableProperty] private string _status = "";
    public bool IsIdle => !IsBusy;
    private bool CanRunDefender() => IsIdle && IsElevated && DefenderActive;

    // ---------- Threats ----------
    public ObservableCollection<ThreatRow> Threats { get; } = new();
    [ObservableProperty] private bool _hasThreats;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RemoveThreatsCommand))] private bool _hasActiveThreats;

    // ---------- Startup ----------
    public ObservableCollection<StartupItemViewModel> StartupItems { get; } = new();
    [ObservableProperty] private string _startupSummary = "";

    private async Task RefreshAllAsync()
    {
        await RefreshProtectionAsync();
        await RefreshThreatsAsync();
        await RefreshStartupAsync();
    }

    private async Task RefreshProtectionAsync()
    {
        var s = await _defender.GetStatusAsync();
        DefenderActive = s.DefenderActive;
        // The scan buttons depend on DefenderActive too.
        QuickScanCommand.NotifyCanExecuteChanged();
        FullScanCommand.NotifyCanExecuteChanged();
        UpdateSignaturesCommand.NotifyCanExecuteChanged();

        if (s.OtherAntivirus.Count > 0 && !s.DefenderActive)
        {
            ProtectionTitle = $"Защиту обеспечивает {string.Join(", ", s.OtherAntivirus)}";
            ProtectionDetail = "Microsoft Defender работает в пассивном режиме. Проверку запускай в своём антивирусе.";
            ProtectionSeverity = InfoBarSeverity.Success;
        }
        else if (!s.DefenderAvailable)
        {
            ProtectionTitle = "Антивирус не найден";
            ProtectionDetail = "Включи Microsoft Defender в «Безопасности Windows».";
            ProtectionSeverity = InfoBarSeverity.Error;
        }
        else if (!s.RealTimeProtection)
        {
            ProtectionTitle = "Защита в реальном времени выключена";
            ProtectionDetail = "Включи её в «Безопасности Windows» → Защита от вирусов и угроз.";
            ProtectionSeverity = InfoBarSeverity.Error;
        }
        else
        {
            ProtectionTitle = "Microsoft Defender включён";
            ProtectionDetail =
                $"Базы обновлены: {(s.SignaturesUpdated is DateTime d ? d.ToString("dd.MM.yyyy HH:mm") : "неизвестно")}" +
                $" · Быстрая проверка: {Age(s.DaysSinceQuickScan)} · Полная: {Age(s.DaysSinceFullScan)}";
            ProtectionSeverity = s.SignaturesOutdated ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        }
    }

    private static string Age(int? days) => days switch
    {
        null => "не было",
        0 => "сегодня",
        1 => "вчера",
        _ => $"{days} дн. назад"
    };

    private async Task RefreshThreatsAsync()
    {
        var threats = await _defender.GetThreatsAsync();
        Threats.Clear();
        foreach (var t in threats.Take(50))
        {
            string state = t.IsActive ? "АКТИВНА — требует удаления" : "обезврежена (в карантине или удалена)";
            string files = t.Files.Count > 0 ? "\n" + string.Join("\n", t.Files.Take(3)) : "";
            Threats.Add(new ThreatRow(t.Name,
                $"Опасность: {DefenderService.SeverityText(t.Severity)} · {state}" +
                (t.DetectedAt is DateTime when ? $" · найдена {when:dd.MM.yyyy}" : "") + files,
                t.IsActive));
        }
        HasThreats = Threats.Count > 0;
        HasActiveThreats = threats.Any(t => t.IsActive);
    }

    [RelayCommand]
    private async Task RefreshStartupAsync()
    {
        var entries = await Task.Run(_startup.GetEntries);
        StartupItems.Clear();
        foreach (var e in entries) StartupItems.Add(new StartupItemViewModel(e, _startup));

        int enabled = entries.Count(e => e.IsEnabled);
        int suspicious = entries.Count(e => e.Risk == RiskLevel.Warning);
        StartupSummary = $"Всего записей: {entries.Count}, включено: {enabled}" +
                         (suspicious > 0 ? $", требуют внимания: {suspicious} (вверху списка)" : ", подозрительных нет");
    }

    // ---------- Defender actions ----------

    [RelayCommand(CanExecute = nameof(CanRunDefender))]
    private Task UpdateSignaturesAsync() => RunBusyAsync("Обновляю антивирусные базы…", async token =>
    {
        bool ok = await _defender.UpdateSignaturesAsync(token);
        Status = ok ? "Базы обновлены" : "Не удалось обновить базы (проверь интернет)";
    });

    [RelayCommand(CanExecute = nameof(CanRunDefender))]
    private Task QuickScanAsync() => ScanAsync(ScanKind.Quick, "Быстрая проверка… (5–15 минут)");

    [RelayCommand(CanExecute = nameof(CanRunDefender))]
    private async Task FullScanAsync()
    {
        if (!await _dialogs.ConfirmAsync("Полная проверка",
                "Полная проверка просматривает все файлы и может занять 1–3 часа. Ноутбук при этом будет нагружен. Начать?", "Начать"))
            return;
        await ScanAsync(ScanKind.Full, "Полная проверка… (может занять несколько часов)");
    }

    private Task ScanAsync(ScanKind kind, string message) => RunBusyAsync(message, async token =>
    {
        bool found = await _defender.ScanAsync(kind, token);
        await RefreshThreatsAsync();
        await RefreshProtectionAsync();
        Status = found ? "Найдены угрозы — смотри список ниже" : "Проверка завершена: угроз не найдено ✔";
        if (found && HasActiveThreats)
            await _dialogs.ShowAsync("Найдены угрозы", "Defender нашёл угрозы. Нажми «Удалить угрозы», чтобы он их удалил.");
    });

    private bool CanRemove() => IsIdle && IsElevated && HasActiveThreats;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveThreatsAsync()
    {
        if (!await _dialogs.ConfirmAsync("Удалить угрозы?",
                "Microsoft Defender удалит все активные угрозы из списка. Если среди них есть нужный тебе файл, сначала разберись с ним в «Безопасности Windows».",
                "Удалить"))
            return;

        await RunBusyAsync("Defender удаляет угрозы…", async _ =>
        {
            bool ok = await _defender.RemoveActiveThreatsAsync();
            await RefreshThreatsAsync();
            Status = ok && !HasActiveThreats ? "Угрозы удалены ✔" : "Не всё удалось удалить — открой «Безопасность Windows»";
        });
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenWindowsSecurity() => DefenderService.OpenWindowsSecurity();

    [RelayCommand]
    private void RestartAsAdmin()
    {
        if (Elevation.RestartElevated(App.ElevatedRestartArg))
            ((App)Microsoft.UI.Xaml.Application.Current).ExitApp();
    }

    private async Task RunBusyAsync(string message, Func<CancellationToken, Task> action)
    {
        IsBusy = true;
        Status = message;
        _cts = new CancellationTokenSource();
        try
        {
            await action(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "Остановлено";
        }
        finally
        {
            IsBusy = false;
            QuickScanCommand.NotifyCanExecuteChanged();
            FullScanCommand.NotifyCanExecuteChanged();
            UpdateSignaturesCommand.NotifyCanExecuteChanged();
            RemoveThreatsCommand.NotifyCanExecuteChanged();
        }
    }
}
