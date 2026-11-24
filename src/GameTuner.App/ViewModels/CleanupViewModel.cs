using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameTuner.App.Services;
using GameTuner.Core.Cleanup;
using GameTuner.Core.Tuning;

namespace GameTuner.App.ViewModels;

/// <summary>One category row with a checkbox on the "Очистка" page.</summary>
public partial class CleanupItemViewModel : ObservableObject
{
    private readonly CleanupViewModel _owner;

    public CleanupItemViewModel(CleanupTarget target, CleanupViewModel owner)
    {
        Target = target;
        _owner = owner;
        CanSelect = !target.RequiresAdmin || Elevation.IsElevated;
        IsSelected = target.SelectedByDefault && CanSelect;
    }

    public CleanupTarget Target { get; }
    public string Name => Target.Name;
    public string Description => Target.Description;
    public string Warning => Target.Warning ?? "";
    public bool HasWarning => Target.Warning != null;

    /// <summary>Admin-only items stay visible but can't be ticked in normal mode.</summary>
    public bool CanSelect { get; }
    public bool NeedsAdmin => Target.RequiresAdmin && !Elevation.IsElevated;

    public long Bytes { get; private set; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _sizeText = "—";
    [ObservableProperty] private string _note = "";

    partial void OnIsSelectedChanged(bool value) => _owner.UpdateTotal();

    public void SetScan(CleanupScanResult r)
    {
        Bytes = r.SizeUnknown ? 0 : r.Bytes;
        SizeText = r.SizeUnknown ? "?" : ByteSize.Format(r.Bytes);
        Note = r.Note ?? (r.SizeUnknown ? "" : $"файлов: {r.Files}");
    }
}

/// <summary>
/// "Очистка" page: scan → user ticks categories → clean → show the real
/// freed space. Scanning and cleaning run on background threads; progress
/// text comes back through IProgress, which posts to the UI thread for us.
/// </summary>
public partial class CleanupViewModel : ObservableObject
{
    private readonly IDiskCleanupService _cleanup;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cts;

    public CleanupViewModel(IDiskCleanupService cleanup, IDialogService dialogs)
    {
        _cleanup = cleanup;
        _dialogs = dialogs;
        foreach (var t in cleanup.Targets) Items.Add(new CleanupItemViewModel(t, this));
        RefreshDrive();
    }

    public ObservableCollection<CleanupItemViewModel> Items { get; } = new();

    public bool ShowAdminHint => !Elevation.IsElevated;

    [ObservableProperty] private string _driveText = "";
    [ObservableProperty] private double _driveUsedPercent;
    [ObservableProperty] private string _totalText = "Нажми «Сканировать», чтобы узнать, сколько места можно освободить.";
    [ObservableProperty] private string _status = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle)),
     NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(CleanCommand))]
    private bool _isBusy;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CleanCommand))] private bool _hasScanned;

    public bool IsIdle => !IsBusy;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ScanAsync()
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            // Progress<T> captures the UI thread when created here, so Report()
            // from the background thread updates Status safely.
            var progress = new Progress<string>(s => Status = s);
            var results = await _cleanup.ScanAsync(progress, _cts.Token);

            foreach (var item in Items)
                if (results.TryGetValue(item.Target.Id, out var r)) item.SetScan(r);

            HasScanned = true;
            Status = "";
            UpdateTotal();
        }
        catch (OperationCanceledException)
        {
            Status = "Сканирование отменено";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanClean() => IsIdle && HasScanned;

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanAsync()
    {
        var selected = Items.Where(i => i.IsSelected && i.CanSelect).ToList();
        if (selected.Count == 0)
        {
            await _dialogs.ShowAsync("Ничего не выбрано", "Отметь галочками, что нужно очистить.");
            return;
        }

        string list = string.Join("\n", selected.Select(i => $"• {i.Name} — {i.SizeText}"));
        string warnings = string.Join("\n", selected.Where(i => i.HasWarning).Select(i => $"⚠ {i.Name}: {i.Warning}"));
        if (!await _dialogs.ConfirmAsync("Очистить диск?",
                list + (warnings.Length > 0 ? "\n\n" + warnings : ""), "Очистить"))
            return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<string>(s => Status = s);
            var summary = await _cleanup.CleanAsync(selected.Select(i => i.Target.Id), progress, _cts.Token);

            RefreshDrive();
            string text = $"Освобождено: {ByteSize.Format(summary.FreedBytes)}\n" +
                          $"Удалено файлов: {summary.Deleted}" +
                          (summary.Skipped > 0 ? $", пропущено занятых: {summary.Skipped}" : "");
            if (summary.Notes.Count > 0) text += "\n\n" + string.Join("\n", summary.Notes);

            Status = $"Готово — освобождено {ByteSize.Format(summary.FreedBytes)}";
            await _dialogs.ShowAsync("Очистка завершена", text);
        }
        catch (OperationCanceledException)
        {
            Status = "Очистка остановлена";
        }
        finally
        {
            IsBusy = false;
        }

        await ScanAsync();   // show what's left
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void RestartAsAdmin()
    {
        if (Elevation.RestartElevated(App.ElevatedRestartArg))
            ((App)Microsoft.UI.Xaml.Application.Current).ExitApp();
    }

    internal void UpdateTotal()
    {
        if (!HasScanned) return;
        long total = Items.Where(i => i.IsSelected && i.CanSelect).Sum(i => i.Bytes);
        bool unknown = Items.Any(i => i.IsSelected && i.CanSelect && i.SizeText == "?");
        TotalText = $"Выбрано к очистке: {ByteSize.Format(total)}" + (unknown ? " + старые компоненты Windows" : "");
    }

    private void RefreshDrive()
    {
        try
        {
            var d = _cleanup.GetSystemDrive();
            DriveUsedPercent = d.UsedPercent;
            DriveText = $"Диск {d.Name}  ·  свободно {ByteSize.Format(d.FreeBytes)} из {ByteSize.Format(d.TotalBytes)}";
        }
        catch (Exception)
        {
            DriveText = "Не удалось прочитать диск";
        }
    }
}
