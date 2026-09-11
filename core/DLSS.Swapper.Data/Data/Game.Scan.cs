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
/// Scanning a game's folder: enumerating dlls, recording what was found, and deciding whether a walk
/// is due at all.
/// </summary>
/// <remarks>
/// Same class as Game.cs, in a file of its own. Nothing here writes into a game folder; that is the
/// swap file's job, and the two are kept apart on purpose.
/// </remarks>
public abstract partial class Game
{
    /// <summary>
    /// How long a "there is nothing in this game" answer is trusted for.
    /// </summary>
    /// <remarks>
    /// A backstop rather than the real guard, which is <see cref="HasUnrecordedDlls"/>. It exists so
    /// that anything that check cannot see - a folder that was unreadable at the time, a dll type
    /// added to the app since - is picked up eventually rather than never.
    /// </remarks>
    const double FullRescanIntervalDays = 7;

    /// <summary>
    /// Detects DLSS and updates cover image.
    /// </summary>
    public void ProcessGame(bool autoSave = true)
    {
        // If we are alreayd procssing we don't need to process again
        if (Processing == true)
        {
            return;
        }

        UiThread.Run(() =>
        {
            NeedsProcessing = false;
        });

        if (string.IsNullOrEmpty(InstallPath))
        {
            return;
        }

        if (Directory.Exists(InstallPath) == false)
        {
            return;
        }

        UiThread.Run(() =>
        {
            Processing = true;
            HasSwappableItems = false;
        });

        ThreadPool.QueueUserWorkItem(async (stateInfo) =>
        {
            // Declared out here so the catch can put it back. See the catch for why.
            var oldGameAssets = new List<GameAsset>();

            var newHasSwappableItems = false;

            try
            {
                var shouldUpdatedCover = true;

                // A hidden entry with nothing swappable in it is not drawn anywhere and cannot be
                // acted on, so fetching art for it is work with no destination. Steam and Xbox mark
                // their own non-game entries hidden on sight, which is what these mostly are -
                // runtimes, redistributables, launchers - and they are also the entries least
                // likely to have any art to find.
                //
                // Self correcting. Un-hiding a game clears IsHidden, and a game that gains a
                // swappable dll clears the other half, so either one puts it back in the queue.
                //
                // There used to be a force branch here that made Refresh bypass the freshness
                // check below, which meant pressing Refresh refetched every non-custom cover in
                // the library from the network - a per-game roundtrip the button's name never
                // promised. The backoff below is the one place "is this cover fresh enough"
                // lives; somebody who wants new art has the labelled route, Find covers.
                if (IsHidden == true && HasSwappableItems == false)
                {
                    shouldUpdatedCover = false;
                }
                else if (_isLoadingCoverImage)
                {
                    // The cache-load's own cover fetch may still be in flight, now that nothing
                    // awaits it. Consulting its guard keeps "one fetch per launch" the one rule in
                    // its one place - see the WHY on LoadCoverImageAsync.
                    shouldUpdatedCover = false;
                }
                else
                {
                    // This shouldn't crash, bit if it does lets not take down the entire processing.
                    try
                    {
                        FileInfo? fileInfo = null;
                        if (HasUsableCover(ExpectedCustomCoverImage))
                        {
                            // If we are using a custom cover we don't want to try reloading any cover so we don't set fileInfo.
                            shouldUpdatedCover = false;
                        }
                        else if (HasUsableCover(ExpectedCoverImage))
                        {
                            fileInfo = new FileInfo(ExpectedCoverImage);
                        }
                        else if (File.Exists(ExpectedCoverImageUnavailableMarker))
                        {
                            // A cover we already failed to fetch. Its marker goes through the same
                            // backoff below, so a game with no cover waits exactly as long before
                            // asking again as a game with one does - rather than asking on every
                            // launch for ever, which is what happened while only the cover file
                            // could hold the answer.
                            fileInfo = new FileInfo(ExpectedCoverImageUnavailableMarker);
                        }

                        if (fileInfo is not null)
                        {
                            var daysSinceLastModified = (DateTime.Now - fileInfo.LastWriteTime).TotalDays;

                            // Add +/- 2 days so not all will process at the same time.
                            daysSinceLastModified += ((new Random()).NextDouble() - 0.5) * 4.0;

                            // If its less than 7 days lets not try refresh.
                            if (daysSinceLastModified < CoverLookupRetryDays)
                            {
                                shouldUpdatedCover = false;
                            }
                        }
                    }
                    catch (Exception err)
                    {
                        Logger.Error(err);
                        Debugger.Break();
                    }
                }

                Task? coverImageTask = null;
                if (shouldUpdatedCover)
                {
                    coverImageTask = UpdateCacheImageAsync();
                }
                else
                {
                    Logger.Verbose($"Skipping updating cover for {Title}");
                }

                var enumerationOptions = new EnumerationOptions();
                enumerationOptions.RecurseSubdirectories = true;
                enumerationOptions.AttributesToSkip |= FileAttributes.ReparsePoint;

                oldGameAssets = GameAssets.ToList();
                GameAssets.Clear();
                // TODO: See if changing these to filter specific files, or getting very *.dll and looking for our specific ones is faster
                //
                // Reuses the walk the guard already did, when there was one. HasUnrecordedDlls
                // enumerates this same tree to decide whether this scan should happen at all, so
                // doing it again here was the folder read twice in a row for every game that had
                // changed.
                var dllPaths = TakeDllPathsFromLastCheck()
                    ?? Directory.GetFiles(InstallPath, "*.dll", enumerationOptions);

                // Which anti-cheat this game carries, from the dll paths just enumerated plus the
                // top-level folders - both already cheap. It decides what the anti-cheat note says
                // and whether a game that gains one after being acknowledged is asked about again.
                RecordAntiCheat(dllPaths);
                RecordStreamline(dllPaths);

                /*
                var dlssDllPaths = Directory.GetFiles(InstallPath, "nvngx_dlss.dll", enumerationOptions);
                var dlssgDllPaths = Directory.GetFiles(InstallPath, "nvngx_dlssg.dll", enumerationOptions);
                var dlssdDllPaths = Directory.GetFiles(InstallPath, "nvngx_dlssd.dll", enumerationOptions);
                var xessDllPaths = Directory.GetFiles(InstallPath, "libxess.dll", enumerationOptions);
                */

                var dllHistory = new List<GameHistory>();
                var unknownGameAssets = new List<GameAsset>();

                // We have never recorded a dll for this game, so whatever is here now is what the
                // game shipped with. That is the only moment we can be sure of it, which is why
                // backing up happens here and not on every scan.
                // Was "the first time this game is seen", which left every dll found later
                // unprotected: a game that gained one in a patch had it detected and never backed
                // up. A dll the app has only just noticed has never been swapped by it, so the file
                // sitting there is the original, and CreateOriginalBackupForGameAsset refuses to
                // overwrite an existing copy, so this cannot promote a swapped dll to "original".
                var shouldBackUpNewDlls = Settings.Instance.BackupNewGamesAutomatically;

                async Task ProcessGame_ProcessGameAsset(GameAsset gameAsset)
                {
                    // Version and size first, both metadata. The hash is only worth paying for when
                    // the file actually looks different to what we already had.
                    gameAsset.LoadVersionAndSize();

                    var oldGameAsset = oldGameAssets.FirstOrDefault(x => x.Path.Equals(gameAsset.Path, StringComparison.OrdinalIgnoreCase));

                    if (oldGameAsset is not null && gameAsset.MatchesCachedFile(oldGameAsset))
                    {
                        gameAsset.Hash = oldGameAsset.Hash;
                        gameAsset.Sha256 = oldGameAsset.Sha256;
                    }
                    else
                    {
                        gameAsset.LoadHash();
                    }

                    if (oldGameAsset is not null) // DLL existed previously
                    {
                        // What this app last wrote here follows the row, not the file. The scan
                        // rebuilds rows from disk, so without this a restart forgot every swap.
                        gameAsset.SwappedHash = oldGameAsset.SwappedHash;

                        if (gameAsset.Version == oldGameAsset.Version)
                        {
                            // NOOP
                        }
                        else
                        {
                            dllHistory.Add(new GameHistory()
                            {
                                GameId = ID,
                                EventType = GameHistoryEventType.DLLChangedExternally,
                                EventTime = DateTime.Now,
                                AssetType = gameAsset.AssetType,
                                AssetPath = gameAsset.Path,
                                AssetVersion = gameAsset.DisplayName,
                            });

                            // The saved original stays exactly where it is.
                            //
                            // Upstream deletes it here, so that a game which updated its own dll past
                            // the version you swapped to does not read as a downgrade. That trades the
                            // only copy of the file the game shipped with for a display detail, and
                            // the copy is the whole point of the app: it is what "restore" restores,
                            // and once deleted no rescan, reinstall or verify brings it back. It cost
                            // 29 saved originals across 13 games in one scan of this library, on dlls
                            // that had been replaced outside the app.
                            //
                            // What upstream was avoiding is a real confusion, but the answer to it is
                            // words, not deletion: every surface that offers a restore in this fork
                            // already names both ends of it - the picker shows Original DLL beside
                            // Current DLL, the revert preview reads "DLSS: v310.8 -> v310.1", and the
                            // row now says which version the saved original is whenever it differs
                            // from what is installed. Nobody restores without being told what they
                            // get, so nothing has to be destroyed to keep them from being surprised.
                        }
                    }
                    else // DLL is new
                    {
                        dllHistory.Add(new GameHistory()
                        {
                            GameId = ID,
                            EventType = GameHistoryEventType.DLLDetected,
                            EventTime = DateTime.Now,
                            AssetType = gameAsset.AssetType,
                            AssetPath = gameAsset.Path,
                            AssetVersion = gameAsset.DisplayName,
                        });
                    }

                    if (DLLManager.Instance.IsInKnownGameAsset(gameAsset, this) == false)
                    {
                        unknownGameAssets.Add(gameAsset);
                    }

                    // Safe to call unconditionally: it refuses to overwrite an existing copy, so a
                    // dll that changed externally keeps the original saved beside it rather than
                    // having the replacement promoted over the top of it.
                    if (shouldBackUpNewDlls)
                    {
                        CreateOriginalBackupForGameAsset(gameAsset);
                    }

                    LoadBackupForGameAsset(gameAsset, oldGameAssets);

                }

                foreach (var dllPath in dllPaths)
                {
                    // Matched case insensitively the way Windows treats file names. The chain this
                    // replaced compared exactly, so a game shipping NVNGX_DLSS.DLL went unnoticed.
                    var dllTypeDefinition = DllTypes.ForFileName(Path.GetFileName(dllPath));
                    if (dllTypeDefinition is null)
                    {
                        continue;
                    }

                    var gameAsset = new GameAsset()
                    {
                        Id = ID,
                        AssetType = dllTypeDefinition.AssetType,
                        Path = dllPath,
                    };
                    try
                    {
                        await ProcessGame_ProcessGameAsset(gameAsset).ConfigureAwait(false);
                    }
                    catch (Exception assetErr)
                    {
                        // One unreadable dll is one unreadable dll. This used to unwind to the
                        // catch around the whole scan, which abandoned every other dll in the game
                        // and threw away the history accumulated so far - including notes about
                        // backups this same pass had already deleted from disk.
                        Logger.Error(assetErr, $"Could not read {gameAsset.Path}, skipping it.");

                        continue;
                    }

                    GameAssets.Add(gameAsset);
                }

                UiThread.Run(() =>
                {
                    UpdateCurrentDLLsFromGameAssets();
                });

                // The old rows are removed here, next to the rows that replace them, rather than
                // before the walk that produces them. Deleting first meant anything that threw in
                // between left the game with no recorded dlls at all - and the history describing
                // what had just happened to them went with it, since that was only inserted at the
                // end. Now a scan that fails leaves the previous rows in place, which are stale
                // rather than absent, and HasUnrecordedDlls brings the game back for another look.
                //
                // Both statements are unconditional. They used to sit inside "did we find any
                // dlls", so a game whose last dll was removed by a patch kept its old rows forever
                // and lost the history saying they had gone.
                using (await Database.Instance.Mutex.LockAsync())
                {
                    await Database.Instance.Connection.ExecuteAsync("DELETE FROM game_asset WHERE id = ?", ID).ConfigureAwait(false);

                    if (dllHistory.Count > 0)
                    {
                        await Database.Instance.Connection.InsertAllAsync(dllHistory, false).ConfigureAwait(false);
                    }

                    if (GameAssets.Count > 0)
                    {
                        await Database.Instance.Connection.InsertAllAsync(GameAssets, false).ConfigureAwait(false);
                    }
                }

                if (GameAssets.Any())
                {
                    newHasSwappableItems = true;

                    if (unknownGameAssets.Any())
                    {
                        GameManager.Instance.AddUnknownGameAssets(GameLibrary, Title, unknownGameAssets);
                    }
                }

                if (coverImageTask is not null)
                {
                    await coverImageTask;

                    RecordWhetherACoverWasFound();
                }

                // The walk finished. Stamped even when it found nothing, which is the whole point of
                // having it: see the remark on LastScannedAt.
                LastScannedAt = DateTime.UtcNow;
            }
            catch (Exception err)
            {
                Logger.Error(err);

                // Put back what the scan emptied before it began. A game whose drive was unplugged
                // part way through was left with no assets and a timestamp from a previous scan,
                // which reads as "this game has no upscalers in it" - a confident statement, about a
                // game that plainly ships one. Restoring the last known list keeps the row honest
                // until HasUnrecordedDlls brings the game back for another look.
                if (GameAssets.Count == 0 && oldGameAssets.Count > 0)
                {
                    GameAssets.AddRange(oldGameAssets);

                    // The flag has to agree with the assets just put back. It was zeroed when the
                    // scan began and only a completed scan sets it again, so restoring the assets
                    // while leaving it false made a rescued game vanish under "Only show games
                    // with an upscaler" - hidden by the very failure the restore was for. A
                    // backup-type asset has no definition, which is the same rule the scan uses.
                    newHasSwappableItems = oldGameAssets.Any(x => Dlls.DllTypes.ForAssetType(x.AssetType) is not null);
                }

                Debugger.Break();
            }
            finally
            {
                // Now update all the data on the UI therad.
                await UiThread.RunAsync(async () =>
                {
                    HasSwappableItems = newHasSwappableItems;

                    if (autoSave)
                    {
                        await SaveToDatabaseAsync();
                    }

                    Processing = false;
                });
            }
        });
    }

    /// <summary>
    /// Notes whether this game loads NVIDIA's dlls through Streamline, from the dll paths the scan
    /// already enumerated.
    /// </summary>
    internal void RecordStreamline(IEnumerable<string> dllPaths)
    {
        var found = false;
        foreach (var dllPath in dllPaths)
        {
            if (string.Equals(Path.GetFileName(dllPath), "sl.interposer.dll", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                break;
            }
        }

        UsesStreamline = found;
    }

    /// <summary>
    /// Notes which anti-cheat, if any, this game's folder carries, and asks for the acknowledgement
    /// again if one has just appeared.
    /// </summary>
    /// <remarks>
    /// Fed the dll paths the scan already enumerated - EasyAntiCheat_x64.dll and BEClient_x64.dll are
    /// dlls - plus the names of the top-level folders, which is one directory listing. Never throws:
    /// a game that cannot be read is a game with no markers to read.
    /// </remarks>
    internal void RecordAntiCheat(IEnumerable<string> dllPaths)
    {
        try
        {
            var markers = new List<string>();
            foreach (var dllPath in dllPaths)
            {
                markers.Add(Path.GetRelativePath(InstallPath, dllPath));
            }

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(InstallPath))
                {
                    markers.Add(Path.GetFileName(directory));
                }
            }
            catch (Exception)
            {
                // A folder that will not list still had its dlls walked above.
            }

            var detected = AntiCheatMarkers.Detect(markers);

            // Found for the first time after the user already said "I understand" for a game that
            // had none: that answer was given without this information, so it is asked for again.
            if (detected is not null && AntiCheat is null && RiskAcknowledgedAt is not null)
            {
                Logger.Info($"{Title} now carries {detected}; the anti-cheat note will be shown again for it.");
                RiskAcknowledgedAt = null;
            }

            AntiCheat = detected;
        }
        catch (Exception err)
        {
            Logger.Error(err);
        }
    }

    /// <summary>
    /// Whether the install folder holds a dll this game has no record of.
    /// </summary>
    /// <remarks>
    /// A game was only ever processed the first time it was seen, so one that gained dlls later was
    /// never looked at again: DOOM shipped three DLSS dlls in a patch and the app went on offering
    /// only its FSR and XeSS, silently, with nothing on screen suggesting anything was missing.
    /// This is the cheap half of a scan, an enumeration and a set comparison with no hashing or
    /// version reading, so it can run for every game on every launch.
    /// </remarks>
    internal bool HasUnrecordedDlls()
    {
        if (string.IsNullOrWhiteSpace(InstallPath) || Directory.Exists(InstallPath) == false)
        {
            return false;
        }

        try
        {
            var enumerationOptions = new EnumerationOptions();
            enumerationOptions.RecurseSubdirectories = true;
            enumerationOptions.AttributesToSkip |= FileAttributes.ReparsePoint;

            var recorded = new HashSet<string>(
                GameAssets.Select(x => x.Path),
                StringComparer.OrdinalIgnoreCase);

            var dllPaths = Directory.GetFiles(InstallPath, "*.dll", enumerationOptions);
            var foundUnrecorded = false;

            foreach (var dllPath in dllPaths)
            {
                // Only the dlls this app manages. Everything else in a game folder is noise.
                if (DllTypes.ForFileName(Path.GetFileName(dllPath)) is null)
                {
                    continue;
                }

                if (recorded.Contains(dllPath) == false)
                {
                    foundUnrecorded = true;
                    break;
                }
            }

            if (foundUnrecorded)
            {
                // Handed to ProcessGame, which is about to walk this exact tree for this exact
                // reason. Kept only when the answer is yes, so a library of games with nothing to
                // do does not sit on a path list per game for the sake of a scan that never runs.
                _dllPathsFromLastCheck = dllPaths;
            }

            return foundUnrecorded;
        }
        catch (Exception err)
        {
            // A folder that cannot be read is not a reason to fail a launch. The game keeps
            // whatever it already had recorded.
            Logger.Warning($"Could not check {Title} for unrecorded dlls: {err.Message}");
        }

        return false;
    }

    /// <summary>
    /// The dll paths <see cref="HasUnrecordedDlls"/> just saw, if it saw any.
    /// </summary>
    /// <remarks>
    /// The guard walks the whole install folder and then, when it says yes, ProcessGame walked the
    /// identical tree again a moment later. Passing the first walk's result across saves the second
    /// for every game that actually changed.
    /// </remarks>
    string[]? _dllPathsFromLastCheck;

    /// <summary>
    /// Takes the handed-over paths, once.
    /// </summary>
    /// <remarks>
    /// Cleared as it is read, so a later scan of the same game cannot be answered with what its
    /// folder held some time ago. A scan with nothing handed to it walks the folder itself, which
    /// is what happens for a game reprocessed for any other reason.
    /// </remarks>
    string[]? TakeDllPathsFromLastCheck()
    {
        var dllPaths = _dllPathsFromLastCheck;
        _dllPathsFromLastCheck = null;

        return dllPaths;
    }


    /// <summary>Whether a game with no recorded dlls is worth walking again.</summary>
    bool HasNotBeenScannedRecently()
    {
        if (LastScannedAt is null)
        {
            return true;
        }

        var age = DateTime.UtcNow - LastScannedAt.Value;

        // A clock that has gone backwards - a timezone change, a corrected system time - would
        // otherwise park a game on the far side of the interval indefinitely.
        return age.TotalDays >= FullRescanIntervalDays || age < TimeSpan.Zero;
    }

    public bool IsInIgnoredPath()
    {
        // If there are no ignored paths we can skip this altogether.
        if (Settings.Instance.IgnoredPaths.Length == 0)
        {
            return false;
        }

        // If installed path is empty we should consider it ignored.
        if (string.IsNullOrWhiteSpace(InstallPath))
        {
            return true;
        }

        foreach (var ignoredPath in Settings.Instance.IgnoredPaths)
        {
            // Because we make IgnoredPaths have a / on the end it will fail the below check.
            // In the cases where the path could be off by one we will do a manual check.
            if (ignoredPath.Length - 1 == InstallPath.Length)
            {
                var tempInstallPath = InstallPath + Path.DirectorySeparatorChar;
                if (tempInstallPath.Equals(ignoredPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }


            if (InstallPath.StartsWith(ignoredPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
