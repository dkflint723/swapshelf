namespace DLSS_Swapper.Swapping;

/// <summary>
/// Why a swap or reset did not happen. The caller maps these onto localized messages; the executor
/// deliberately produces no user facing text of its own.
/// </summary>
public enum SwapFailure
{
    None = 0,

    /// <summary>The dll we were asked to swap in is not on disk.</summary>
    SourceMissing,

    /// <summary>
    /// The dll we were asked to swap in was built for a different processor architecture than the
    /// one it would replace - a 32-bit file over a 64-bit one, or the reverse. The game could not
    /// have loaded it, so nothing was written.
    /// </summary>
    ArchitectureMismatch,

    /// <summary>There was nothing to act on.</summary>
    NoTargets,

    /// <summary>A reset was requested but a target had no backup to restore from.</summary>
    BackupMissing,

    /// <summary>
    /// A reset was requested but a backup no longer hashes to what was recorded when it was saved.
    /// It was not restored: putting back a file that is not the original would leave the game
    /// running an unknown dll while the row says it was returned to what it shipped with.
    /// </summary>
    BackupTampered,

    /// <summary>
    /// A reset was requested but the dll to be replaced is no longer the one this app last wrote or
    /// last saw there. Nothing was restored: whatever changed it since - a game update, a mod, a fix
    /// applied by hand - would be erased without anyone having been asked. The caller asks, and
    /// repeats the reset with the check waived if the answer is yes.
    /// </summary>
    TargetChanged,

    /// <summary>We could not write to the game directory. Usually fixed by running elevated.</summary>
    AccessDenied,

    /// <summary>A target dll is held open by another process. Usually the game is running.</summary>
    FileInUse,

    /// <summary>Anything else. The originating exception is on the result.</summary>
    Unknown,
}
