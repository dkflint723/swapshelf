using System;
using System.Linq;
using DLSS_Swapper.Swapping;
using Xunit;

namespace DLSS_Swapper.Tests;

/// <summary>
/// Putting back what a swap the previous session did not finish had already changed.
/// </summary>
/// <remarks>
/// The scenarios are the disk as the executor leaves it at each point it could be interrupted,
/// built by hand: there is no way to kill a test half way through a method and no wish for one.
/// Every path is checked afterwards, because "the game is as it was" is the whole claim.
/// </remarks>
public class SwapRecoveryTests
{
    const string Target = @"C:\game\nvngx_dlss.dll";
    const string SecondTarget = @"C:\game\bin\nvngx_dlss.dll";
    static readonly string Backup = DllSwapExecutor.GetBackupPath(Target);
    static readonly string SecondBackup = DllSwapExecutor.GetBackupPath(SecondTarget);

    static OperationRecord Committing(params string[] targets)
    {
        var record = new OperationRecord()
        {
            Id = "op-1",
            Kind = OperationKind.Swap,
            State = OperationState.Committing,
            GameId = "game-1",
            GameTitle = "Some Game",
            SourcePath = @"C:\library\nvngx_dlss.dll",
            StartedAtUtc = DateTime.UtcNow,
        };
        record.TargetPaths.AddRange(targets);
        foreach (var target in targets)
        {
            record.CreatedBackupPaths.Add(DllSwapExecutor.GetBackupPath(target));
        }
        return record;
    }

    [Fact]
    public void AGameLeftHalfSwappedIsPutBackWhole()
    {
        // Killed between the two renames: the first location holds the new dll with the old one
        // beside it, the second still holds the old dll with the staged copy beside it. Both got a
        // backup before anything was staged.
        var fs = new FakeFileSystem()
            .AddFile(Target, "new")
            .AddFile(Target + DllSwapExecutor.PreviousSuffix, "old-1")
            .AddFile(Backup, "old-1")
            .AddFile(SecondTarget, "old-2")
            .AddFile(SecondTarget + DllSwapExecutor.StagedSuffix, "new")
            .AddFile(SecondBackup, "old-2");
        var journal = new FakeOperationJournal().With(Committing(Target, SecondTarget));

        var report = SwapRecovery.Recover(fs, journal);

        Assert.Equal("old-1", fs.ReadFile(Target));
        Assert.Equal("old-2", fs.ReadFile(SecondTarget));

        // Temp files gone, and the backups this swap made gone with them - the in-process rollback
        // does the same, and the game holds its originals again so they protect nothing.
        Assert.Equal(new[] { Target, SecondTarget }.OrderBy(x => x), fs.AllPaths.OrderBy(x => x));

        var operation = Assert.Single(report.Operations);
        Assert.True(operation.IsComplete);
        Assert.Equal(new[] { Target }, operation.RestoredPaths);
        Assert.Equal("Some Game", operation.Record.GameTitle);
        Assert.Equal(0, journal.Count);
    }

    [Fact]
    public void AGameInterruptedWhileStagingIsOnlyTidied()
    {
        var fs = new FakeFileSystem()
            .AddFile(Target, "old")
            .AddFile(Target + DllSwapExecutor.StagedSuffix, "half a copy")
            .AddFile(Backup, "old");
        var record = Committing(Target);
        record.State = OperationState.Staging;
        record.CreatedBackupPaths.Clear();   // not yet reported at that point
        var journal = new FakeOperationJournal().With(record);

        var report = SwapRecovery.Recover(fs, journal);

        Assert.Equal("old", fs.ReadFile(Target));
        Assert.False(fs.FileExists(Target + DllSwapExecutor.StagedSuffix));

        // A backup the journal did not list is not touched: it may predate this operation.
        Assert.True(fs.FileExists(Backup));
        Assert.Empty(Assert.Single(report.Operations).RestoredPaths);
        Assert.Equal(0, journal.Count);
    }

    [Fact]
    public void APreviousCopyThatWillNotMoveIsReportedAndItsBackupKept()
    {
        var fs = new FakeFileSystem()
            .AddFile(Target, "new")
            .AddFile(Target + DllSwapExecutor.PreviousSuffix, "old")
            .AddFile(Backup, "old")
            .LockFile(Target);
        var journal = new FakeOperationJournal().With(Committing(Target));

        var report = SwapRecovery.Recover(fs, journal);

        var operation = Assert.Single(report.Operations);
        Assert.False(operation.IsComplete);
        Assert.Contains(operation.Warnings, x => x.Contains("Could not restore"));
        Assert.Contains(operation.Warnings, x => x.Contains("Keeping backup"));

        // The only copy of the original stays, and so does the evidence.
        Assert.True(fs.FileExists(Backup));
        Assert.Equal("new", fs.ReadFile(Target));

        // Reported once: the entry does not come back on every launch.
        Assert.Equal(0, journal.Count);
    }

    [Fact]
    public void AnEmptyJournalChangesNothing()
    {
        var fs = new FakeFileSystem().AddFile(Target, "whatever").AddFile(Target + DllSwapExecutor.PreviousSuffix, "stale");

        var report = SwapRecovery.Recover(fs, new FakeOperationJournal());

        Assert.True(report.IsEmpty);
        Assert.Equal("whatever", fs.ReadFile(Target));
        Assert.True(fs.FileExists(Target + DllSwapExecutor.PreviousSuffix));
    }

    [Fact]
    public void AnInterruptedResetGoesBackToTheSwappedDll()
    {
        // A restore renamed the first location back to its original and was killed before the
        // second. Recovery undoes the first rename; the saved originals are untouched.
        var fs = new FakeFileSystem()
            .AddFile(Target, "original")
            .AddFile(Target + DllSwapExecutor.PreviousSuffix, "swapped")
            .AddFile(Backup, "original")
            .AddFile(SecondTarget, "swapped")
            .AddFile(SecondTarget + DllSwapExecutor.StagedSuffix, "original")
            .AddFile(SecondBackup, "original");
        var record = Committing(Target, SecondTarget);
        record.Kind = OperationKind.Reset;
        record.SourcePath = null;
        record.CreatedBackupPaths.Clear();
        var journal = new FakeOperationJournal().With(record);

        SwapRecovery.Recover(fs, journal);

        Assert.Equal("swapped", fs.ReadFile(Target));
        Assert.Equal("swapped", fs.ReadFile(SecondTarget));
        Assert.Equal("original", fs.ReadFile(Backup));
        Assert.Equal("original", fs.ReadFile(SecondBackup));
        Assert.False(fs.FileExists(SecondTarget + DllSwapExecutor.StagedSuffix));
    }

    [Fact]
    public void RecoveryAfterTheExecutorItselfFindsNothingToDo()
    {
        // The ordinary case: the last swap finished. Same journal the executor wrote to.
        var journal = new FakeOperationJournal();
        var fs = new FakeFileSystem().AddFile(@"C:\library\nvngx_dlss.dll", "new").AddFile(Target, "old");
        Assert.True(new DllSwapExecutor(fs, journal).Swap(@"C:\library\nvngx_dlss.dll", new[] { Target }).Success);

        var report = SwapRecovery.Recover(fs, journal);

        Assert.True(report.IsEmpty);
        Assert.Equal("new", fs.ReadFile(Target));
    }
}
