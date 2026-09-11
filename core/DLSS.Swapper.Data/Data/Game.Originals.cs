using CommunityToolkit.Mvvm.ComponentModel;
using AsyncAwaitBestPractices;
using DLSS_Swapper.Dlls;
using DLSS_Swapper.Extensions;
using DLSS_Swapper.Helpers;
using DLSS_Swapper.Interfaces;
using DLSS_Swapper.Swapping;
using DLSS_Swapper.Versioning;
using NvAPIWrapper.DRS;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SQLite;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DLSS_Swapper.Signing;
using DLSS_Swapper.Compatibility;

namespace DLSS_Swapper.Data;

/// <summary>
/// The saved originals: whether a dll has one, making one the first time a game is seen, and
/// putting one back beside the game from the library mirror.
/// </summary>
/// <remarks>
/// Same class as Game.cs, in a file of its own. A saved original is the only copy of what the game
/// shipped with, and the rules for when one may be made are subtle enough to want their own page.
/// </remarks>
public abstract partial class Game
{
    /// <summary>
    /// Whether this particular dll has a saved copy of the original beside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked about the path, not about the asset type. It used to be
    /// <c>GameAssets.Any(x =&gt; x.AssetType == definition.BackupAssetType)</c> - "is there a backup of
    /// any dll of this kind" - written out in four places. A game shipping the same dll in two
    /// folders has two assets of one type, so one backup answered for both: "Save a copy" backed up
    /// the first location, skipped the second, and reported success, and the row then read as
    /// protected while the second location had no original saved anywhere. A game update before the
    /// next swap destroyed the very file the user had asked to keep.
    /// </para>
    /// <para>
    /// A dll that IS a backup, or of a type the app does not manage, has nothing to protect and
    /// answers true - there is no missing copy to report.
    /// </para>
    /// <para>
    /// So does a dll of a type no game ships. Nobody's install had that file until a person or a
    /// tool put it there, so there is no original behind it and no amount of copying would make
    /// one. Reporting it as missing a copy would ask the user to fix something that is not broken,
    /// and every count of backup coverage in the app reads this.
    /// </para>
    /// </remarks>
    internal bool HasSavedOriginal(GameAsset gameAsset)
    {
        var definition = DllTypes.ForAssetType(gameAsset.AssetType);
        if (definition is null || definition.GamesShipThisDll == false)
        {
            return true;
        }

        var backupPath = DllSwapExecutor.GetBackupPath(gameAsset.Path);

        return GameAssets.Any(x => x.AssetType == definition.BackupAssetType
            && string.Equals(x.Path, backupPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Saves the copy of a dll that the game shipped with, if there is not one already.
    /// </summary>
    /// <remarks>
    /// The refusal for types games do not ship lives here rather than at the call sites, because
    /// both callers - the scan on first detection and "Save a copy" - would otherwise each have to
    /// remember it. Copying such a file records the version somebody installed as though it were
    /// the developer's, which the app would then offer to restore.
    /// </remarks>
    void CreateOriginalBackupForGameAsset(GameAsset gameAsset)
    {
        if (DllTypes.ForAssetType(gameAsset.AssetType)?.GamesShipThisDll == false)
        {
            return;
        }

        var backupPath = DllSwapExecutor.GetBackupPath(gameAsset.Path);
        if (File.Exists(backupPath))
        {
            return;
        }

        try
        {
            File.Copy(gameAsset.Path, backupPath);
            Logger.Info($"Backed up {gameAsset.Path} on first detection of {Title}.");

            // And a second copy in the library, where a game update cannot take it. Mirrored from
            // the .dlsss just written rather than from the game's file, so the two are the same bytes.
            OriginalsStore.Mirror(ID, gameAsset.Path, backupPath, gameAsset.Version);
        }
        catch (Exception err)
        {
            // Not worth interrupting a scan for. The game keeps working, it just has no backup,
            // which is where it would have been anyway.
            Logger.Warning($"Could not back up {gameAsset.Path}: {err.Message}");
        }
    }

    /// <summary>
    /// Adds the ".dlsss" backup beside a dll, if there is one.
    /// </summary>
    /// <param name="cachedGameAssets">
    /// What we had for this game before the scan, so an unchanged backup does not need re-hashing.
    /// Backups are the same size as the dlls they shadow, so this is worth as much as it is for
    /// the dlls themselves.
    /// </param>
    /// <summary>
    /// Saves a copy of every swappable dll in this game that does not already have one.
    /// </summary>
    /// <remarks>
    /// Backups are normally taken the first time a game is seen. This is for the games that missed
    /// that, either because they were in the library before the app started doing it or because the
    /// copy failed at the time. Existing backups are never overwritten, so a game that already has
    /// one keeps the original it has rather than gaining a copy of a dll that was swapped in later.
    /// </remarks>
    /// <returns>How many dlls now have a copy that did not before.</returns>
    internal async Task<int> SaveOriginalCopiesAsync()
    {
        var saved = SaveOriginalCopiesOnDisk();

        if (saved > 0)
        {
            // The list in memory is not the record. Games are read back from this table on the next
            // launch, so a backup that only exists in memory is reported as missing again the
            // moment the app restarts, which is exactly what happened.
            using (await Database.Instance.Mutex.LockAsync())
            {
                await Database.Instance.Connection.ExecuteAsync("DELETE FROM game_asset WHERE id = ?", ID).ConfigureAwait(false);
                await Database.Instance.Connection.InsertAllAsync(GameAssets, false).ConfigureAwait(false);
            }
        }

        return saved;
    }

    int SaveOriginalCopiesOnDisk()
    {
        var cachedGameAssets = new List<GameAsset>(GameAssets);
        var saved = 0;

        foreach (var gameAsset in cachedGameAssets)
        {
            if (HasSavedOriginal(gameAsset))
            {
                continue;
            }

            CreateOriginalBackupForGameAsset(gameAsset);

            // Registers the copy as a game asset, so the row stops reporting it as missing.
            LoadBackupForGameAsset(gameAsset, cachedGameAssets);

            // The same question again, so the count can only rise for a copy that actually landed
            // beside this dll. Asked type-wide, it counted the first location and then agreed the
            // second was done too.
            if (HasSavedOriginal(gameAsset))
            {
                saved += 1;
            }
        }

        if (saved > 0)
        {
            UpdateCurrentDLLsFromGameAssets();
        }

        RefreshRowStatus();
        return saved;
    }

    void LoadBackupForGameAsset(GameAsset gameAsset, List<GameAsset> cachedGameAssets)
    {
        var backupPath = DllSwapExecutor.GetBackupPath(gameAsset.Path);

        // A .dlsss that has gone - a game update, a verify, a patcher tidying up - comes back from
        // the library mirror before the original is counted as lost. Checked against the mirror's
        // own record on the way, so a damaged mirror is left where it is and named in the log.
        if (File.Exists(backupPath) == false && OriginalsStore.TryRestoreBackup(ID, gameAsset.Path, backupPath))
        {
            Logger.Info($"Restored the missing saved original beside {gameAsset.Path} from the library.");
        }

        if (File.Exists(backupPath))
        {
            var gameAssetBackup = new GameAsset()
            {
                Id = ID,
                AssetType = DLLManager.Instance.GetAssetBackupType(gameAsset.AssetType),
                Path = backupPath,
            };

            gameAssetBackup.LoadVersionAndSize();

            var cachedBackup = cachedGameAssets.FirstOrDefault(x => x.Path.Equals(backupPath, StringComparison.OrdinalIgnoreCase));
            if (cachedBackup is not null && gameAssetBackup.MatchesCachedFile(cachedBackup))
            {
                gameAssetBackup.Hash = cachedBackup.Hash;
                gameAssetBackup.Sha256 = cachedBackup.Sha256;
            }
            else
            {
                gameAssetBackup.LoadHash();
            }

            GameAssets.Add(gameAssetBackup);
        }
    }
}
