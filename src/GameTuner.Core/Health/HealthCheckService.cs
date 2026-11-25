using GameTuner.Core.Automation;
using GameTuner.Core.Cleanup;
using GameTuner.Core.Hardware;
using GameTuner.Core.Monitoring;
using GameTuner.Core.Security;
using GameTuner.Core.Settings;
using GameTuner.Core.Tuning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTuner.Core.Health;

public enum HealthStatus { Ok = 0, Info = 1, Warning = 2, Problem = 3 }

/// <summary>One line of the health report.</summary>
public sealed record HealthItem(string Id, string Title, string Detail, HealthStatus Status)
{
    /// <summary>GameTuner can fix this by itself on "Improve".</summary>
    public bool CanAutoFix { get; init; }

    /// <summary>What the fix will do, e.g. "Удалить 3,2 ГБ мусора".</summary>
    public string? FixDescription { get; init; }

    /// <summary>What the user should do when it can't (or shouldn't) be fixed automatically.</summary>
    public string? Advice { get; init; }

    public string? LinkText { get; init; }
    public Uri? Link { get; init; }
}

public sealed record HealthReport(IReadOnlyList<HealthItem> Items, int Score)
{
    public int FixableCount => Items.Count(i => i.CanAutoFix && i.Status != HealthStatus.Ok);
}

public sealed record ImproveResult(IReadOnlyList<string> Done, IReadOnlyList<string> Failed, long FreedBytes);

public interface IHealthCheckService
{
    Task<HealthReport> CheckAsync(SystemInfo info, IProgress<string>? progress = null, CancellationToken token = default);
    Task<ImproveResult> ImproveAsync(HealthReport report, IProgress<string>? progress = null, CancellationToken token = default);
}

/// <summary>
/// The "Улучшить ноутбук" button.
///
/// Step 1 — CheckAsync: looks at ~12 things that commonly make a Windows PC
/// slow, hot or unsafe and rates each one (OK / info / warning / problem).
/// Step 2 — ImproveAsync: applies ONLY the fixes that are safe and reversible
/// (junk cleanup with the safe default categories, Game Mode, quiet mode,
/// Defender signature update). Everything that is the user's decision (driver
/// update, restart, disabling startup apps, buying an SSD) stays an advice.
///
/// Every check is wrapped in its own try/catch: one failing check must not
/// break the whole report.
/// </summary>
public sealed class HealthCheckService : IHealthCheckService
{
    private readonly IDiskCleanupService _cleanup;
    private readonly IWindowsGameSettings _windows;
    private readonly ISettingsService _settings;
    private readonly IDefenderService _defender;
    private readonly IStartupManager _startup;
    private readonly ISensorService _sensors;
    private readonly IGameWatcher _games;
    private readonly ILogger<HealthCheckService> _log;

    public HealthCheckService(IDiskCleanupService cleanup, IWindowsGameSettings windows, ISettingsService settings,
        IDefenderService defender, IStartupManager startup, ISensorService sensors, IGameWatcher games,
        ILogger<HealthCheckService>? log = null)
    {
        _cleanup = cleanup;
        _windows = windows;
        _settings = settings;
        _defender = defender;
        _startup = startup;
        _sensors = sensors;
        _games = games;
        _log = log ?? NullLogger<HealthCheckService>.Instance;
    }

    public async Task<HealthReport> CheckAsync(SystemInfo info, IProgress<string>? progress = null, CancellationToken token = default)
    {
        var items = new List<HealthItem>();

        async Task Check(string what, Func<Task<HealthItem?>> check)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report($"Проверяю: {what}…");
            try
            {
                if (await check() is HealthItem item) items.Add(item);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Health check '{Check}' failed", what);
            }
        }

        await Check("место на диске", () => Task.FromResult<HealthItem?>(CheckDiskSpace()));
        await Check("мусор", () => CheckJunkAsync(token));
        await Check("антивирус", CheckDefenderAsync);
        await Check("автозагрузку", () => Task.Run(CheckStartup, token));
        await Check("драйвер видеокарты", () => Task.FromResult(CheckGpuDriver(info)));
        await Check("время работы", () => Task.FromResult<HealthItem?>(CheckUptime()));
        await Check("температуру", () => Task.FromResult(CheckIdleTemperature()));
        await Check("память", () => Task.FromResult<HealthItem?>(CheckMemory()));
        await Check("игровой режим", () => Task.FromResult<HealthItem?>(CheckGameMode()));
        await Check("тихий режим", () => Task.FromResult(CheckQuietMode(info)));
        await Check("планирование GPU", () => Task.FromResult(CheckGpuScheduling()));
        await Check("тип диска", () => Task.FromResult(CheckDiskType(info)));

        var sorted = items.OrderByDescending(i => i.Status).ToList();
        return new HealthReport(sorted, ComputeScore(sorted));
    }

    /// <summary>100 minus penalties: problem −20, warning −8, info −2. Never below 0.</summary>
    public static int ComputeScore(IEnumerable<HealthItem> items) =>
        Math.Clamp(100 - items.Sum(i => i.Status switch
        {
            HealthStatus.Problem => 20,
            HealthStatus.Warning => 8,
            HealthStatus.Info => 2,
            _ => 0
        }), 0, 100);

    public async Task<ImproveResult> ImproveAsync(HealthReport report, IProgress<string>? progress = null, CancellationToken token = default)
    {
        var done = new List<string>();
        var failed = new List<string>();
        long freed = 0;

        foreach (var item in report.Items.Where(i => i.CanAutoFix && i.Status != HealthStatus.Ok))
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(item.FixDescription ?? item.Title);
            try
            {
                switch (item.Id)
                {
                    case "junk":
                        var summary = await _cleanup.CleanAsync(SafeJunkTargets().Select(t => t.Id), progress, token);
                        freed += summary.FreedBytes;
                        done.Add($"Удалён мусор: освобождено {ByteSize.Format(summary.FreedBytes)}");
                        break;

                    case "game-mode":
                        _windows.SetGameMode(true);
                        done.Add("Включён игровой режим Windows");
                        break;

                    case "quiet-mode":
                        _settings.Current.QuietMode = QuietModePolicy.Always;
                        _settings.Save();
                        done.Add("Включён тихий режим вне игр");
                        break;

                    case "defender-signatures":
                        if (await _defender.UpdateSignaturesAsync(token)) done.Add("Обновлены антивирусные базы");
                        else failed.Add("Не удалось обновить антивирусные базы (нужен интернет)");
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Improve step {Id} failed", item.Id);
                failed.Add($"{item.Title}: {ex.Message}");
            }
        }
        return new ImproveResult(done, failed, freed);
    }

    // ------------------------------------------------------------------
    // Checks
    // ------------------------------------------------------------------

    private HealthItem CheckDiskSpace()
    {
        var d = _cleanup.GetSystemDrive();
        double freePct = d.TotalBytes > 0 ? d.FreeBytes * 100.0 / d.TotalBytes : 100;
        var status = DiskSpaceStatus(freePct);
        return new HealthItem("disk-space", "Место на диске " + d.Name,
            $"Свободно {ByteSize.Format(d.FreeBytes)} из {ByteSize.Format(d.TotalBytes)} ({freePct:0}%)", status)
        {
            // Windows needs free space for updates, the page file and hibernation;
            // below ~10% everything slows down and updates start failing.
            Advice = status == HealthStatus.Ok ? null
                : "Удали ненужные игры и большие файлы из «Загрузок». Посмотреть, что занимает место: Параметры → Система → Память."
        };
    }

    internal static HealthStatus DiskSpaceStatus(double freePercent) =>
        freePercent < 10 ? HealthStatus.Problem : freePercent < 15 ? HealthStatus.Warning : HealthStatus.Ok;

    /// <summary>Cleanup categories that are safe to run without asking: the defaults the user is allowed to clean.</summary>
    private IEnumerable<CleanupTarget> SafeJunkTargets() =>
        _cleanup.Targets.Where(t => t.SelectedByDefault && (!t.RequiresAdmin || Elevation.IsElevated));

    private async Task<HealthItem?> CheckJunkAsync(CancellationToken token)
    {
        var scan = await _cleanup.ScanAsync(null, token);
        long bytes = SafeJunkTargets().Sum(t => scan.TryGetValue(t.Id, out var r) ? r.Bytes : 0);
        long adminOnly = Elevation.IsElevated ? 0
            : _cleanup.Targets.Where(t => t.SelectedByDefault && t.RequiresAdmin)
                .Sum(t => scan.TryGetValue(t.Id, out var r) ? r.Bytes : 0);

        var status = bytes > 1L << 30 ? HealthStatus.Warning
                   : bytes > 200L << 20 ? HealthStatus.Info
                   : HealthStatus.Ok;

        return new HealthItem("junk", "Мусор на диске",
            status == HealthStatus.Ok ? "Мусора почти нет" : $"Можно безопасно удалить {ByteSize.Format(bytes)}", status)
        {
            CanAutoFix = status != HealthStatus.Ok,
            FixDescription = $"Удалить мусор ({ByteSize.Format(bytes)})",
            Advice = adminOnly > 0
                ? $"Ещё {ByteSize.Format(adminOnly)} в системных папках — их можно очистить в режиме администратора."
                : null
        };
    }

    private async Task<HealthItem?> CheckDefenderAsync()
    {
        var s = await _defender.GetStatusAsync();

        if (s.OtherAntivirus.Count > 0 && !s.DefenderActive)
            return new HealthItem("antivirus", "Антивирус", $"Защиту обеспечивает {string.Join(", ", s.OtherAntivirus)}", HealthStatus.Ok);

        if (!s.DefenderAvailable)
            return new HealthItem("antivirus", "Антивирус", "Антивирус не найден", HealthStatus.Problem)
            {
                Advice = "Включи Microsoft Defender: Безопасность Windows → Защита от вирусов и угроз.",
                LinkText = "Открыть «Безопасность Windows»",
                Link = new Uri("windowsdefender://threat")
            };

        if (!s.RealTimeProtection)
            return new HealthItem("antivirus", "Антивирус", "Защита в реальном времени выключена", HealthStatus.Problem)
            {
                Advice = "Включи «Защиту в режиме реального времени» — без неё вирус заметят только при ручной проверке.",
                LinkText = "Открыть «Безопасность Windows»",
                Link = new Uri("windowsdefender://threat")
            };

        if (s.SignaturesOutdated)
            return new HealthItem("defender-signatures", "Антивирусные базы",
                $"Последнее обновление: {s.SignaturesUpdated:dd.MM.yyyy}", HealthStatus.Warning)
            {
                CanAutoFix = Elevation.IsElevated,
                FixDescription = "Обновить антивирусные базы",
                Advice = Elevation.IsElevated ? null : "Обновить базы можно в режиме администратора или в «Безопасности Windows»."
            };

        if (s.DaysSinceQuickScan is null or > 14)
            return new HealthItem("antivirus", "Антивирус", "Защита включена, но давно не было проверки", HealthStatus.Info)
            {
                Advice = "Запусти быструю проверку на вкладке «Безопасность» — это 5–10 минут."
            };

        return new HealthItem("antivirus", "Антивирус", "Microsoft Defender включён, базы свежие", HealthStatus.Ok);
    }

    private HealthItem? CheckStartup()
    {
        var entries = _startup.GetEntries();
        int enabled = entries.Count(e => e.IsEnabled);
        int suspicious = entries.Count(e => e.IsEnabled && e.Risk == RiskLevel.Warning);

        if (suspicious > 0)
            return new HealthItem("startup", "Автозагрузка",
                $"Подозрительных записей: {suspicious} (всего включено {enabled})", HealthStatus.Warning)
            {
                Advice = "Открой «Безопасность → Автозагрузка», посмотри отмеченные записи и отключи незнакомые."
            };

        if (enabled > 12)
            return new HealthItem("startup", "Автозагрузка", $"{enabled} программ запускаются вместе с Windows", HealthStatus.Info)
            {
                Advice = "Чем больше программ в автозагрузке, тем дольше включается ноутбук. Отключи ненужные на вкладке «Безопасность»."
            };

        return new HealthItem("startup", "Автозагрузка", $"{enabled} программ при запуске — нормально", HealthStatus.Ok);
    }

    private static HealthItem? CheckGpuDriver(SystemInfo info)
    {
        var gpu = info.PrimaryGpu;
        if (gpu?.DriverDate is not DateTime date) return null;

        int days = (int)(DateTime.Now - date).TotalDays;
        var status = days > 365 ? HealthStatus.Warning : days > 180 ? HealthStatus.Info : HealthStatus.Ok;
        var (linkText, link) = DriverLink(gpu.Name);

        return new HealthItem("gpu-driver", "Драйвер видеокарты",
            $"{gpu.Name}: драйвер от {date:dd.MM.yyyy} ({days} дн.)", status)
        {
            // New drivers regularly add 5–20% FPS in recent games and fix crashes.
            Advice = status == HealthStatus.Ok ? null : "Новые драйверы часто дают прирост FPS в свежих играх и исправляют вылеты.",
            LinkText = status == HealthStatus.Ok ? null : linkText,
            Link = status == HealthStatus.Ok ? null : link
        };
    }

    internal static (string Text, Uri Link) DriverLink(string gpuName)
    {
        string n = gpuName.ToLowerInvariant();
        if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("rtx") || n.Contains("gtx"))
            return ("Драйверы NVIDIA", new Uri("https://www.nvidia.com/Download/index.aspx"));
        if (n.Contains("amd") || n.Contains("radeon"))
            return ("Драйверы AMD", new Uri("https://www.amd.com/en/support/download/drivers.html"));
        return ("Драйверы Intel", new Uri("https://www.intel.com/content/www/us/en/support/detect.html"));
    }

    private static HealthItem CheckUptime()
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var status = uptime.TotalDays > 7 ? HealthStatus.Warning : uptime.TotalDays > 3 ? HealthStatus.Info : HealthStatus.Ok;
        return new HealthItem("uptime", "Без перезагрузки", $"{(int)uptime.TotalDays} дн. {uptime.Hours} ч.", status)
        {
            // Windows "Fast startup" makes Shut down a kind of hibernation — the kernel
            // keeps running, so only Restart really starts fresh.
            Advice = status == HealthStatus.Ok ? null
                : "Перезагрузи ноутбук (именно «Перезагрузка», а не «Завершение работы» — при быстром запуске Windows выключение не очищает память)."
        };
    }

    private HealthItem? CheckIdleTemperature()
    {
        var s = _sensors.Latest;
        if (s.CpuTempC is not double temp) return null;

        // Only meaningful when the PC is idle: hot under load is normal.
        if (_games.Current != null || s.CpuLoad > 25) return null;

        var status = temp > 75 ? HealthStatus.Warning : HealthStatus.Ok;
        return new HealthItem("idle-temp", "Температура в простое", $"Процессор {temp:0} °C при нагрузке {s.CpuLoad:0}%", status)
        {
            Advice = status == HealthStatus.Ok ? null
                : "Для простоя это горячо: скорее всего, забит пылью радиатор или высохла термопаста. Чистка системы охлаждения снизит температуру на 10–20 °C."
        };
    }

    private HealthItem CheckMemory()
    {
        var s = _sensors.Latest;
        var status = s.RamPercent > 85 ? HealthStatus.Warning : s.RamPercent > 70 ? HealthStatus.Info : HealthStatus.Ok;
        return new HealthItem("memory", "Оперативная память", $"Занято {s.RamPercent}% ({s.RamUsedGb} из {s.RamTotalGb} ГБ)", status)
        {
            Advice = status == HealthStatus.Ok ? null
                : "Много памяти занято ещё до игры. Закрой браузер с вкладками и отключи лишнее в автозагрузке; в «Оптимизации» видно, кто ест больше всего."
        };
    }

    private HealthItem CheckGameMode()
    {
        bool on = _windows.IsGameModeEnabled();
        return new HealthItem("game-mode", "Игровой режим Windows", on ? "Включён" : "Выключен",
            on ? HealthStatus.Ok : HealthStatus.Warning)
        {
            CanAutoFix = !on,
            FixDescription = "Включить игровой режим Windows"
        };
    }

    private HealthItem? CheckQuietMode(SystemInfo info)
    {
        if (!info.IsLaptop) return null;   // the cooler/quieter/battery benefit is laptop-specific
        bool off = _settings.Current.QuietMode == QuietModePolicy.Off;
        return new HealthItem("quiet-mode", "Тихий режим вне игр", off ? "Выключен" : "Включён",
            off ? HealthStatus.Info : HealthStatus.Ok)
        {
            CanAutoFix = off,
            FixDescription = "Включить тихий режим вне игр",
            Advice = off ? "Без турбо-буста вне игр ноутбук холоднее, тише и дольше работает от батареи." : null
        };
    }

    private HealthItem? CheckGpuScheduling()
    {
        if (_windows.IsGpuSchedulingEnabled() != false) return null;   // on, or not supported
        return new HealthItem("hags", "Аппаратное планирование GPU", "Выключено", HealthStatus.Info)
        {
            Advice = "Включи его: Параметры → Система → Дисплей → Графика → «Изменить параметры графики по умолчанию», затем перезагрузи. Немного снижает задержку в играх.",
            LinkText = "Открыть настройки графики",
            Link = new Uri("ms-settings:display-advancedgraphics")
        };
    }

    private static HealthItem? CheckDiskType(SystemInfo info)
    {
        if (info.SystemDriveIsSsd != false) return null;
        return new HealthItem("hdd", "Системный диск", "Windows стоит на HDD", HealthStatus.Warning)
        {
            Advice = "Переход на SSD — самое заметное улучшение для старого ноутбука: Windows и игры будут загружаться в 3–5 раз быстрее."
        };
    }
}
