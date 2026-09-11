using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DLSS_Swapper.Dlls;
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
/// the promise, and it is behind a setting for anyone who would rather not pay it. A copy is never
/// made that would leave less than <see cref="MinimumFreeSpaceBytes"/> free, since the catch-up
/// below runs unattended and a full disk is a worse problem than a missing second copy.
/// </para>
/// <para>
/// Every saved original gets one, however it was saved: on first sight of a game, by "Save a copy",
/// or by the executor at swap time - and, through <see cref="CatchUp"/>, every original saved before
/// the mirror existed, which for an existing library is nearly all of them.
/// </para>
/// </remarks>
internal static class OriginalsStore
{
    const string SidecarExtension = ".json";

    /// <summary>What a mirror copy may leave free on the volume it is written to, at least.</summary>
    internal const long MinimumFreeSpaceBytes = 1L << 30;

    /// <summary>What became of one attempt to mirror an original.</summary>
    internal enum MirrorOutcome
    {
        Mirrored,
        TurnedOff,
        NotTheRecordedFile,
        NotEnoughSpace,
        Failed,
    }

    /// <summary>
    /// Free bytes on the volume holding a path, or null when that cannot be told. Replaceable so a
    /// test can stand in for a nearly full disk.
    /// </summary>
    internal static Func<string, long?> FreeSpaceProbe { get; set; } = DefaultFreeSpace;

    static long? DefaultFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            // A network share, or anything else DriveInfo cannot describe. Unknown is not a refusal.
            return null;
        }
    }

    /// <summary>Whether the library already holds a copy of this game's original at this path.</summary>
    internal static bool HasMirror(string gameId, string sourcePath)
    {
        var (copyPath, sidecarPath) = LocationFor(gameId, sourcePath);
        return File.Exists(copyPath) && File.Exists(sidecarPath);
    }

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
    /// Copies a saved original into the mirror, with a note of what it is and what it hashes to.
    /// </summary>
    /// <param name="expectedMd5">
    /// What the saved original is recorded as hashing to, when there is a record. A file that no
    /// longer matches it is not copied: the mirror would otherwise preserve, under the name
    /// "original", whatever replaced it.
    /// </param>
    /// <returns>True if the mirror now holds the copy. False is logged, never thrown - a scan must not stop for it.</returns>
    internal static bool Mirror(string gameId, string sourcePath, string savedCopyPath, string version, string? expectedMd5 = null)
    {
        return MirrorWithOutcome(gameId, sourcePath, savedCopyPath, version, expectedMd5) == MirrorOutcome.Mirrored;
    }

    internal static MirrorOutcome MirrorWithOutcome(string gameId, string sourcePath, string savedCopyPath, string version, string? expectedMd5 = null)
    {
        if (Settings.Instance.KeepOriginalCopiesInLibrary == false)
        {
            return MirrorOutcome.TurnedOff;
        }

        try
        {
            var (copyPath, sidecarPath) = LocationFor(gameId, sourcePath);

            FileDigests digests;
            using (var stream = new FileStream(savedCopyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                digests = FileHashes.Compute(stream);
            }

            if (string.IsNullOrWhiteSpace(expectedMd5) == false && FileHashes.HexEquals(digests.Md5, expectedMd5) == false)
            {
                Logger.Warning($"The saved original {savedCopyPath} hashes to {digests.Md5}, not the {expectedMd5} it was recorded as; not copying it to the library.");
                return MirrorOutcome.NotTheRecordedFile;
            }

            var length = new FileInfo(savedCopyPath).Length;
            var free = FreeSpaceProbe(Storage.GetOriginalsFolder());
            if (free is not null && free.Value - length < MinimumFreeSpaceBytes)
            {
                Logger.Warning($"Not copying the original {Path.GetFileName(sourcePath)} for {gameId} to the library: it would leave under {MinimumFreeSpaceBytes / (1024 * 1024)} MB free.");
                return MirrorOutcome.NotEnoughSpace;
            }

            Storage.CreateDirectoryForFileIfNotExists(copyPath);

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
            return MirrorOutcome.Mirrored;
        }
        catch (Exception err)
        {
            Logger.Warning($"Could not mirror the original {sourcePath} for {gameId}: {err.Message}");
            return MirrorOutcome.Failed;
        }
    }

    /// <summary>
    /// Copies every saved original these games have that the library holds no copy of yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mirror used to be made only when a game was first found. Every original saved before it
    /// existed - most of any library - and every one the executor saved at swap time went without,
    /// so the protection was missing exactly where the valuable originals were. This catches them up,
    /// and costs two File.Exists per dll once they are caught up.
    /// </para>
    /// <para>
    /// Each copy is checked against the hash its row records, like a restore is. Stops at the first
    /// copy refused for disk space rather than being refused again for every one after it. Types no
    /// game ships have no original to protect and are skipped, as they are when backups are made.
    /// </para>
    /// </remarks>
    /// <returns>How many copies were made.</returns>
    internal static int CatchUp(IEnumerable<Game> games)
    {
        if (Settings.Instance.KeepOriginalCopiesInLibrary == false)
        {
            return 0;
        }

        var made = 0;
        foreach (var game in games)
        {
            List<GameAsset> backups;
            try
            {
                backups = game.GameAssets
                    .Where(x => DllTypes.IsBackupAssetType(x.AssetType)
                        && DllTypes.ForAssetTypeIncludingBackup(x.AssetType)?.GamesShipThisDll != false)
                    .ToList();
            }
            catch (InvalidOperationException)
            {
                // A scan or a swap changed the list while it was being read. The next launch catches up.
                continue;
            }

            foreach (var backup in backups)
            {
                var sourcePath = Game.TrimBackupSuffix(backup.Path);
                if (HasMirror(game.ID, sourcePath) || File.Exists(backup.Path) == false)
                {
                    continue;
                }

                var expected = string.IsNullOrWhiteSpace(backup.Hash) ? null : backup.Hash;
                switch (MirrorWithOutcome(game.ID, sourcePath, backup.Path, backup.Version, expected))
                {
                    case MirrorOutcome.Mirrored:
                        made++;
                        break;
                    case MirrorOutcome.NotEnoughSpace:
                        return made;
                }
            }
        }

        return made;
    }

    /// <summary>
    /// Runs <see cref="CatchUp"/> off the calling thread, logging what it did. It can copy gigabytes
    /// the first time, so nothing waits for it.
    /// </summary>
    internal static void CatchUpInBackground(IReadOnlyList<Game> games)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var made = CatchUp(games);
                if (made > 0)
                {
                    Logger.Info($"Copied {made} saved original(s) that had no second copy into the library.");
                }
            }
            catch (Exception err)
            {
                Logger.Error(err, "Could not catch up the library copies of saved originals.");
            }
        });
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
