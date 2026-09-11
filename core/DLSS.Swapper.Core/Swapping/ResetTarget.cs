namespace DLSS_Swapper.Swapping;

/// <summary>
/// One dll to put back: what its saved original should hash to, and what the file being replaced should.
/// </summary>
/// <param name="TargetPath">The dll in the game folder that is to be restored.</param>
/// <param name="ExpectedBackupHash">
/// The MD5 recorded when the original was saved, or null when nothing was recorded. A null skips
/// the check rather than failing it: a backup made by an older build, before hashes were kept, is
/// still the only original there is.
/// </param>
/// <param name="ExpectedCurrentHash">
/// The MD5 the dll at <paramref name="TargetPath"/> is expected to have - what this app last wrote
/// there, or failing that last saw there. When the file no longer matches, someone or something
/// changed it since, and restoring over it would erase that change unasked; the reset is refused
/// with <see cref="SwapFailure.TargetChanged"/> so the caller can ask. Null skips the check, which
/// is how a caller says the user has answered.
/// </param>
public readonly record struct ResetTarget(string TargetPath, string? ExpectedBackupHash, string? ExpectedCurrentHash = null);
