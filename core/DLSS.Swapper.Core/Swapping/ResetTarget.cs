namespace DLSS_Swapper.Swapping;

/// <summary>
/// One dll to put back, and what its saved original is supposed to hash to.
/// </summary>
/// <param name="TargetPath">The dll in the game folder that is to be restored.</param>
/// <param name="ExpectedBackupHash">
/// The MD5 recorded when the original was saved, or null when nothing was recorded. A null skips
/// the check rather than failing it: a backup made by an older build, before hashes were kept, is
/// still the only original there is.
/// </param>
public readonly record struct ResetTarget(string TargetPath, string? ExpectedBackupHash);
