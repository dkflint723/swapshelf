using System.IO;

namespace DLSS_Swapper.Swapping;

/// <summary>
/// The subset of file operations the swap executor needs. Exists so the executor can be tested
/// against an in-memory filesystem, including failure modes (locked files, denied permissions)
/// that are impractical to reproduce against real game installs.
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);

    /// <summary>
    /// Opens a file for reading. The executor hashes a saved original through this before putting
    /// it back, so a copy that is not what was saved is refused rather than restored.
    /// </summary>
    Stream OpenRead(string path);

    void Copy(string sourcePath, string destinationPath, bool overwrite);

    void Move(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>
    /// Atomically replaces <paramref name="destinationPath"/> with <paramref name="sourcePath"/>, moving the
    /// existing contents of the destination to <paramref name="backupPath"/> (when supplied).
    /// </summary>
    /// <remarks>
    /// This is the operation that keeps a swap crash-safe. Unlike a delete-then-move, the destination
    /// path is never absent, so an interruption can't leave a game missing a dll. Both paths must be on
    /// the same volume, which the executor guarantees by staging alongside the target.
    /// </remarks>
    void Replace(string sourcePath, string destinationPath, string? backupPath);

    void Delete(string path);
}
