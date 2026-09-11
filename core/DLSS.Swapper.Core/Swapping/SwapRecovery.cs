using System;
using System.Collections.Generic;

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
    public required IReadOnlyList<RecoveredOperation> Operations { get; init; }

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
    public static RecoveryReport Recover(IFileSystem fileSystem, IOperationJournal journal)
    {
        var operations = new List<RecoveredOperation>();

        foreach (var record in journal.ReadAll())
        {
            var warnings = new List<string>();
            var restored = new List<string>();
            var notRestored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var targetPath in record.TargetPaths)
            {
                var previousPath = targetPath + DllSwapExecutor.PreviousSuffix;
                var stagedPath = targetPath + DllSwapExecutor.StagedSuffix;

                if (fileSystem.FileExists(previousPath))
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

                TryDelete(fileSystem, stagedPath, warnings);
            }

            foreach (var backupPath in record.CreatedBackupPaths)
            {
                var targetPath = backupPath.EndsWith(DllSwapExecutor.BackupSuffix, StringComparison.OrdinalIgnoreCase)
                    ? backupPath.Substring(0, backupPath.Length - DllSwapExecutor.BackupSuffix.Length)
                    : backupPath;

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

            operations.Add(new RecoveredOperation()
            {
                Record = record,
                RestoredPaths = restored,
                Warnings = warnings,
            });
        }

        return new RecoveryReport() { Operations = operations };
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
