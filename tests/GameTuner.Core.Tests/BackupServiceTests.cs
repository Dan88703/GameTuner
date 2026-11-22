using GameTuner.Core.Backup;

namespace GameTuner.Core.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GameTunerTests_" + Guid.NewGuid().ToString("N"));

    public BackupServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void BackupAndRestore_RoundTrip_WithDuplicateFileNames()
    {
        // Two Steam accounts → two files with the same name in different folders.
        string a = Path.Combine(_dir, "acc1", "cs2_video.txt");
        string b = Path.Combine(_dir, "acc2", "cs2_video.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(a)!);
        Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        File.WriteAllText(a, "original A");
        File.WriteAllText(b, "original B");

        var service = new BackupService(Path.Combine(_dir, "backups"));
        service.Backup("cs2", new[] { a, b });

        File.WriteAllText(a, "changed");
        File.WriteAllText(b, "changed");

        Assert.Equal(2, service.RestoreLatest("cs2"));
        Assert.Equal("original A", File.ReadAllText(a));
        Assert.Equal("original B", File.ReadAllText(b));
    }

    [Fact]
    public void OldBackups_ArePruned()
    {
        string file = Path.Combine(_dir, "options.txt");
        File.WriteAllText(file, "x");
        var service = new BackupService(Path.Combine(_dir, "backups"));

        for (int i = 0; i < BackupService.MaxBackupsPerGame + 5; i++)
        {
            service.Backup("mc", new[] { file });
            Thread.Sleep(2); // backup folder names have millisecond precision
        }

        Assert.Equal(BackupService.MaxBackupsPerGame, service.ListBackups("mc").Count);
    }

    [Fact]
    public void RestoreLatest_WithoutBackups_ReturnsZero() =>
        Assert.Equal(0, new BackupService(Path.Combine(_dir, "empty")).RestoreLatest("nothing"));
}
