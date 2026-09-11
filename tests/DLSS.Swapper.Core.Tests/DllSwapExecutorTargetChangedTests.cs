using System.IO;
using System.Text;
using DLSS_Swapper.Swapping;
using Xunit;

namespace DLSS_Swapper.Tests;

/// <summary>
/// A restore does not write over a dll that changed since the app last touched it, unless told to.
/// </summary>
/// <remarks>
/// "Put the original back" and "erase whatever somebody did to this file since" are different
/// requests, and the second one has to be made on purpose. The executor knows nothing about
/// users; it refuses with a reason the caller can turn into a question, and a caller that has
/// asked passes no expected hash and the check is skipped.
/// </remarks>
public class DllSwapExecutorTargetChangedTests
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

    static FakeFileSystem Game(string onDisk)
    {
        return new FakeFileSystem()
            .AddFile(Target, onDisk)
            .AddFile(Backup, "original");
    }

    [Fact]
    public void ADllStillAsTheAppLeftItIsRestored()
    {
        var fs = Game("swapped");

        var result = new DllSwapExecutor(fs).Reset(new[] { new ResetTarget(Target, HashOf("original"), HashOf("swapped")) });

        Assert.True(result.Success);
        Assert.Equal("original", fs.ReadFile(Target));
        Assert.False(fs.FileExists(Backup));
    }

    [Fact]
    public void ADllChangedSinceIsRefusedAndNothingMoves()
    {
        var fs = Game("edited by hand since the swap");

        var result = new DllSwapExecutor(fs).Reset(new[] { new ResetTarget(Target, HashOf("original"), HashOf("swapped")) });

        Assert.False(result.Success);
        Assert.Equal(SwapFailure.TargetChanged, result.Failure);
        Assert.Equal(Target, result.FailedPath);

        // Both files exactly as they were: the edit is intact and the original is still saved.
        Assert.Equal("edited by hand since the swap", fs.ReadFile(Target));
        Assert.Equal("original", fs.ReadFile(Backup));
        Assert.Equal(2, fs.AllPaths.Count);
    }

    [Fact]
    public void NoExpectationMeansNoQuestion()
    {
        // The two-argument shape every existing caller used, and the shape a caller uses after the
        // user has said "restore anyway".
        var fs = Game("edited by hand since the swap");

        var result = new DllSwapExecutor(fs).Reset(new[] { new ResetTarget(Target, HashOf("original")) });

        Assert.True(result.Success);
        Assert.Equal("original", fs.ReadFile(Target));
    }

    [Fact]
    public void ATamperedBackupIsReportedBeforeAChangedTarget()
    {
        // Both wrong. The backup is the one that cannot be fixed by answering a question, so it is
        // the one reported.
        var fs = new FakeFileSystem()
            .AddFile(Target, "edited")
            .AddFile(Backup, "not the original either");

        var result = new DllSwapExecutor(fs).Reset(new[] { new ResetTarget(Target, HashOf("original"), HashOf("swapped")) });

        Assert.False(result.Success);
        Assert.Equal(SwapFailure.BackupTampered, result.Failure);
    }

    [Fact]
    public void OneChangedLocationHoldsTheWholeGame()
    {
        const string Second = @"C:\game\bin\nvngx_dlss.dll";
        var fs = Game("swapped")
            .AddFile(Second, "edited")
            .AddFile(DllSwapExecutor.GetBackupPath(Second), "original");

        var result = new DllSwapExecutor(fs).Reset(new[]
        {
            new ResetTarget(Target, HashOf("original"), HashOf("swapped")),
            new ResetTarget(Second, HashOf("original"), HashOf("swapped")),
        });

        Assert.False(result.Success);
        Assert.Equal(SwapFailure.TargetChanged, result.Failure);
        Assert.Equal(Second, result.FailedPath);

        // The first location was restorable and was still not touched.
        Assert.Equal("swapped", fs.ReadFile(Target));
        Assert.True(fs.FileExists(Backup));
    }
}
