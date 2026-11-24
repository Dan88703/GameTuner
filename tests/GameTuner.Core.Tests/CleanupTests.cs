using GameTuner.Core.Cleanup;

namespace GameTuner.Core.Tests;

public sealed class CleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GameTunerCleanup_" + Guid.NewGuid().ToString("N"));

    public CleanupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string MakeFile(string relative, int size, TimeSpan age)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact]
    public void Clean_DeletesOldFiles_KeepsNewOnes_AndTheRootFolder()
    {
        string old1 = MakeFile(@"a\old.tmp", 1000, TimeSpan.FromDays(3));
        string old2 = MakeFile(@"b\c\old2.log", 500, TimeSpan.FromDays(2));
        string fresh = MakeFile("fresh.tmp", 100, TimeSpan.FromMinutes(5));

        var target = new FolderCleanupTarget("t", "Test", "", new[] { _root }) { MinAge = TimeSpan.FromDays(1) };

        var scan = target.Scan(CancellationToken.None);
        Assert.Equal(1500, scan.Bytes);
        Assert.Equal(2, scan.Files);

        var result = target.Clean(CancellationToken.None);
        Assert.Equal(1500, result.FreedBytes);
        Assert.Equal(2, result.Deleted);

        Assert.False(File.Exists(old1));
        Assert.False(File.Exists(old2));
        Assert.True(File.Exists(fresh));                       // too new
        Assert.True(Directory.Exists(_root));                  // root is never removed
        Assert.False(Directory.Exists(Path.Combine(_root, "b")));  // emptied subfolders are removed
    }

    [Fact]
    public void Clean_DeletesReadOnlyFiles()
    {
        string path = MakeFile("readonly.tmp", 10, TimeSpan.FromDays(5));
        File.SetAttributes(path, FileAttributes.ReadOnly);

        new FolderCleanupTarget("t", "Test", "", new[] { _root }).Clean(CancellationToken.None);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Clean_SkipsLockedFiles()
    {
        string path = MakeFile("locked.tmp", 10, TimeSpan.FromDays(5));
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = new FolderCleanupTarget("t", "Test", "", new[] { _root }).Clean(CancellationToken.None);

        Assert.Equal(1, result.Skipped);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ExpandPattern_MatchesEveryProfileFolder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "User Data", "Default", "Cache"));
        Directory.CreateDirectory(Path.Combine(_root, "User Data", "Profile 1", "Cache"));
        Directory.CreateDirectory(Path.Combine(_root, "User Data", "System Profile"));   // no Cache inside

        var found = FolderCleanupTarget.ExpandPattern(Path.Combine(_root, "User Data", "*", "Cache")).ToList();

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void MissingFolder_ScansAsEmpty()
    {
        var target = new FolderCleanupTarget("t", "Test", "", new[] { Path.Combine(_root, "does-not-exist") });
        Assert.Equal(0, target.Scan(CancellationToken.None).Bytes);
    }

    [Theory]
    [InlineData(512, "512 Б")]
    [InlineData(1536, "1{0}5 КБ")]
    [InlineData(5L * 1024 * 1024 * 1024, "5{0}0 ГБ")]
    public void ByteSize_Formats(long bytes, string expected) =>
        Assert.Equal(string.Format(expected, System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator),
                     ByteSize.Format(bytes));
}
