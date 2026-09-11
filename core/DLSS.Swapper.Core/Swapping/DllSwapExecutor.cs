using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DLSS_Swapper.Pe;

namespace DLSS_Swapper.Swapping;

/// <summary>
/// Replaces a set of dlls inside a game install as a single all-or-nothing operation.
/// </summary>
/// <remarks>
/// <para>
/// A game can ship the same dll in more than one place, so a swap is rarely a single file copy.
/// Doing it as a plain loop of copies means a failure part way through leaves some locations
/// swapped and some not, with no record of which. This executor instead runs three phases across
/// every target before anything user visible changes:
/// </para>
/// <list type="number">
/// <item><description>Back up any target that does not already have a backup, checked per file.</description></item>
/// <item><description>Stage the incoming dll next to each target, so a partial write never lands on a real path.</description></item>
/// <item><description>Commit each target with an atomic replace, keeping the previous contents aside.</description></item>
/// </list>
/// <para>
/// If any phase throws, everything already done is undone and the caller gets a failure with the
/// targets back at their original contents. Callers must not update their own bookkeeping until
/// this reports success.
/// </para>
/// </remarks>
public sealed class DllSwapExecutor
{
    /// <summary>Suffix of the backup holding a game's original dll. Long standing, do not change.</summary>
    public const string BackupSuffix = ".dlsss";

    /// <summary>Incoming dll, written here first so a partial copy never touches the real path.</summary>
    internal const string StagedSuffix = ".dlss-swapper-staged";

    /// <summary>Previous contents of a target, kept until the whole operation succeeds.</summary>
    internal const string PreviousSuffix = ".dlss-swapper-previous";

    readonly IFileSystem _fileSystem;
    readonly IOperationJournal _journal;

    /// <param name="journal">
    /// Where each operation is recorded while it runs, so one the process does not live to finish
    /// can be put back next launch by <see cref="SwapRecovery"/>. The journal is a safety net over
    /// the operation, never a gate on it: one that cannot be written is reported as a warning and
    /// the swap goes ahead.
    /// </param>
    public DllSwapExecutor(IFileSystem fileSystem, IOperationJournal journal)
    {
        _fileSystem = fileSystem;
        _journal = journal;
    }

    public DllSwapExecutor(IFileSystem fileSystem) : this(fileSystem, NullOperationJournal.Instance)
    {
    }

    public DllSwapExecutor() : this(PhysicalFileSystem.Instance)
    {
    }

    /// <summary>
    /// Points every target at the dll in <paramref name="sourcePath"/>, backing up any target that
    /// does not already have a backup.
    /// </summary>
    public SwapResult Swap(string sourcePath, IReadOnlyList<string> targetPaths, OperationLabel? label = null)
    {
        var targets = WithoutDuplicates(targetPaths);

        if (targets.Count == 0)
        {
            return SwapResult.Fail(SwapFailure.NoTargets);
        }

        if (_fileSystem.FileExists(sourcePath) == false)
        {
            return SwapResult.Fail(SwapFailure.SourceMissing, sourcePath);
        }

        // The dll going in has to be built for the same processor as the one it replaces. Windows
        // refuses to load the wrong one, and the game turns that into a crash on startup - long
        // after the swap reported success. Two bytes of header, read before anything is staged; a
        // header that cannot be read says nothing and is not held against the file.
        var sourceMachine = ReadMachine(sourcePath);
        if (sourceMachine != PeMachine.Unknown)
        {
            foreach (var targetPath in targets)
            {
                if (_fileSystem.FileExists(targetPath) == false)
                {
                    continue;
                }

                var targetMachine = ReadMachine(targetPath);
                if (targetMachine != PeMachine.Unknown && targetMachine != sourceMachine)
                {
                    return SwapResult.Fail(SwapFailure.ArchitectureMismatch, targetPath, warnings: new[]
                    {
                        $"{sourcePath} is {PeHeader.Describe(sourceMachine)}; {targetPath} is {PeHeader.Describe(targetMachine)}. Not swapped.",
                    });
                }
            }
        }

        var transaction = new Transaction(_fileSystem);
        var journal = JournalEntry.Open(_journal, OperationKind.Swap, label, sourcePath, targets);

        try
        {
            // Each target is considered on its own. A backup existing for one location says nothing
            // about whether the others have one.
            foreach (var targetPath in targets)
            {
                transaction.EnsureBackup(targetPath);
            }

            foreach (var targetPath in targets)
            {
                transaction.Stage(targetPath, sourcePath);
            }

            // From here the game folder changes. The journal says so and lists the backups this
            // swap made, so a session that ends between two renames can be put back next launch.
            journal.Committing(transaction.CreatedBackupPaths);

            foreach (var targetPath in targets)
            {
                transaction.Commit(targetPath);
            }

            // Every rename is done: the game has its new dll at every location. Closed before the
            // cleanup, so a crash while deleting temp files leaves orphans rather than a rollback.
            journal.Done();
        }
        catch (Exception err)
        {
            return journal.Finish(transaction.RollbackAndFail(err));
        }

        return journal.Finish(transaction.Complete());
    }

    /// <summary>
    /// Puts saved originals back by path alone, with no hash to check them against.
    /// </summary>
    /// <remarks>
    /// Kept for callers that only know the paths - the command line among them. Anything that has
    /// the recorded hash should use the <see cref="ResetTarget"/> overload, which refuses a backup
    /// that is no longer what was saved.
    /// </remarks>
    public SwapResult Reset(IReadOnlyList<string> targetPaths)
    {
        return Reset(targetPaths.Select(x => new ResetTarget(x, null)).ToList());
    }

    /// <summary>
    /// Puts saved originals back, refusing any that do not hash to what was recorded when saved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every backup is checked - present, and matching its hash where one is known - before
    /// anything at all is staged. A refusal therefore costs nothing: both the swapped dll and the
    /// suspect backup stay exactly where they were, for someone to look at.
    /// </para>
    /// <para>
    /// A target with no recorded hash is restored without the check. Backups saved by an older
    /// build carry none, and they are still the only original there is.
    /// </para>
    /// </remarks>
    public SwapResult Reset(IReadOnlyList<ResetTarget> targets, OperationLabel? label = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distinct = new List<ResetTarget>();
        foreach (var target in targets)
        {
            if (seen.Add(target.TargetPath))
            {
                distinct.Add(target);
            }
        }

        if (distinct.Count == 0)
        {
            return SwapResult.Fail(SwapFailure.NoTargets);
        }

        // Check every backup before touching anything, so a game with one missing backup does not
        // end up half restored.
        foreach (var target in distinct)
        {
            var backupPath = GetBackupPath(target.TargetPath);
            if (_fileSystem.FileExists(backupPath) == false)
            {
                return SwapResult.Fail(SwapFailure.BackupMissing, backupPath);
            }
        }

        // And that each one is still the file that was saved. This is the check that used to be
        // missing: existence was taken as identity, and a backup corrupted or replaced since it was
        // made went back into the game as though it were the original.
        foreach (var target in distinct)
        {
            if (string.IsNullOrWhiteSpace(target.ExpectedBackupHash))
            {
                continue;
            }

            var backupPath = GetBackupPath(target.TargetPath);
            using (var stream = _fileSystem.OpenRead(backupPath))
            {
                if (FileHashes.Md5Matches(stream, target.ExpectedBackupHash) == false)
                {
                    return SwapResult.Fail(SwapFailure.BackupTampered, backupPath);
                }
            }
        }

        // And that the dll being replaced is still the one this app left there. A restore is the
        // button people press when something has already gone sideways, and "put the original back"
        // is not the same request as "erase whatever somebody did to this file since". The caller
        // decides what to ask; this only refuses to guess.
        foreach (var target in distinct)
        {
            if (string.IsNullOrWhiteSpace(target.ExpectedCurrentHash) || _fileSystem.FileExists(target.TargetPath) == false)
            {
                continue;
            }

            using (var stream = _fileSystem.OpenRead(target.TargetPath))
            {
                if (FileHashes.Md5Matches(stream, target.ExpectedCurrentHash) == false)
                {
                    return SwapResult.Fail(SwapFailure.TargetChanged, target.TargetPath);
                }
            }
        }

        var transaction = new Transaction(_fileSystem);
        var targetPaths = new List<string>(distinct.Count);
        foreach (var target in distinct)
        {
            targetPaths.Add(target.TargetPath);
        }
        var journal = JournalEntry.Open(_journal, OperationKind.Reset, label, null, targetPaths);

        try
        {
            foreach (var target in distinct)
            {
                transaction.Stage(target.TargetPath, GetBackupPath(target.TargetPath));
            }

            journal.Committing(transaction.CreatedBackupPaths);

            foreach (var target in distinct)
            {
                transaction.Commit(target.TargetPath);
            }

            // The originals are back at every location. Closed before the backups are discarded: a
            // crash during that cleanup must not be answered by rolling the restore back.
            journal.Done();
        }
        catch (Exception err)
        {
            return journal.Finish(transaction.RollbackAndFail(err));
        }

        // A backup only exists to get back to the original dll. Once we are there it has done its job.
        foreach (var target in distinct)
        {
            transaction.Discard(GetBackupPath(target.TargetPath));
        }

        return journal.Finish(transaction.Complete());
    }

    /// <summary>
    /// One location named twice is one location.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The phases below are three separate loops over this list, so a repeated path was worked three
    /// times. The second commit deleted the previous contents the first had just set aside and then
    /// found its staged file already consumed by the first replace - which failed the whole swap and
    /// reported an incomplete rollback, for something the caller had merely said twice. The original
    /// survived, in the backup, but a swap that could have worked did not.
    /// </para>
    /// <para>
    /// Ordinal ignore case, matching how the rollback already compares these paths. That catches a
    /// path repeated with different casing and an exact repeat; it does not canonicalise, so two
    /// spellings of one file - through a junction, or a relative segment - are still two targets.
    /// Nothing in the app produces either today: the paths come from one directory walk.
    /// </para>
    /// </remarks>
    static IReadOnlyList<string> WithoutDuplicates(IReadOnlyList<string> targetPaths)
    {
        if (targetPaths.Count < 2)
        {
            return targetPaths;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distinct = new List<string>(targetPaths.Count);

        foreach (var targetPath in targetPaths)
        {
            if (seen.Add(targetPath))
            {
                distinct.Add(targetPath);
            }
        }

        // The list itself when there was nothing to remove, so the ordinary case allocates nothing
        // and callers comparing against what they passed in still see it.
        return distinct.Count == targetPaths.Count ? targetPaths : distinct;
    }

    PeMachine ReadMachine(string path)
    {
        try
        {
            using (var stream = _fileSystem.OpenRead(path))
            {
                return PeHeader.ReadMachine(stream);
            }
        }
        catch (Exception)
        {
            // A file that will not open for reading is reported by the step that needs to write it.
            return PeMachine.Unknown;
        }
    }

    public static string GetBackupPath(string targetPath) => targetPath + BackupSuffix;

    internal static SwapFailure Classify(Exception err)
    {
        return err switch
        {
            UnauthorizedAccessException => SwapFailure.AccessDenied,
            IOException ioErr when IsSharingViolation(ioErr) => SwapFailure.FileInUse,
            _ => SwapFailure.Unknown,
        };
    }

    static bool IsSharingViolation(IOException err)
    {
        const int ERROR_SHARING_VIOLATION = unchecked((int)0x80070020);
        const int ERROR_LOCK_VIOLATION = unchecked((int)0x80070021);

        return err.HResult == ERROR_SHARING_VIOLATION || err.HResult == ERROR_LOCK_VIOLATION;
    }

    /// <summary>
    /// The journal's view of one operation: opened before the folder is touched, marked when the
    /// renames begin, removed when they are all done. Never throws into the operation.
    /// </summary>
    sealed class JournalEntry
    {
        readonly IOperationJournal _journal;
        readonly OperationRecord _record;
        readonly List<string> _warnings = new List<string>();
        bool _enabled;
        bool _done;

        JournalEntry(IOperationJournal journal, OperationRecord record)
        {
            _journal = journal;
            _record = record;
        }

        public static JournalEntry Open(IOperationJournal journal, OperationKind kind, OperationLabel? label, string? sourcePath, IReadOnlyList<string> targetPaths)
        {
            var entry = new JournalEntry(journal, new OperationRecord()
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = kind,
                State = OperationState.Staging,
                GameId = label?.GameId,
                GameTitle = label?.GameTitle,
                SourcePath = sourcePath,
                TargetPaths = targetPaths.ToList(),
                StartedAtUtc = DateTime.UtcNow,
                ProcessId = OperationOwner.Current.ProcessId,
                ProcessStartedAtUtc = OperationOwner.Current.StartedAtUtc,
            });

            entry._enabled = entry.TryWrite("Could not open the operation journal");
            return entry;
        }

        public void Committing(IReadOnlyList<string> createdBackupPaths)
        {
            if (_enabled == false)
            {
                return;
            }

            _record.State = OperationState.Committing;
            _record.CreatedBackupPaths = createdBackupPaths.ToList();
            TryWrite("Could not update the operation journal");
        }

        public void Done()
        {
            if (_enabled == false || _done)
            {
                return;
            }

            _done = true;
            try
            {
                _journal.Remove(_record.Id);
            }
            catch (Exception err)
            {
                // Left behind, recovery next launch finds nothing to put back and removes it.
                _warnings.Add($"Could not close the operation journal entry: {err.Message}");
            }
        }

        public SwapResult Finish(SwapResult result)
        {
            Done();
            return result.WithWarnings(_warnings);
        }

        bool TryWrite(string what)
        {
            try
            {
                _journal.Write(_record);
                return true;
            }
            catch (Exception err)
            {
                _warnings.Add($"{what}; the operation ran without one: {err.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Tracks what has been done so far so it can all be undone.
    /// </summary>
    sealed class Transaction
    {
        readonly IFileSystem _fileSystem;
        readonly List<CreatedBackup> _createdBackups = new List<CreatedBackup>();
        readonly List<string> _stagedPaths = new List<string>();
        readonly List<(string TargetPath, string PreviousPath)> _previousContents = new List<(string, string)>();
        readonly List<string> _replacedPaths = new List<string>();

        /// <summary>Targets that did not exist until this swap made them. Undone by deleting.</summary>
        readonly List<string> _createdTargets = new List<string>();
        readonly List<string> _warnings = new List<string>();

        string? _currentPath;

        public Transaction(IFileSystem fileSystem)
        {
            _fileSystem = fileSystem;
        }

        /// <summary>Backups this operation has made so far, for the journal.</summary>
        public IReadOnlyList<string> CreatedBackupPaths => _createdBackups.Select(x => x.BackupPath).ToList();

        public void EnsureBackup(string targetPath)
        {
            _currentPath = targetPath;

            var backupPath = GetBackupPath(targetPath);
            if (_fileSystem.FileExists(backupPath))
            {
                return;
            }

            _fileSystem.Copy(targetPath, backupPath, false);
            _createdBackups.Add(new CreatedBackup()
            {
                TargetPath = targetPath,
                BackupPath = backupPath,
            });
        }

        public void Stage(string targetPath, string sourcePath)
        {
            _currentPath = targetPath;

            // Staging beside the target keeps the commit on one volume, which is what lets it be a rename.
            var stagedPath = targetPath + StagedSuffix;
            if (_fileSystem.FileExists(stagedPath))
            {
                _fileSystem.Delete(stagedPath);
            }

            _fileSystem.Copy(sourcePath, stagedPath, true);
            _stagedPaths.Add(stagedPath);
        }

        public void Commit(string targetPath)
        {
            _currentPath = targetPath;

            var stagedPath = targetPath + StagedSuffix;
            var previousPath = targetPath + PreviousSuffix;

            if (_fileSystem.FileExists(previousPath))
            {
                _fileSystem.Delete(previousPath);
            }

            if (_fileSystem.FileExists(targetPath))
            {
                _fileSystem.Replace(stagedPath, targetPath, previousPath);
                _previousContents.Add((targetPath, previousPath));
            }
            else
            {
                // Nothing to preserve - but the path is still something this swap brought into
                // existence, and undoing that means removing it rather than restoring it. Recorded
                // separately from _previousContents for that reason. Leaving it behind after
                // reporting failure put a dll in a game folder the app had just said it did not
                // write, which the next scan reads as a version it does not recognise.
                _fileSystem.Move(stagedPath, targetPath, false);
                _createdTargets.Add(targetPath);
            }

            _stagedPaths.Remove(stagedPath);
            _replacedPaths.Add(targetPath);
        }

        public void Discard(string path)
        {
            TryDelete(path);
        }

        public SwapResult RollbackAndFail(Exception err)
        {
            var failure = Classify(err);
            var failedPath = _currentPath;
            var rollbackIncomplete = false;
            var restoredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Undo commits newest first.
            for (var i = _previousContents.Count - 1; i >= 0; i--)
            {
                var (targetPath, previousPath) = _previousContents[i];

                try
                {
                    if (_fileSystem.FileExists(previousPath) == false)
                    {
                        rollbackIncomplete = true;
                        _warnings.Add($"Could not restore '{targetPath}', its previous contents at '{previousPath}' are gone.");
                        continue;
                    }

                    if (_fileSystem.FileExists(targetPath))
                    {
                        _fileSystem.Replace(previousPath, targetPath, null);
                    }
                    else
                    {
                        _fileSystem.Move(previousPath, targetPath, false);
                    }

                    restoredPaths.Add(targetPath);
                }
                catch (Exception rollbackErr)
                {
                    rollbackIncomplete = true;
                    _warnings.Add($"Could not restore '{targetPath}' from '{previousPath}': {rollbackErr.Message}");
                }
            }

            // Targets this swap created go back to not existing. Reported when that fails, because
            // a dll left in a game folder after a failure is exactly the divergence between disk and
            // records that the scan later resolves by deleting a backup.
            foreach (var createdTarget in _createdTargets)
            {
                try
                {
                    if (_fileSystem.FileExists(createdTarget))
                    {
                        _fileSystem.Delete(createdTarget);
                    }
                }
                catch (Exception createdTargetErr)
                {
                    rollbackIncomplete = true;
                    _warnings.Add($"Could not remove '{createdTarget}', which this swap created: {createdTargetErr.Message}");
                }
            }

            foreach (var stagedPath in _stagedPaths)
            {
                TryDelete(stagedPath);
            }

            foreach (var createdBackup in _createdBackups)
            {
                var wasCommitted = _replacedPaths.Any(x => string.Equals(x, createdBackup.TargetPath, StringComparison.OrdinalIgnoreCase));

                // If we replaced this target and could not put it back, the backup we made is now the
                // only copy of the original dll. Keeping a stray backup is much cheaper than losing it.
                if (wasCommitted && restoredPaths.Contains(createdBackup.TargetPath) == false)
                {
                    _warnings.Add($"Keeping backup '{createdBackup.BackupPath}', '{createdBackup.TargetPath}' could not be restored.");
                    continue;
                }

                TryDelete(createdBackup.BackupPath);
            }

            return SwapResult.Fail(failure, failedPath, err, rollbackIncomplete, _warnings);
        }

        public SwapResult Complete()
        {
            foreach (var (_, previousPath) in _previousContents)
            {
                TryDelete(previousPath);
            }

            foreach (var stagedPath in _stagedPaths)
            {
                TryDelete(stagedPath);
            }

            return SwapResult.Ok(_createdBackups, _replacedPaths, _warnings);
        }

        void TryDelete(string path)
        {
            try
            {
                if (_fileSystem.FileExists(path))
                {
                    _fileSystem.Delete(path);
                }
            }
            catch (Exception err)
            {
                // Leftover temp files are untidy but harmless, they do not match the *.dll scan.
                _warnings.Add($"Could not remove '{path}': {err.Message}");
            }
        }
    }
}
