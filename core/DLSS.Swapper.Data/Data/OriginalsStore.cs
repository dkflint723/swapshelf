using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DLSS_Swapper.Swapping;

namespace DLSS_Swapper.Data;

/// <summary>
/// A second copy of every saved original, kept in the app's own folder.
/// </summary>
/// <remarks>
/// <para>
/// The first copy sits beside the dll it protects, as <c>nvngx_dlss.dll.dlsss</c>, and that is the
/// right place for it - restore is a rename on one volume. But it is inside the game folder, and
/// the game folder belongs to the game. Steam's "verify integrity", a launcher's repair, and any
/// patcher that removes files it does not recognise will take a .dlsss with everything else, and
/// with it the only copy of what the game shipped with. The originals are the one thing nothing
/// can recreate.
/// </para>
/// <para>
/// So each one is mirrored here, under the data folder the uninstaller already promises to leave
/// alone. Keyed by game and by the dll's full path, because one game can keep the same dll in two
/// places and they are two different files. The scan puts a missing .dlsss back from here before
/// it counts the original as gone, and the mirror is checked against the hash recorded when it was
/// made before it is trusted - the same rule the executor applies to a .dlsss.
/// </para>
/// <para>
/// Costs disk: an original is between 20 and 170 MB, once per dll per game. That is the price of
/// the promise, and it is behind a setting for anyone who would rather not pay it.
/// </para>
/// </remarks>
internal static class OriginalsStore
{
    const string SidecarExtension = ".json";

    /// <summary>Where a given game's copy of a given dll lives, and its sidecar.</summary>
    internal static (string CopyPath, string SidecarPath) LocationFor(string gameId, string sourcePath)
    {
        // The path is hashed rather than reproduced, so two locations of the same dll in one game
        // do not collide and a very long install path does not become a very long mirror path.
        var pathKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath.ToLowerInvariant()))).Substring(0, 16);
        var folder = Path.Combine(Storage.GetOriginalsFolder(), SafeSegment(gameId), pathKey);
        var copy = Path.Combine(folder, Path.GetFileName(sourcePath));
        return (copy, copy + SidecarExtension);
    }

    /// <summary>
    /// Copies a freshly saved original into the mirror, with a note of what it is and what it hashes to.
    /// </summary>
    /// <returns>True if the mirror now holds the copy. False is logged, never thrown - a scan must not stop for it.</returns>
    internal static bool Mirror(string gameId, string sourcePath, string savedCopyPath, string version)
    {
        if (Settings.Instance.KeepOriginalCopiesInLibrary == false)
        {
            return false;
        }

        try
        {
            var (copyPath, sidecarPath) = LocationFor(gameId, sourcePath);
            Storage.CreateDirectoryForFileIfNotExists(copyPath);

            FileDigests digests;
            using (var stream = new FileStream(savedCopyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                digests = FileHashes.Compute(stream);
            }

            // Written beside, then moved over, so a half-written mirror never looks like a whole one.
            var staged = copyPath + ".staged";
            File.Copy(savedCopyPath, staged, true);
            File.Move(staged, copyPath, true);

            var sidecar = new SidecarRecord()
            {
                GameId = gameId,
                SourcePath = sourcePath,
                FileName = Path.GetFileName(sourcePath),
                Version = version,
                Md5 = digests.Md5,
                Sha256 = digests.Sha256,
                SavedAt = DateTime.UtcNow,
            };
            File.WriteAllText(sidecarPath, JsonSerializer.Serialize(sidecar, SidecarJsonContext.Default.SidecarRecord));

            Logger.Info($"Mirrored the original {Path.GetFileName(sourcePath)} for {gameId} into the library.");
            return true;
        }
        catch (Exception err)
        {
            Logger.Warning($"Could not mirror the original {sourcePath} for {gameId}: {err.Message}");
            return false;
        }
    }

    /// <summary>
    /// Puts a missing .dlsss back from the mirror, if the mirror holds one that still hashes to what
    /// its sidecar recorded.
    /// </summary>
    /// <remarks>
    /// Checked before it is trusted, like a .dlsss is. A mirror that does not match its own record
    /// is left alone and named in the log; restoring it would be putting an unknown file back into
    /// a game under the name "original".
    /// </remarks>
    internal static bool TryRestoreBackup(string gameId, string sourcePath, string backupPath)
    {
        try
        {
            var (copyPath, sidecarPath) = LocationFor(gameId, sourcePath);
            if (File.Exists(copyPath) == false || File.Exists(sidecarPath) == false)
            {
                return false;
            }

            var sidecar = JsonSerializer.Deserialize(File.ReadAllText(sidecarPath), SidecarJsonContext.Default.SidecarRecord);
            if (sidecar is null || string.IsNullOrWhiteSpace(sidecar.Md5))
            {
                Logger.Warning($"The mirror for {sourcePath} has no readable record; not restoring from it.");
                return false;
            }

            using (var stream = new FileStream(copyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                // Both digests when the record has both; a record from before SHA-256 was kept is
                // checked by its MD5 alone.
                var digests = FileHashes.Compute(stream);
                var sha256Mismatch = string.IsNullOrWhiteSpace(sidecar.Sha256) == false
                    && FileHashes.HexEquals(digests.Sha256, sidecar.Sha256) == false;
                if (FileHashes.HexEquals(digests.Md5, sidecar.Md5) == false || sha256Mismatch)
                {
                    Logger.Error($"The mirror for {sourcePath} no longer matches its own record ({sidecar.Md5}); not restoring from it.");
                    return false;
                }
            }

            var staged = backupPath + ".staged";
            File.Copy(copyPath, staged, true);
            File.Move(staged, backupPath, true);

            Logger.Info($"Put the saved original for {sourcePath} back beside it from the library mirror.");
            return true;
        }
        catch (Exception err)
        {
            Logger.Warning($"Could not restore the saved original for {sourcePath} from the mirror: {err.Message}");
            return false;
        }
    }

    /// <summary>The hash the mirror was recorded with, or null when there is no usable mirror.</summary>
    internal static string? RecordedHash(string gameId, string sourcePath)
    {
        try
        {
            var (_, sidecarPath) = LocationFor(gameId, sourcePath);
            if (File.Exists(sidecarPath) == false)
            {
                return null;
            }

            var sidecar = JsonSerializer.Deserialize(File.ReadAllText(sidecarPath), SidecarJsonContext.Default.SidecarRecord);
            return string.IsNullOrWhiteSpace(sidecar?.Md5) ? null : sidecar.Md5;
        }
        catch (Exception err)
        {
            Logger.Warning($"Could not read the mirror record for {sourcePath}: {err.Message}");
            return null;
        }
    }

    static string SafeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return builder.Length == 0 ? "_" : builder.ToString();
    }

    internal sealed class SidecarRecord
    {
        public string GameId { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Md5 { get; set; } = string.Empty;
        public string? Sha256 { get; set; }
        public DateTime SavedAt { get; set; }
    }
}
