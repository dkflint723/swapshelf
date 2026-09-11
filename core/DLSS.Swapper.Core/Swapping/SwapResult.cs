using System;
using System.Collections.Generic;

namespace DLSS_Swapper.Swapping;

/// <summary>
/// A backup file the executor created during this operation. The caller uses these to add backup
/// records to its own bookkeeping, and only ever sees backups that actually landed on disk.
/// </summary>
public sealed class CreatedBackup
{
    public required string TargetPath { get; init; }
    public required string BackupPath { get; init; }
}

/// <summary>
/// The outcome of a swap or reset.
/// </summary>
/// <remarks>
/// The executor guarantees that on failure the targets are back to their pre-operation contents,
/// so a caller seeing <see cref="Success"/> false can leave its own state untouched. The single
/// exception is <see cref="RollbackIncomplete"/>, which means a rollback step itself failed and
/// the caller should force a rescan rather than trust either its cache or this result.
/// </remarks>
public sealed class SwapResult
{
    public bool Success { get; init; }

    public SwapFailure Failure { get; init; } = SwapFailure.None;

    /// <summary>The path being operated on when things went wrong, when we know it.</summary>
    public string? FailedPath { get; init; }

    public Exception? Error { get; init; }

    /// <summary>Backups created by this operation. Empty when the operation failed.</summary>
    public IReadOnlyList<CreatedBackup> CreatedBackups { get; init; } = Array.Empty<CreatedBackup>();

    /// <summary>Targets whose contents were replaced. Empty when the operation failed.</summary>
    public IReadOnlyList<string> ReplacedPaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// True when we failed and could not fully undo what we had already done. Disk state is
    /// indeterminate and the caller must rescan rather than trust its cached asset list.
    /// </summary>
    public bool RollbackIncomplete { get; init; }

    /// <summary>Non-fatal problems worth logging, such as temp files we could not clean up.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    internal static SwapResult Ok(IReadOnlyList<CreatedBackup> createdBackups, IReadOnlyList<string> replacedPaths, IReadOnlyList<string> warnings)
    {
        return new SwapResult()
        {
            Success = true,
            CreatedBackups = createdBackups,
            ReplacedPaths = replacedPaths,
            Warnings = warnings,
        };
    }

    /// <summary>This result with more warnings on it. The same object when there are none to add.</summary>
    internal SwapResult WithWarnings(IReadOnlyList<string> extra)
    {
        if (extra.Count == 0)
        {
            return this;
        }

        var merged = new List<string>(Warnings);
        merged.AddRange(extra);

        return new SwapResult()
        {
            Success = Success,
            Failure = Failure,
            FailedPath = FailedPath,
            Error = Error,
            CreatedBackups = CreatedBackups,
            ReplacedPaths = ReplacedPaths,
            RollbackIncomplete = RollbackIncomplete,
            Warnings = merged,
        };
    }

    internal static SwapResult Fail(SwapFailure failure, string? failedPath = null, Exception? error = null, bool rollbackIncomplete = false, IReadOnlyList<string>? warnings = null)
    {
        return new SwapResult()
        {
            Success = false,
            Failure = failure,
            FailedPath = failedPath,
            Error = error,
            RollbackIncomplete = rollbackIncomplete,
            Warnings = warnings ?? Array.Empty<string>(),
        };
    }
}
