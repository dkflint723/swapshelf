using System;
using System.Collections.Generic;
using System.Linq;

namespace DLSS_Swapper.Swapping;

/// <summary>One journaled operation as recovery found and left it.</summary>
public sealed class RecoveredOperation
{
    public required OperationRecord Record { get; init; }

    /// <summary>Targets whose previous contents were put back.</summary>
    public required IReadOnlyList<string> RestoredPaths { get; init; }

    /// <summary>Anything that could not be put back or cleaned up. Empty means the game is as it was.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    public bool IsComplete => Warnings.Count == 0;
}

public sealed class RecoveryReport
{
    /// <summary>Interrupted operations that were undone, in the order they were handled.</summary>
    public required IReadOnlyList<RecoveredOperation> Operations { get; init; }

    /// <summary>Records whose operation had left nothing behind to undo - something later finished over it. Removed quietly.</summary>
    public int StaleRemoved { get; init; }

    /// <summary>Records left alone because the process that wrote them is still running.</summary>
    public int StillRunning { get; init; }

    public bool IsEmpty => Operations.Count == 0;
}

/// <summary>
/// Puts back whatever a swap or reset the previous session did not finish had already changed.
/// </summary>
/// <remarks>
/// <para>
/// The executor renames each target over in turn. Between two renames a game holds the new dll at
/// one location and the old one at another; if the app is closed or killed there, the in-process
/// rollback never runs, and the next scan reads the half-swapped game as a version it does not
/// recognise. The journal names the targets; this walks them.
/// </para>
/// <para>
/// Always backwards. A target with its previous contents beside it was renamed over, and is
/// renamed back; a staged copy is deleted; a backup this operation created is removed, as the
/// in-process rollback removes it, unless its target could not be restored - then it is the only
/// copy of the original and is kept. Rolling forward would mean committing a state nobody
/// verified. The record is removed even when something could not be put back, so a problem is
/// reported once rather than on every launch; what was left is in the warnings and the log.
/// </para>
/// </remarks>
public static class SwapRecovery
{
    /// <param name="isOwnerRunning">
    /// Whether a record's process is still running. Defaults to <see cref="OperationOwner.IsRunning"/>;
    /// replaceable so a test can stand in for another process.
    /// </param>
    public static RecoveryReport Recover(IFileSystem fileSystem, IOperationJournal journal, Func<OperationRecord, bool>? isOwnerRunning = null)
    {
        isOwnerRunning ??= OperationOwner.IsRunning;

        var records = journal.ReadAll();

        // A record whose process is alive is an operation in progress, not an interrupted one - the
        // app and the command line a Steam plugin starts both write here, and each recovers when it
        // starts. Left alone, and so is every dll it names: an older record must not reach into files
        // a live operation is part way through renaming.
        var running = records.Where(isOwnerRunning).ToList();
        var busy = new HashSet<string>(running.SelectMany(x => x.TargetPaths), StringComparer.OrdinalIgnoreCase);

        var operations = new List<RecoveredOperation>();
        var stale = 0;

        // Newest first. When two interrupted operations touched one dll, the later one's copies sit on
        // top of the earlier one's; undoing the earlier first would restore its state and then leave
        // nothing to undo the later one with.
        foreach (var record in records.Except(running).OrderByDescending(x => x.StartedAtUtc))
        {
            var warnings = new List<string>();
            var restored = new List<string>();
            var notRestored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // The dlls this operation can still be shown to have been in the middle of: a staged or a
            // previous copy is beside them. Everything else it did is gone, finished over by something
            // later, and nothing is undone on its say-so.
            var evidenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var targetPath in record.TargetPaths)
            {
                if (busy.Contains(targetPath))
                {
                    continue;
                }

                var previousPath = targetPath + DllSwapExecutor.PreviousSuffix;
                var stagedPath = targetPath + DllSwapExecutor.StagedSuffix;
                var hasPrevious = fileSystem.FileExists(previousPath);
                var hasStaged = fileSystem.FileExists(stagedPath);

                if (hasPrevious || hasStaged)
                {
                    evidenced.Add(targetPath);
                }

                if (hasPrevious)
                {
                    try
                    {
                        if (fileSystem.FileExists(targetPath))
                        {
                            fileSystem.Replace(previousPath, targetPath, null);
                        }
                        else
                        {
                            fileSystem.Move(previousPath, targetPath, false);
                        }

                        restored.Add(targetPath);
                    }
                    catch (Exception err)
                    {
                        notRestored.Add(targetPath);
                        warnings.Add($"Could not restore '{targetPath}' from '{previousPath}': {err.Message}");
                    }
                }

                if (hasStaged)
                {
                    TryDelete(fileSystem, stagedPath, warnings);
                }
            }

            foreach (var backupPath in record.CreatedBackupPaths)
            {
                var targetPath = backupPath.EndsWith(DllSwapExecutor.BackupSuffix, StringComparison.OrdinalIgnoreCase)
                    ? backupPath.Substring(0, backupPath.Length - DllSwapExecutor.BackupSuffix.Length)
                    : backupPath;

                // A backup this operation made goes only while the operation is still in evidence at
                // its dll. Once something later has finished over that dll, the backup may be the
                // original that later operation relies on - it found it there and made none of its own.
                if (evidenced.Contains(targetPath) == false)
                {
                    continue;
                }

                if (notRestored.Contains(targetPath))
                {
                    warnings.Add($"Keeping backup '{backupPath}', '{targetPath}' could not be restored.");
                    continue;
                }

                TryDelete(fileSystem, backupPath, warnings);
            }

            try
            {
                journal.Remove(record.Id);
            }
            catch (Exception err)
            {
                warnings.Add($"Could not remove the journal entry for this operation: {err.Message}");
            }

            if (evidenced.Count == 0 && warnings.Count == 0)
            {
                stale++;
                continue;
            }

            operations.Add(new RecoveredOperation()
            {
                Record = record,
                RestoredPaths = restored,
                Warnings = warnings,
            });
        }

        return new RecoveryReport()
        {
            Operations = operations,
            StaleRemoved = stale,
            StillRunning = running.Count,
        };
    }

    static void TryDelete(IFileSystem fileSystem, string path, List<string> warnings)
    {
        try
        {
            if (fileSystem.FileExists(path))
            {
                fileSystem.Delete(path);
            }
        }
        catch (Exception err)
        {
            warnings.Add($"Could not remove '{path}': {err.Message}");
        }
    }
}
