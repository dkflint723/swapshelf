using System;
using System.IO;
using System.Linq;
using DLSS_Swapper.Swapping;
using Xunit;

namespace DLSS_Swapper.Tests;

/// <summary>
/// The executor writes down what it is about to do, and crosses it off when done.
/// </summary>
/// <remarks>
/// The journal exists for the process that never gets to run its rollback. So the record has to be
/// on disk before the first rename - not after - and gone once every rename is done, whether the
/// operation succeeded or rolled itself back. And it is a safety net over the swap, never a gate:
/// a journal that cannot be written is a warning, not a refusal.
/// </remarks>
public class OperationJournalTests
{
    const string Source = @"C:\library\nvngx_dlss.dll";
    const string Target = @"C:\game\nvngx_dlss.dll";
    const string SecondTarget = @"C:\game\bin\nvngx_dlss.dll";

    static FakeFileSystem Game()
    {
        return new FakeFileSystem()
            .AddFile(Source, "new")
            .AddFile(Target, "old")
            .AddFile(SecondTarget, "old");
    }

    [Fact]
    public void ACompletedSwapLeavesNoEntryAndWasRecordedInTwoSteps()
    {
        var journal = new FakeOperationJournal();
        var fs = Game();

        var result = new DllSwapExecutor(fs, journal).Swap(Source, new[] { Target, SecondTarget }, new OperationLabel("game-1", "Some Game"));

        Assert.True(result.Success);
        Assert.Equal(0, journal.Count);
        Assert.Equal(2, journal.Writes.Count);
        Assert.Equal(OperationState.Staging, journal.Writes[0].State);
        Assert.Equal(OperationState.Committing, journal.Writes[1].State);
        Assert.Equal(journal.Writes[0].Id, journal.Writes[1].Id);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void TheEntrySaysCommittingBeforeTheFirstRename()
    {
        var fs = Game();
        var journal = new FakeOperationJournal();
        journal.OnWrite = record =>
        {
            if (record.State == OperationState.Committing)
            {
                // Staged copies are beside both targets, and neither target has been touched yet.
                Assert.Equal("old", fs.ReadFile(Target));
                Assert.Equal("old", fs.ReadFile(SecondTarget));
                Assert.True(fs.FileExists(Target + DllSwapExecutor.StagedSuffix));
                Assert.True(fs.FileExists(SecondTarget + DllSwapExecutor.StagedSuffix));

                // And it names what recovery would need.
                Assert.Equal(OperationKind.Swap, record.Kind);
                Assert.Equal("game-1", record.GameId);
                Assert.Equal("Some Game", record.GameTitle);
                Assert.Equal(Source, record.SourcePath);
                Assert.Equal(new[] { Target, SecondTarget }, record.TargetPaths);
                Assert.Equal(new[] { DllSwapExecutor.GetBackupPath(Target), DllSwapExecutor.GetBackupPath(SecondTarget) }, record.CreatedBackupPaths);
            }
        };

        var result = new DllSwapExecutor(fs, journal).Swap(Source, new[] { Target, SecondTarget }, new OperationLabel("game-1", "Some Game"));

        Assert.True(result.Success);
        Assert.Contains(journal.Writes, x => x.State == OperationState.Committing);
    }

    [Fact]
    public void AnExistingBackupIsNotListedAsCreated()
    {
        var fs = Game().AddFile(DllSwapExecutor.GetBackupPath(Target), "older original");
        var journal = new FakeOperationJournal();
        OperationRecord? committing = null;
        journal.OnWrite = record => { if (record.State == OperationState.Committing) committing = record; };

        new DllSwapExecutor(fs, journal).Swap(Source, new[] { Target, SecondTarget });

        Assert.NotNull(committing);
        Assert.Equal(new[] { DllSwapExecutor.GetBackupPath(SecondTarget) }, committing.CreatedBackupPaths);
    }

    [Fact]
    public void AFailedSwapLeavesNoEntry()
    {
        var journal = new FakeOperationJournal();
        var fs = Game().LockFile(SecondTarget);

        var result = new DllSwapExecutor(fs, journal).Swap(Source, new[] { Target, SecondTarget });

        Assert.False(result.Success);
        Assert.Equal(0, journal.Count);
    }

    [Fact]
    public void ASwapRefusedBeforeStagingWritesNothing()
    {
        var journal = new FakeOperationJournal();
        var fs = Game();

        var result = new DllSwapExecutor(fs, journal).Swap(@"C:\library\missing.dll", new[] { Target });

        Assert.Equal(SwapFailure.SourceMissing, result.Failure);
        Assert.Empty(journal.Writes);
    }

    [Fact]
    public void AJournalThatCannotBeWrittenIsAWarningNotARefusal()
    {
        var journal = new FakeOperationJournal() { FailWritesWith = new IOException("disk full") };
        var fs = Game();

        var result = new DllSwapExecutor(fs, journal).Swap(Source, new[] { Target });

        Assert.True(result.Success);
        Assert.Equal("new", fs.ReadFile(Target));
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("journal", warning);
        Assert.Contains("disk full", warning);
    }

    [Fact]
    public void ARestoreIsJournaledAsAReset()
    {
        var fs = new FakeFileSystem()
            .AddFile(Target, "swapped")
            .AddFile(DllSwapExecutor.GetBackupPath(Target), "original");
        var journal = new FakeOperationJournal();
        OperationRecord? seen = null;
        journal.OnWrite = record => seen = record;

        var result = new DllSwapExecutor(fs, journal).Reset(new[] { new ResetTarget(Target, null) }, new OperationLabel("game-2", "Other Game"));

        Assert.True(result.Success);
        Assert.Equal("original", fs.ReadFile(Target));
        Assert.NotNull(seen);
        Assert.Equal(OperationKind.Reset, seen.Kind);
        Assert.Equal("Other Game", seen.GameTitle);
        Assert.Null(seen.SourcePath);
        Assert.Empty(seen.CreatedBackupPaths);
        Assert.Equal(0, journal.Count);
    }

    [Fact]
    public void TheExecutorWithoutAJournalStillWorks()
    {
        var fs = Game();

        var result = new DllSwapExecutor(fs).Swap(Source, new[] { Target });

        Assert.True(result.Success);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void TheFileJournalRoundTrips()
    {
        var folder = Path.Combine(Path.GetTempPath(), "swapshelf-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new FileOperationJournal(folder);

            // Missing folder reads as empty rather than failing.
            Assert.Empty(journal.ReadAll());

            var first = new OperationRecord() { Id = "a1", Kind = OperationKind.Swap, State = OperationState.Staging, GameTitle = "First", TargetPaths = { Target }, StartedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
            var second = new OperationRecord() { Id = "b2", Kind = OperationKind.Reset, State = OperationState.Committing, TargetPaths = { Target, SecondTarget }, CreatedBackupPaths = { Target + ".dlsss" }, StartedAtUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) };
            journal.Write(second);
            journal.Write(first);

            // Something that is not a record, left where it is.
            File.WriteAllText(Path.Combine(folder, "garbage.json"), "{ not json");

            var all = journal.ReadAll();
            Assert.Equal(new[] { "a1", "b2" }, all.Select(x => x.Id).ToArray());   // oldest first
            Assert.Equal(OperationKind.Reset, all[1].Kind);
            Assert.Equal(OperationState.Committing, all[1].State);
            Assert.Equal(new[] { Target, SecondTarget }, all[1].TargetPaths);
            Assert.Equal(new[] { Target + ".dlsss" }, all[1].CreatedBackupPaths);
            Assert.Equal("First", all[0].GameTitle);
            Assert.True(File.Exists(Path.Combine(folder, "garbage.json")));

            // A rewrite replaces rather than duplicates.
            first.State = OperationState.Committing;
            journal.Write(first);
            Assert.Equal(2, journal.ReadAll().Count);
            Assert.Equal(OperationState.Committing, journal.ReadAll().Single(x => x.Id == "a1").State);

            journal.Remove("a1");
            journal.Remove("never-existed");
            Assert.Equal(new[] { "b2" }, journal.ReadAll().Select(x => x.Id).ToArray());

            // No temp files left behind.
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }
}
