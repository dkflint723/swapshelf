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
/// Writing into a game folder: the swap and the restore, the checks in front of each, and the
/// bookkeeping after.
/// </summary>
/// <remarks>
/// Same class as Game.cs, in a file of its own. Everything that changes a file the game loads is
/// here and nowhere else in the class.
/// </remarks>
public abstract partial class Game
{
    /// <summary>
    /// Whether something is running out of a game's install folder right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The swap executor already copes with a running game: the rename fails with a sharing
    /// violation, it is classified as FileInUse, and everything is rolled back with nothing changed.
    /// That is the safety net, and it stays. But a game that is running is not an error to recover
    /// from, it is a reason not to begin - and the user is better told "close the game" before
    /// anything is staged than "the swap failed" after.
    /// </para>
    /// <para>
    /// The check is the one play-clean already polls with. Swappable so a test can say "running"
    /// without having to run anything.
    /// </para>
    /// </remarks>
    internal static Func<string, bool> IsRunningCheck { get; set; } = PlayCleanSession.AnyProcessUnder;

    /// <param name="restoreChangedFiles">
    /// True when the user has been told the dll changed since it was swapped and has said to restore
    /// over it anyway. False asks: a dll that no longer hashes to what this app last wrote there is
    /// left alone and the result says so.
    /// </param>
    internal async Task<DllOperationResult> ResetDllAsync(GameAssetType gameAssetType, bool restoreChangedFiles = false)
    {
        // Restoring the original is the safe direction, but it is still a change to a game the user
        // asked to be left alone. Blocking both means the setting has one meaning rather than two.
        if (SkipUpdates)
        {
            return new DllOperationResult(false, ResourceHelper.GetString("Game_Swap_UpdatesTurnedOff"), false);
        }

        var backupRecordType = DLLManager.Instance.GetAssetBackupType(gameAssetType);
        var existingBackupRecords = this.GameAssets.Where(x => x.AssetType == backupRecordType).ToList();

        if (existingBackupRecords.Count == 0)
        {
            Logger.Info("No backup records found.");
            return new DllOperationResult(false, ResourceHelper.GetString("Game_Reset_RepairManually"), false);
        }

        // Pair every backup with the dll it restores before touching anything, so a game missing one
        // of its backups doesn't end up half restored.
        var restorePairs = new List<(GameAsset Backup, GameAsset Current)>();
        foreach (var existingBackupRecord in existingBackupRecords)
        {
            var primaryRecordName = TrimBackupSuffix(existingBackupRecord.Path);
            var existingRecords = this.GameAssets.Where(x => x.AssetType == gameAssetType && x.Path.Equals(primaryRecordName, StringComparison.OrdinalIgnoreCase)).ToList();

            if (existingRecords.Count != 1)
            {
                Logger.Info("Backup record was found, existing records were not.");
                return new DllOperationResult(false, ResourceHelper.GetString("Game_Reset_RepairManually"), false);
            }

            restorePairs.Add((existingBackupRecord, existingRecords[0]));
        }

        // Locations of this asset type with no backup at all. There is nothing to restore them from,
        // and reporting a plain success while leaving them swapped is how a game ends up running
        // mismatched dlls without the user ever being told.
        var restorableTargets = new HashSet<string>(restorePairs.Select(x => x.Current.Path), StringComparer.OrdinalIgnoreCase);
        var unrestorableRecords = this.GameAssets
            .Where(x => x.AssetType == gameAssetType && restorableTargets.Contains(x.Path) == false)
            .ToList();

        // Restoring is the safe direction, but a rename over a dll the game has open fails the same
        // way a swap does. Asked first so the answer is "close the game", not a failed restore.
        if (IsRunningCheck(InstallPath))
        {
            return new DllOperationResult(false, ResourceHelper.GetString("Game_GameRunning_CloseFirst"), false);
        }

        // Each backup goes back only if it still hashes to what was recorded when it was saved. A
        // backup made before hashes were kept has none recorded and is restored without the check -
        // it is still the only original there is.
        //
        // And each dll is replaced only if it is still what this app last put there, unless the
        // user has already been asked. Restoring over a file somebody changed since - a game update,
        // a mod, a fix by hand - erases their work, and "reset to default" never promised that.
        var resetResult = SwapExecutors.Create().Reset(restorePairs
            .Select(x => new ResetTarget(
                x.Current.Path,
                string.IsNullOrWhiteSpace(x.Backup.Hash) ? null : x.Backup.Hash,
                restoreChangedFiles ? null : ExpectedCurrentHash(x.Current)))
            .ToList(), new OperationLabel(ID, Title));

        foreach (var warning in resetResult.Warnings)
        {
            Logger.Warning(warning);
        }

        if (resetResult.Success == false)
        {
            if (resetResult.Error is not null)
            {
                Logger.Error(resetResult.Error);
            }

            if (resetResult.RollbackIncomplete)
            {
                // We couldn't get the game back to how we found it, so our cached view of it can't be trusted.
                NeedsProcessing = true;
            }

            return DescribeResetFailure(resetResult);
        }

        // Only now that the disk is committed do we update our own bookkeeping.
        var dllHistory = new List<GameHistory>();
        var newGameAssets = new List<GameAsset>();

        foreach (var (backupRecord, currentRecord) in restorePairs)
        {
            var newGameAsset = new GameAsset()
            {
                Id = ID,
                AssetType = gameAssetType,
                Path = currentRecord.Path,
                Version = backupRecord.Version,
                Hash = backupRecord.Hash,
                Sha256 = backupRecord.Sha256,
            };
            newGameAssets.Add(newGameAsset);

            dllHistory.Add(new GameHistory()
            {
                GameId = ID,
                EventType = GameHistoryEventType.DLLReset,
                EventTime = DateTime.Now,
                AssetType = gameAssetType,
                AssetPath = currentRecord.Path,
                AssetVersion = backupRecord.DisplayName,
            });

            GameAssets.Remove(currentRecord);
            GameAssets.Remove(backupRecord);
        }

        GameAssets.AddRange(newGameAssets);

        foreach (var newGameAsset in newGameAssets)
        {
            UpdateCurrentAsset(newGameAsset, gameAssetType);
        }

        using (await Database.Instance.Mutex.LockAsync())
        {
            await Database.Instance.Connection.InsertAllAsync(dllHistory, false);

            // Update game assets list by deleting and re-adding.
            await Database.Instance.Connection.ExecuteAsync("DELETE FROM game_asset WHERE id = ?", ID).ConfigureAwait(false);
            await Database.Instance.Connection.InsertAllAsync(GameAssets, false).ConfigureAwait(false);
        }

        // Restoring an older dll can put the game behind again, so the badge has to be recomputed.
        RefreshUpdateAvailable();

        if (unrestorableRecords.Count > 0)
        {
            foreach (var unrestorableRecord in unrestorableRecords)
            {
                Logger.Warning($"No backup to restore for {unrestorableRecord.Path}, it has been left unchanged.");
            }

            var totalCount = restorePairs.Count + unrestorableRecords.Count;
            return new DllOperationResult(true, ResourceHelper.GetFormattedResourceTemplate("Game_Reset_PartialTemplate", restorePairs.Count, totalCount, unrestorableRecords.Count), false);
        }

        return new DllOperationResult(true, string.Empty, false);
    }

    static string TrimBackupSuffix(string backupPath)
    {
        // Not Replace, that would also mangle a path containing the suffix somewhere in the middle.
        if (backupPath.EndsWith(DllSwapExecutor.BackupSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return backupPath.Substring(0, backupPath.Length - DllSwapExecutor.BackupSuffix.Length);
        }

        return backupPath;
    }

    DllOperationResult DescribeSwapFailure(SwapResult result)
    {
        switch (result.Failure)
        {
            case SwapFailure.SourceMissing:
                return new DllOperationResult(false, ResourceHelper.GetString("Game_Swap_DownloadedDllNotFound"), false, result.Failure);

            case SwapFailure.NoTargets:
                return new DllOperationResult(false, ResourceHelper.GetString("Game_Swap_NoDllRecordsToUpdate"), false, result.Failure);

            case SwapFailure.AccessDenied:
                if (Environment.IsPrivilegedProcess is false)
                {
                    return new DllOperationResult(false, ResourceHelper.GetString("Game_Swap_AccessDeniedAdmin"), true, result.Failure);
                }
                return new DllOperationResult(false, ResourceHelper.GetString("Game_Swap_AccessDenied"), false, result.Failure);

            case SwapFailure.FileInUse:
                return new DllOperationResult(false, ResourceHelper.GetString("Game_Swap_FileInUse"), false, result.Failure);

            case SwapFailure.ArchitectureMismatch:
                return new DllOperationResult(false, ResourceHelper.GetString("Game_Swap_ArchitectureMismatch"), false, result.Failure);

            default:
                return new DllOperationResult(false, ResourceHelper.GetString("Game_Swap_UnknownError"), false, result.Failure);
        }
    }

    DllOperationResult DescribeResetFailure(SwapResult result)
    {
        switch (result.Failure)
        {
            case SwapFailure.AccessDenied:
                if (Environment.IsPrivilegedProcess is false)
                {
                    return new DllOperationResult(false, ResourceHelper.GetString("Game_Reset_AccessDeniedAdmin"), true, result.Failure);
                }
                return new DllOperationResult(false, ResourceHelper.GetString("Game_Reset_RepairManually"), false, result.Failure);

            case SwapFailure.FileInUse:
                return new DllOperationResult(false, ResourceHelper.GetString("Game_Reset_FileInUse"), false, result.Failure);

            case SwapFailure.BackupTampered:
                return new DllOperationResult(false, ResourceHelper.GetString("Game_Reset_BackupTampered"), false, result.Failure);

            case SwapFailure.TargetChanged:
                // Not an error: a question. The caller sees NeedsConfirmation and asks it.
                return new DllOperationResult(false, ResourceHelper.GetString("Game_Reset_TargetChanged"), false, result.Failure);

            default:
                return new DllOperationResult(false, ResourceHelper.GetString("Game_Reset_RepairManually"), false, result.Failure);
        }
    }

    /// <summary>
    /// What the dll now at a path is expected to hash to: what this app last wrote there, or failing
    /// that what the last scan saw. Null when neither is known, which skips the check.
    /// </summary>
    static string? ExpectedCurrentHash(GameAsset current)
    {
        if (string.IsNullOrWhiteSpace(current.SwappedHash) == false)
        {
            return current.SwappedHash;
        }

        return string.IsNullOrWhiteSpace(current.Hash) ? null : current.Hash;
    }

    /// <summary>
    /// Attempts to update a DLSS dll in a given game.
    /// </summary>
    /// <param name="dlssRecord"></param>
    /// <returns>Tuple containing a boolean of Success, if this is false there will be an error message in the Message response.</returns>
    internal async Task<DllOperationResult> UpdateDllAsync(DLLRecord dllRecord)
    {
        // Locked means locked, not merely left out of bulk updates. A game excluded because a
        // modified dll gets it flagged by anti cheat is no safer if the swap can still be done by
        // hand, and a promise that only covers one route is worse than no promise.
        if (SkipUpdates)
        {
            return new DllOperationResult(false, ResourceHelper.GetString("Game_Swap_UpdatesTurnedOff"), false);
        }

        if (dllRecord is null)
        {
            return new DllOperationResult(false, ResourceHelper.GetString("Game_Swap_DllRecordNotFound"), false);
        }

        if (dllRecord.LocalRecord is null)
        {
            return new DllOperationResult(false, ResourceHelper.GetString("Game_Swap_LocalDllRecordNotFound"), false);
        }

        if (File.Exists(dllRecord.LocalRecord.ExpectedPath) == false)
        {
            return new DllOperationResult(false, ResourceHelper.GetString("Game_Swap_DownloadedDllNotFound"), false);
        }

        var existingRecords = this.GameAssets.Where(x => x.AssetType == dllRecord.AssetType).ToList();
        if (existingRecords.Count == 0)
        {
            return new DllOperationResult(false, ResourceHelper.GetString("Game_Swap_NoDllRecordsToUpdate"), false);
        }

        var backupRecordType = DLLManager.Instance.GetAssetBackupType(dllRecord.AssetType);

        var versionInfo = FileVersionInfo.GetVersionInfo(dllRecord.LocalRecord.ExpectedPath);
        var dllVersion = versionInfo.GetFormattedFileVersion();

        // Both digests from one read. MD5 is the identity the manifest keys every record by and has
        // to match; SHA-256 is compared when the record carries one and learned when it does not, so
        // from the first swap on there is a digest a forger cannot cheaply collide to check against.
        var digests = versionInfo.GetDigests();
        if (FileHashes.HexEquals(dllRecord.MD5Hash, digests.Md5) == false || dllRecord.MatchesSha256(digests.Sha256) == false)
        {
            return new DllOperationResult(false, ResourceHelper.GetString("Game_Swap_InvalidHash"), false);
        }

        dllRecord.RememberSha256(digests.Sha256);


        // Validate new DLL: valid, and from the vendor that makes this kind of dll. Chaining to a
        // trusted root only proves somebody signed it, and the check used to stop there.
        if (Settings.Instance.AllowUntrusted == false)
        {
            var vendor = DllTypes.ForAssetType(dllRecord.AssetType)?.Vendor ?? DllVendor.Unknown;
            var signature = WinTrust.VerifyForVendor(dllRecord.LocalRecord.ExpectedPath, vendor);
            if (signature.IsTrustedForVendor == false)
            {
                if (signature.Verdict == SignatureVerdict.SignedByOtherPublisher)
                {
                    return new DllOperationResult(false, ResourceHelper.GetFormattedResourceTemplate(
                        "Game_Swap_SignedByOtherPublisherTemplate",
                        signature.Publisher ?? "?",
                        PublisherAllowList.ExpectedPublisher(vendor)), false);
                }

                return new DllOperationResult(false, ResourceHelper.GetString("Game_Swap_UntrustedSignature"), false);
            }
        }

        // A running game holds its dlls open. The executor would notice when the rename failed and
        // roll back cleanly - that safety net stays - but "close the game and try again" is a better
        // answer than a failed swap, and this is where there is still nothing to undo.
        if (IsRunningCheck(InstallPath))
        {
            return new DllOperationResult(false, ResourceHelper.GetString("Game_GameRunning_CloseFirst"), false);
        }

        // Every location this game keeps the dll in is swapped as one operation. The executor backs up
        // each one that needs it, stages the writes, and puts everything back if any step fails, so a
        // failure here means nothing on disk changed.
        var swapResult = SwapExecutors.Create().Swap(dllRecord.LocalRecord.ExpectedPath, existingRecords.Select(x => x.Path).ToList(), new OperationLabel(ID, Title));

        foreach (var warning in swapResult.Warnings)
        {
            Logger.Warning(warning);
        }

        if (swapResult.Success == false)
        {
            if (swapResult.Error is not null)
            {
                Logger.Error(swapResult.Error);
            }

            if (swapResult.RollbackIncomplete)
            {
                // We couldn't get the game back to how we found it, so our cached view of it can't be trusted.
                NeedsProcessing = true;
            }

            return DescribeSwapFailure(swapResult);
        }

        // Only now that the disk is committed do we update our own bookkeeping.
        var newGameAssets = new List<GameAsset>();

        foreach (var createdBackup in swapResult.CreatedBackups)
        {
            var backedUpRecord = existingRecords.First(x => x.Path.Equals(createdBackup.TargetPath, StringComparison.OrdinalIgnoreCase));

            newGameAssets.Add(new GameAsset()
            {
                Id = ID,
                AssetType = backupRecordType,
                Path = createdBackup.BackupPath,
                Version = backedUpRecord.Version,
                Hash = backedUpRecord.Hash,
                Sha256 = backedUpRecord.Sha256,
            });
        }

        var dllHistory = new List<GameHistory>();

        foreach (var existingRecord in existingRecords)
        {
            // No need to call LoadVersionAndHash, the data is already here.
            newGameAssets.Add(new GameAsset()
            {
                Id = ID,
                AssetType = dllRecord.AssetType,
                Path = existingRecord.Path,
                Version = dllVersion,
                Hash = dllRecord.MD5Hash,
                SwappedHash = dllRecord.MD5Hash,
                Sha256 = digests.Sha256,
            });

            dllHistory.Add(new GameHistory()
            {
                GameId = ID,
                EventType = GameHistoryEventType.DLLSwapped,
                EventTime = DateTime.Now,
                AssetType = dllRecord.AssetType,
                AssetPath = existingRecord.Path,
                AssetVersion = dllRecord.DisplayName,
            });
        }

        foreach (var existingRecrod in existingRecords)
        {
            GameAssets.Remove(existingRecrod);
        }
        GameAssets.AddRange(newGameAssets);

        // This should never be null.
        // Using FirstOrDefault as there may be multiple, but we only care about using the information of the first.
        var firstNewGameAsset = newGameAssets.FirstOrDefault(x => x.AssetType == dllRecord.AssetType);
        if (firstNewGameAsset is not null)
        {
            UpdateCurrentAsset(firstNewGameAsset, dllRecord.AssetType);
        }

        // Update game assets list by deleting and re-adding.
        using (await Database.Instance.Mutex.LockAsync())
        {
            await Database.Instance.Connection.InsertAllAsync(dllHistory, false);
            await Database.Instance.Connection.ExecuteAsync("DELETE FROM game_asset WHERE id = ?", ID).ConfigureAwait(false);
            await Database.Instance.Connection.InsertAllAsync(GameAssets, false).ConfigureAwait(false);
        }

        // The game is no longer behind on this dll, so the badge has to be recomputed. Without this
        // it only refreshed when the manifest reloaded or the game was rescanned.
        RefreshUpdateAvailable();

        return new DllOperationResult(true, string.Empty, false);
    }

    void UpdateCurrentAsset(GameAsset newGameAsset, GameAssetType gameAssetType)
    {
        UiThread.Run(() =>
        {
            var assetSlot = GetAssetSlot(gameAssetType);
            if (assetSlot is null)
            {
                Logger.Error($"Unknown AssetType: {gameAssetType}");
                return;
            }

            // Cleared first so the change is raised even when the same instance comes back, which
            // is what the assignments this replaced were doing.
            assetSlot.CurrentAsset = null;
            assetSlot.CurrentAsset = newGameAsset;
        });
    }
}
