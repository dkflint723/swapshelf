using System;
using System.Diagnostics;
using System.Linq;
using DLSS_Swapper.Swapping;
using Xunit;

namespace DLSS_Swapper.Tests;

/// <summary>
/// Recovery leaves alone what is not its to undo: an operation still running, and a record that
/// something later has already finished over.
/// </summary>
/// <remarks>
/// <para>
/// Two processes write the journal - the app, and the command line a Steam plugin starts - and each
/// recovers when it starts. Recovery first treated every record as an interrupted operation. So the
/// app starting while the plugin was part way through a swap would pull the plugin's files out from
/// under it; and a record left by an interrupted swap, recovered after a later swap had finished over
/// the same dll, would delete the backup that later swap had found there and relied on - the one copy
/// of what the game shipped with.
/// </para>
/// <para>
/// Built from the disk as each case leaves it, the same way as the other recovery tests.
/// </para>
/// </remarks>
public class SwapRecoveryOwnershipTests
{
    const string Target = @"C:\game\nvngx_dlss.dll";
    static readonly string Backup = DllSwapExecutor.GetBackupPath(Target);
    static readonly string Previous = Target + DllSwapExecutor.PreviousSuffix;

    static OperationRecord Swap(string id, DateTime startedAtUtc, bool createdBackup)
    {
        var record = new OperationRecord()
        {
            Id = id,
            Kind = OperationKind.Swap,
            State = OperationState.Committing,
            GameId = "game-1",
            GameTitle = "Some Game",
            SourcePath = @"C:\library\nvngx_dlss.dll",
            StartedAtUtc = startedAtUtc,
        };
        record.TargetPaths.Add(Target);
        if (createdBackup)
        {
            record.CreatedBackupPaths.Add(Backup);
        }
        return record;
    }

    [Fact]
    public void AnOperationWhoseProcessIsStillRunningIsLeftAlone()
    {
        // Mid-commit in another process: the target already holds the new dll, the old one is beside it.
        var fs = new FakeFileSystem()
            .AddFile(Target, "new")
            .AddFile(Previous, "original")
            .AddFile(Backup, "original");
        var journal = new FakeOperationJournal().With(Swap("live", DateTime.UtcNow, createdBackup: true));

        var report = SwapRecovery.Recover(fs, journal, isOwnerRunning: _ => true);

        Assert.True(report.IsEmpty);
        Assert.Equal(1, report.StillRunning);
        Assert.Equal(1, journal.Count);
        Assert.Equal("new", fs.ReadFile(Target));
        Assert.True(fs.FileExists(Previous));
        Assert.True(fs.FileExists(Backup));
    }

    [Fact]
    public void AnInterruptedRecordDoesNotReachIntoADllALiveOperationIsWorkingOn()
    {
        var fs = new FakeFileSystem()
            .AddFile(Target, "new")
            .AddFile(Previous, "the live operation's copy")
            .AddFile(Backup, "original");
        var interrupted = Swap("old", DateTime.UtcNow.AddMinutes(-10), createdBackup: true);
        var live = Swap("live", DateTime.UtcNow, createdBackup: false);
        var journal = new FakeOperationJournal().With(interrupted).With(live);

        var report = SwapRecovery.Recover(fs, journal, isOwnerRunning: record => record.Id == "live");

        // Nothing the live operation needs was touched, and the interrupted record is gone.
        Assert.Equal("new", fs.ReadFile(Target));
        Assert.Equal("the live operation's copy", fs.ReadFile(Previous));
        Assert.True(fs.FileExists(Backup));
        Assert.Equal(1, report.StillRunning);
        Assert.Equal("live", Assert.Single(journal.ReadAll()).Id);
    }

    [Fact]
    public void ARecordSomethingLaterFinishedOverDoesNotDeleteTheOriginal()
    {
        // An interrupted swap made the backup; a later swap - from the command line, before the app
        // ever recovered - found it, relied on it, and finished cleanly. No staged or previous copy
        // is left. The backup is now the only copy of what the game shipped with.
        var fs = new FakeFileSystem()
            .AddFile(Target, "the later swap's dll")
            .AddFile(Backup, "original");
        var journal = new FakeOperationJournal().With(Swap("stale", DateTime.UtcNow.AddHours(-1), createdBackup: true));

        var report = SwapRecovery.Recover(fs, journal, isOwnerRunning: _ => false);

        Assert.Equal("original", fs.ReadFile(Backup));
        Assert.Equal("the later swap's dll", fs.ReadFile(Target));

        // Removed quietly: there was nothing to put back, so there is nothing to tell anyone.
        Assert.True(report.IsEmpty);
        Assert.Equal(1, report.StaleRemoved);
        Assert.Equal(0, journal.Count);
    }

    [Fact]
    public void TwoInterruptedOperationsOnOneDllAreUndoneNewestFirst()
    {
        // The first swap made the backup and committed; the second committed over it and was cut off
        // too. Its previous copy is the first swap's dll - the first's own previous copy was replaced.
        var fs = new FakeFileSystem()
            .AddFile(Target, "second")
            .AddFile(Previous, "first")
            .AddFile(Backup, "original");
        var first = Swap("first", DateTime.UtcNow.AddMinutes(-5), createdBackup: true);
        var second = Swap("second", DateTime.UtcNow, createdBackup: false);
        var journal = new FakeOperationJournal().With(first).With(second);

        var report = SwapRecovery.Recover(fs, journal, isOwnerRunning: _ => false);

        // The second is undone; the first then has nothing left in evidence and keeps its hands off
        // the backup. The game holds the first swap's dll, and the original is still saved.
        Assert.Equal("first", fs.ReadFile(Target));
        Assert.Equal("original", fs.ReadFile(Backup));
        Assert.False(fs.FileExists(Previous));
        Assert.Equal("second", Assert.Single(report.Operations).Record.Id);
        Assert.Equal(1, report.StaleRemoved);
        Assert.Equal(0, journal.Count);
    }

    [Fact]
    public void AnInterruptedOperationStillInEvidenceIsUndoneAsBefore()
    {
        // The ordinary case the journal exists for, now with an owner that has gone.
        var fs = new FakeFileSystem()
            .AddFile(Target, "new")
            .AddFile(Previous, "original")
            .AddFile(Backup, "original");
        var journal = new FakeOperationJournal().With(Swap("cut-off", DateTime.UtcNow, createdBackup: true));

        var report = SwapRecovery.Recover(fs, journal, isOwnerRunning: _ => false);

        Assert.Equal("original", fs.ReadFile(Target));
        Assert.False(fs.FileExists(Previous));
        Assert.False(fs.FileExists(Backup));
        Assert.True(Assert.Single(report.Operations).IsComplete);
    }

    [Fact]
    public void ThisProcessIsRunningAndAGoneOneIsNot()
    {
        var (id, started) = OperationOwner.Current;
        Assert.Equal(Environment.ProcessId, id);

        Assert.True(OperationOwner.IsRunning(new OperationRecord() { ProcessId = id, ProcessStartedAtUtc = started }));

        // Written before owners were kept.
        Assert.False(OperationOwner.IsRunning(new OperationRecord() { ProcessId = 0 }));

        // An id no process has.
        Assert.False(OperationOwner.IsRunning(new OperationRecord() { ProcessId = int.MaxValue, ProcessStartedAtUtc = DateTime.UtcNow }));
    }

    [Fact]
    public void AnIdThatNowBelongsToADifferentProcessIsNotTheOwner()
    {
        // This process's id, but a start time an hour off: Windows has reused the id.
        var (id, started) = OperationOwner.Current;
        Assert.NotNull(started);

        Assert.False(OperationOwner.IsRunning(new OperationRecord() { ProcessId = id, ProcessStartedAtUtc = started!.Value.AddHours(-1) }));
    }

    [Fact]
    public void TheExecutorNamesItselfInEveryRecord()
    {
        var journal = new FakeOperationJournal();
        OperationRecord? seen = null;
        journal.OnWrite = record => seen = record;
        var fs = new FakeFileSystem().AddFile(@"C:\library\nvngx_dlss.dll", "new").AddFile(Target, "old");

        new DllSwapExecutor(fs, journal).Swap(@"C:\library\nvngx_dlss.dll", new[] { Target });

        Assert.NotNull(seen);
        Assert.Equal(Environment.ProcessId, seen.ProcessId);
        Assert.True(OperationOwner.IsRunning(seen));
    }
}
