using System.IO;
using System.Linq;
using System.Text;
using DLSS_Swapper.Swapping;
using Xunit;

namespace DLSS_Swapper.Tests;

/// <summary>
/// A saved original is checked against the hash recorded when it was saved, before it is put back.
/// </summary>
/// <remarks>
/// <para>
/// Reset used to check that the backup file existed and nothing else. A backup corrupted by the
/// disk, or replaced by another tool, was restored without comment - and the game then ran an
/// unknown dll while the row said it had been returned to what it shipped with. That is the
/// harmful kind of wrong: a restore is the button people press when something has already gone
/// sideways, and it has to be the one thing that does what it says.
/// </para>
/// <para>
/// The check happens before anything is staged, so a refused restore leaves both the swapped dll
/// and the suspect backup exactly where they were for someone to look at.
/// </para>
/// </remarks>
public class DllSwapExecutorBackupIntegrityTests
{
    const string Target = @"C:\game\nvngx_dlss.dll";
    static readonly string Backup = DllSwapExecutor.GetBackupPath(Target);

    static string HashOf(string contents)
    {
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(contents)))
        {
            return FileHashes.Md5Hex(stream);
        }
    }

    static FakeFileSystem SwappedGame(string backupContents = "original")
    {
        return new FakeFileSystem()
            .AddFile(Target, "swapped")
            .AddFile(Backup, backupContents);
    }

    [Fact]
    public void ABackupThatMatchesItsRecordIsRestored()
    {
        var fs = SwappedGame();

        var result = new DllSwapExecutor(fs).Reset(new[] { new ResetTarget(Target, HashOf("original")) });

        Assert.True(result.Success);
        Assert.Equal("original", fs.ReadFile(Target));
        Assert.False(fs.FileExists(Backup));
    }

    [Fact]
    public void ABackupThatDoesNotMatchIsRefusedAndNothingMoves()
    {
        var fs = SwappedGame(backupContents: "something else entirely");

        var result = new DllSwapExecutor(fs).Reset(new[] { new ResetTarget(Target, HashOf("original")) });

        Assert.False(result.Success);
        Assert.Equal(SwapFailure.BackupTampered, result.Failure);
        Assert.Equal(Backup, result.FailedPath);

        // Both files are exactly as they were, for someone to look at.
        Assert.Equal("swapped", fs.ReadFile(Target));
        Assert.Equal("something else entirely", fs.ReadFile(Backup));
    }

    [Fact]
    public void NothingIsStagedBeforeEveryBackupHasBeenChecked()
    {
        // Two targets, the second one's backup tampered. The first must not have been staged or
        // touched: the whole point of checking first is that a refusal costs nothing.
        var second = @"C:\game\bin\nvngx_dlss.dll";
        var secondBackup = DllSwapExecutor.GetBackupPath(second);
        var fs = SwappedGame()
            .AddFile(second, "swapped")
            .AddFile(secondBackup, "not the original");

        var result = new DllSwapExecutor(fs).Reset(new[]
        {
            new ResetTarget(Target, HashOf("original")),
            new ResetTarget(second, HashOf("original")),
        });

        Assert.Equal(SwapFailure.BackupTampered, result.Failure);
        Assert.Equal(secondBackup, result.FailedPath);
        Assert.Equal("swapped", fs.ReadFile(Target));
        Assert.Equal("swapped", fs.ReadFile(second));
        Assert.Equal(4, fs.AllPaths.Count);
        Assert.DoesNotContain(fs.AllPaths, x => x.EndsWith(".staged", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ARecordWithNoHashIsRestoredWithoutTheCheck()
    {
        // Backups saved by an older build carry no hash. They are still the only original there is,
        // and refusing them would strand every game backed up before hashes were kept.
        var fs = SwappedGame();

        var result = new DllSwapExecutor(fs).Reset(new[] { new ResetTarget(Target, null) });

        Assert.True(result.Success);
        Assert.Equal("original", fs.ReadFile(Target));
    }

    [Fact]
    public void TheHashComparisonIgnoresCase()
    {
        var fs = SwappedGame();

        var result = new DllSwapExecutor(fs).Reset(new[] { new ResetTarget(Target, HashOf("original").ToLowerInvariant()) });

        Assert.True(result.Success);
    }

    [Fact]
    public void TheOldOverloadStillRestoresWithoutHashes()
    {
        // Callers that only know the paths - the command line among them - keep working unchanged.
        var fs = SwappedGame();

        var result = new DllSwapExecutor(fs).Reset(new[] { Target });

        Assert.True(result.Success);
        Assert.Equal("original", fs.ReadFile(Target));
    }

    [Fact]
    public void AMissingBackupIsStillReportedAsMissingNotTampered()
    {
        var fs = new FakeFileSystem().AddFile(Target, "swapped");

        var result = new DllSwapExecutor(fs).Reset(new[] { new ResetTarget(Target, HashOf("original")) });

        Assert.Equal(SwapFailure.BackupMissing, result.Failure);
    }

    [Fact]
    public void Md5HexIsUpperCaseAndStable()
    {
        // The database stores upper-case hex. The helper has to produce the same shape or every
        // comparison against a stored value would need to know to normalise.
        var hex = HashOf("original");

        Assert.Equal(32, hex.Length);
        Assert.Equal(hex, hex.ToUpperInvariant());
        Assert.Equal(hex, HashOf("original"));
        Assert.True(hex.All(c => "0123456789ABCDEF".Contains(c)));
    }
}
