using System.IO;

namespace DLSS_Swapper.Swapping;

/// <summary>
/// The real filesystem. Every method is a direct passthrough so that behaviour differences between
/// this and the test double stay limited to what <see cref="System.IO.File"/> itself does.
/// </summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    public static PhysicalFileSystem Instance { get; } = new PhysicalFileSystem();

    public bool FileExists(string path) => File.Exists(path);

    // ReadWrite sharing, so a backup another process happens to have open for reading can still be
    // checked. Nothing legitimately writes a .dlsss while it is being restored.
    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    public void Copy(string sourcePath, string destinationPath, bool overwrite) => File.Copy(sourcePath, destinationPath, overwrite);

    public void Move(string sourcePath, string destinationPath, bool overwrite) => File.Move(sourcePath, destinationPath, overwrite);

    public void Replace(string sourcePath, string destinationPath, string? backupPath) => File.Replace(sourcePath, destinationPath, backupPath);

    public void Delete(string path) => File.Delete(path);
}
