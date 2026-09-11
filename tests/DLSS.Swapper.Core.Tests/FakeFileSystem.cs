using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DLSS_Swapper.Swapping;
using System.Text;

namespace DLSS_Swapper.Tests;

/// <summary>
/// In-memory filesystem with the failure modes that matter for swapping, none of which are
/// practical to reproduce against a real game install.
/// </summary>
internal sealed class FakeFileSystem : IFileSystem
{
    readonly Dictionary<string, string> _files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _lockedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _readOnlyDirectoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public FakeFileSystem AddFile(string path, string contents)
    {
        _files[path] = contents;
        return this;
    }

    /// <summary>
    /// Models a dll held open by a running game. Windows lets another process keep reading the file,
    /// so copying <em>from</em> it still works, but renaming, replacing or deleting it does not.
    /// This distinction is the whole reason a swap can fail half way through.
    /// </summary>
    public FakeFileSystem LockFile(string path)
    {
        _lockedPaths.Add(path);
        return this;
    }

    /// <summary>Models a directory we lack write permission on, as with a game installed under Program Files.</summary>
    public FakeFileSystem DenyWritesTo(string path)
    {
        _readOnlyDirectoryPaths.Add(path);
        return this;
    }

    public string ReadFile(string path) => _files[path];

    public IReadOnlyCollection<string> AllPaths => _files.Keys.ToList();

    public bool FileExists(string path) => _files.ContainsKey(path);

    public Stream OpenRead(string path)
    {
        RequireExists(path);
        return new MemoryStream(Encoding.UTF8.GetBytes(_files[path]), writable: false);
    }

    public void Copy(string sourcePath, string destinationPath, bool overwrite)
    {
        RequireExists(sourcePath);
        GuardWrite(destinationPath);

        if (_files.ContainsKey(destinationPath) && overwrite == false)
        {
            throw AlreadyExists(destinationPath);
        }

        _files[destinationPath] = _files[sourcePath];
    }

    public void Move(string sourcePath, string destinationPath, bool overwrite)
    {
        RequireExists(sourcePath);

        // A rename needs delete access to the source, which a locked file will not give up.
        GuardWrite(sourcePath);
        GuardWrite(destinationPath);

        if (_files.ContainsKey(destinationPath) && overwrite == false)
        {
            throw AlreadyExists(destinationPath);
        }

        _files[destinationPath] = _files[sourcePath];
        _files.Remove(sourcePath);
    }

    public void Replace(string sourcePath, string destinationPath, string? backupPath)
    {
        RequireExists(sourcePath);
        RequireExists(destinationPath);

        GuardWrite(sourcePath);
        GuardWrite(destinationPath);

        if (backupPath is not null)
        {
            GuardWrite(backupPath);
            _files[backupPath] = _files[destinationPath];
        }

        _files[destinationPath] = _files[sourcePath];
        _files.Remove(sourcePath);
    }

    public void Delete(string path)
    {
        // File.Delete is a no-op on a missing file.
        if (_files.ContainsKey(path) == false)
        {
            return;
        }

        GuardWrite(path);
        _files.Remove(path);
    }

    void RequireExists(string path)
    {
        if (_files.ContainsKey(path) == false)
        {
            throw new FileNotFoundException($"Could not find file '{path}'.", path);
        }
    }

    void GuardWrite(string path)
    {
        if (_lockedPaths.Contains(path))
        {
            throw SharingViolation(path);
        }

        var directoryPath = DirectoryNameOf(path);
        if (directoryPath is not null && _readOnlyDirectoryPaths.Contains(directoryPath))
        {
            throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
        }
    }

    /// <summary>
    /// The directory part of <paramref name="path"/>, split on either separator rather than the
    /// host's. Every fixture here is a Windows path, and <see cref="Path.GetDirectoryName(string)"/>
    /// does not count a backslash as a separator off Windows, so on the Linux runner this returned
    /// empty and <see cref="DenyWritesTo"/> quietly stopped matching anything.
    /// </summary>
    static string? DirectoryNameOf(string path)
    {
        var separatorIndex = path.LastIndexOfAny(new[] { '\\', '/' });
        return separatorIndex < 0 ? null : path.Substring(0, separatorIndex);
    }

    static IOException SharingViolation(string path)
    {
        // ERROR_SHARING_VIOLATION, what you get when a game has the dll open.
        return new IOException($"The process cannot access the file '{path}' because it is being used by another process.", unchecked((int)0x80070020));
    }

    static IOException AlreadyExists(string path)
    {
        return new IOException($"The file '{path}' already exists.");
    }
}
