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

namespace DLSS_Swapper.Data;

public abstract partial class Game : ObservableObject, IComparable<Game>, IEquatable<Game> //, INotifyPropertyChanged
{
    [PrimaryKey]
    [Column("id")]
    public string ID { get; set; } = string.Empty;

    [Column("platform_id")]
    public string PlatformId { get; set; } = string.Empty;

    [ObservableProperty]
    [Column("title")]
    public partial string Title { get; set; } = string.Empty;

    // Used to cache the title as a base64 string
    string? _titleBase64;
    [Ignore]
    public string TitleBase64 => _titleBase64 ??= Convert.ToBase64String(Encoding.UTF8.GetBytes(Title));

    [Column("install_path")]
    public string InstallPath { get; set; } = string.Empty;

    [ObservableProperty]
    [Column("cover_image")]
    public partial string? CoverImage { get; set; } = null;

    [ObservableProperty]
    [Ignore]
    public partial uint? DlssPreset { get; set; }

    [ObservableProperty]
    [Ignore]
    public partial uint? DlssDPreset { get; set; }


    [ObservableProperty]
    [Ignore]
    public partial uint? DlssGPreset { get; set; }

    [Ignore]
    public DriverSettingsProfile? DriverSettingsProfile { get; set; }

    /*
    [ObservableProperty]
    [property: Column("base_dlss_version")]
    string baseDLSSVersion = string.Empty;

    [ObservableProperty]
    [property: Column("current_dlss_version")]
    string currentDLSSVersion = string.Empty;

    [ObservableProperty]
    [property: Column("current_dlss_hash")]
    string currentDLSSHash = string.Empty;

    [ObservableProperty]
    [property: Column("base_dlss_hash")]
    string baseDLSSHash = string.Empty;

    [ObservableProperty]
    [property: Column("has_dlss")]
    bool hasDLSS = false;
    */

    [ObservableProperty]
    [Column("has_swappable_items")]
    public partial bool HasSwappableItems { get; set; } = false;

    [ObservableProperty]
    [Column("notes")]
    public partial string Notes { get; set; } = string.Empty;

    [ObservableProperty]
    [Column("is_favourite")]
    public partial bool IsFavourite { get; set; } = false;

    /// <summary>
    /// If the game is hidden from the main list or not. All hidden games are still processed.
    /// If the value is null the user has not set the value and this should be considered as not hidden.
    /// </summary>
    [ObservableProperty]
    [Column("is_hidden")]
    public partial bool? IsHidden { get; set; } = null;

    /// <summary>
    /// When true this game is left alone by every bulk update.
    /// </summary>
    /// <remarks>
    /// For games where a swapped dll causes a problem rather than fixes one: anti cheat in
    /// multiplayer titles can flag a modified dll and refuse to launch, and some games simply
    /// misbehave on a newer version. Without this the only way to keep such a game safe is to never
    /// use "update all", which gives up the feature for the whole library to protect one game.
    /// </remarks>
    [ObservableProperty]
    [Column("skip_updates")]
    public partial bool SkipUpdates { get; set; } = false;

    partial void OnSkipUpdatesChanged(bool value)
    {
        // The row stops offering an update and starts saying why, so the sentence has to change
        // with it.
        RefreshRowStatus();
    }

    [ObservableProperty]
    [Ignore]
    public partial bool Processing { get; set; } = false;

    partial void OnProcessingChanged(bool value)
    {
        // The row sentence changes to and from "Swapping…" with this, so it has to be recomputed
        // rather than only refreshed when versions change.
        RefreshRowStatus();
    }

    /// <summary>
    /// What this game's row says, as a sentence rather than a version delta.
    /// </summary>
    /// <remarks>
    /// Held as a property rather than computed in the binding so it updates once per change instead
    /// of on every layout pass, and so the view has nothing to decide.
    /// </remarks>
    [ObservableProperty]
    [Ignore]
    public partial GameRowStatus? RowStatus { get; set; }

    internal void RefreshRowStatus()
    {
        UiThread.Run(() =>
        {
            RowStatus = GameRowStatus.For(this);
        });
    }

    [Ignore]
    public abstract GameLibrary GameLibrary { get; }

    /// <summary>
    /// The cover art is drawn at 200x300, and these are what is kept on disk for it.
    /// </summary>
    /// <remarks>
    /// A store's art is kept at twice the drawn size and one chosen by hand at three times it: a
    /// downloaded cover is one of hundreds and is replaceable, while a chosen one was somebody's
    /// decision and is the one likely to be looked at closely.
    /// </remarks>
    const int CoverDrawnWidth = 200;

    const int CoverDrawnHeight = 300;

    const int StoreCoverScale = 2;

    const int CustomCoverScale = 3;

    /// <summary>
    /// Where a store's cover art is kept.
    /// </summary>
    /// <remarks>
    /// The 400_600 in the name is the size this one is stored at. The custom one below carries the
    /// same suffix and is stored at 600x900, which is simply wrong and is left alone deliberately:
    /// the name is how an already downloaded cover is found, so changing it orphans every cover
    /// anyone has ever chosen. Read the constants above for the sizes, not the filenames.
    /// </remarks>
    [Ignore]
    public string ExpectedCoverImage => Path.Combine(Storage.GetImageCachePath(), $"{ID}_400_600.png");

    /// <summary>Where a cover chosen by hand is kept. See <see cref="ExpectedCoverImage"/> on the name.</summary>
    [Ignore]
    public string ExpectedCustomCoverImage => Path.Combine(Storage.GetImageCachePath(), $"{ID}_custom_400_600.png");

    /// <summary>
    /// Remembers that a cover could not be fetched, so that failing is as good a reason to wait as
    /// succeeding is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An empty file whose timestamp is the only thing read, which lets a failure go through the
    /// same seven day backoff a downloaded cover gets - see <see cref="ProcessGame"/>.
    /// </para>
    /// <para>
    /// Without it a game whose cover cannot be fetched retries on every launch forever, because the
    /// backoff was keyed on the cover file existing and a failure is precisely the case where no
    /// file was produced. Measured on a real library: two Steam runtimes, four requests every
    /// launch - an IStoreBrowseService call and a CDN request each - all four 404, every time,
    /// for as long as the app is installed.
    /// </para>
    /// </remarks>
    [Ignore]
    public string ExpectedCoverImageUnavailableMarker => Path.Combine(Storage.GetImageCachePath(), $"{ID}_400_600.unavailable");

    /// <summary>
    /// How long a cover lookup's answer is trusted for, found or not found.
    /// </summary>
    /// <remarks>
    /// One number for both, so a game with a cover and a game without one are refreshed on the same
    /// schedule rather than one waiting a week and the other asking on every launch.
    /// </remarks>
    const double CoverLookupRetryDays = 7;

    [Ignore]
    public List<GameAsset> GameAssets { get; } = new List<GameAsset>();

    [Ignore]
    public bool NeedsProcessing { get; set; } = false;

    /// <summary>
    /// When this game's install folder was last walked in full.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Records that a scan happened rather than only what it found. <see cref="ProcessGame"/> writes
    /// game_asset rows only when it finds something, so a game with no DLSS, FSR or XeSS dll has
    /// zero rows for ever - and the cache read could not tell that apart from "never scanned", so it
    /// forced a full rescan of that game on every launch. In most libraries that is the majority of
    /// the games, and each one paid a DELETE, a cover freshness check and a recursive walk of its
    /// install folder to find out again that there was nothing there.
    /// </para>
    /// <para>
    /// Nothing is given up by trusting it. <see cref="HasUnrecordedDlls"/> still runs for every game
    /// on every launch, and noticing a dll that appeared later is the case this rescan was actually
    /// guarding - the rescan was just doing it the expensive way, twice.
    /// </para>
    /// </remarks>
    [Column("last_scanned_at")]
    public DateTime? LastScannedAt { get; set; } = null;

    /// <summary>
    /// How long a "there is nothing in this game" answer is trusted for.
    /// </summary>
    /// <remarks>
    /// A backstop rather than the real guard, which is <see cref="HasUnrecordedDlls"/>. It exists so
    /// that anything that check cannot see - a folder that was unreadable at the time, a dll type
    /// added to the app since - is picked up eventually rather than never.
    /// </remarks>
    const double FullRescanIntervalDays = 7;

    bool _isLoadingCoverImage;

    /// <summary>
    /// Convenience accessor for the game grid, which deliberately shows only DLSS.
    /// </summary>
    [Ignore]
    public GameAssetSlot? DlssSlot => GetAssetSlot(GameAssetType.DLSS);

    /// <summary>True when any dll installed in this game has a newer version available to swap to.</summary>
    [ObservableProperty]
    [Ignore]
    public partial bool UpdateAvailable { get; set; } = false;

    /// <summary>One entry per vendor that has an out of date dll in this game. Empty when nothing is.</summary>
    [ObservableProperty]
    [Ignore]
    public partial List<DllVendorUpdate> AvailableUpdates { get; set; } = new List<DllVendorUpdate>();

    /// <summary>
    /// The dll types with a newer version available, which is what "update all" acts on.
    /// </summary>
    /// <remarks>Kept alongside the badges so both come from the same pass.</remarks>
    [ObservableProperty]
    [Ignore]
    public partial IReadOnlyList<GameAssetType> OutdatedAssetTypes { get; set; } = [];

    /// <summary>
    /// The dll types with a newer version available, pinned or not.
    /// </summary>
    /// <remarks>
    /// What the row sentences read, so a pinned row can still say a newer version exists rather
    /// than claiming it is current. Bulk actions read <see cref="OutdatedAssetTypes"/>, which is
    /// this minus the pins.
    /// </remarks>
    [ObservableProperty]
    [Ignore]
    public partial IReadOnlyList<GameAssetType> BehindAssetTypes { get; set; } = [];

    /// <summary>
    /// What this game has installed for each swappable dll type.
    /// </summary>
    /// <remarks>
    /// One slot per type, created once and never replaced, so anything bound to a slot stays bound
    /// for the life of the game.
    /// </remarks>
    readonly List<GameAssetSlot> _assetSlots = DllTypes.All
        .Select(x => new GameAssetSlot() { AssetType = x.AssetType })
        .ToList();

    [Ignore]
    public IReadOnlyList<GameAssetSlot> AssetSlots => _assetSlots;

    /// <summary>The slot for an asset type, or null if it is not a swappable one.</summary>
    public GameAssetSlot? GetAssetSlot(GameAssetType assetType)
    {
        return _assetSlots.FirstOrDefault(x => x.AssetType == assetType);
    }

    // In their own table and loaded by GameManager when the library loads, not columns here: a
    // pin has to outlive the scans that delete and rewrite this game's asset rows.
    List<GameDllPin> _dllPins = new List<GameDllPin>();

    [Ignore]
    public IReadOnlyList<GameDllPin> DllPins => _dllPins;

    /// <summary>Whether this dll is held where it is. A pinned dll is refused by every batch.</summary>
    public bool IsDllPinned(GameAssetType assetType)
    {
        return _dllPins.Any(x => x.AssetType == assetType);
    }

    public GameDllPin? DllPinFor(GameAssetType assetType)
    {
        return _dllPins.FirstOrDefault(x => x.AssetType == assetType);
    }

    /// <summary>Hands this game its pins. Internal so tests can arrange them without a database.</summary>
    internal void SetDllPins(IEnumerable<GameDllPin> dllPins)
    {
        _dllPins = dllPins.ToList();

        // Pins decide what OutdatedAssetTypes leaves out, and these can arrive after the assets.
        RefreshUpdateAvailable();
    }

    /// <summary>
    /// Holds one dll where it is, with the user's own reason.
    /// </summary>
    /// <remarks>
    /// One pin per dll type: pinning again replaces the reason rather than stacking a second row,
    /// so what the row shows is always the sentence most recently written.
    /// </remarks>
    public async Task PinDllAsync(GameAssetType assetType, string reason)
    {
        var pin = new GameDllPin()
        {
            GameId = ID,
            AssetType = assetType,
            Reason = reason.Trim(),
            PinnedAt = DateTime.Now,
        };

        using (await Database.Instance.Mutex.LockAsync())
        {
            await Database.Instance.Connection.ExecuteAsync(
                "DELETE FROM game_dll_pin WHERE game_id = ? AND asset_type = ?", ID, (int)assetType).ConfigureAwait(false);
            await Database.Instance.Connection.InsertAsync(pin).ConfigureAwait(false);
        }

        _dllPins.RemoveAll(x => x.AssetType == assetType);
        _dllPins.Add(pin);

        RefreshUpdateAvailable();
    }

    public async Task UnpinDllAsync(GameAssetType assetType)
    {
        using (await Database.Instance.Mutex.LockAsync())
        {
            await Database.Instance.Connection.ExecuteAsync(
                "DELETE FROM game_dll_pin WHERE game_id = ? AND asset_type = ?", ID, (int)assetType).ConfigureAwait(false);
        }

        _dllPins.RemoveAll(x => x.AssetType == assetType);

        RefreshUpdateAvailable();
    }



    [Ignore]
    public abstract bool IsReadyToPlay { get; }

    protected void SetID()
    {
        // Seeing as we use ID, it sure would be a shame if a PlatformId was set to "C:\Program Files\"
        // So try to remove all funky characters before

        var platformId = PlatformId;
        foreach (var invalidPathChar in PathHelpers.InvalidFileNamePathChars)
        {
            if (platformId.Contains(invalidPathChar))
            {
                platformId = platformId.Replace(invalidPathChar, '_');
            }
        }

        ID = GameLibrary switch
        {
            GameLibrary.Steam => $"steam_{platformId}",
            GameLibrary.GOG => $"gog_{platformId}",
            GameLibrary.EpicGamesStore => $"epicgamesstore_{platformId}",
            GameLibrary.UbisoftConnect => $"ubisoftconnect_{platformId}",
            GameLibrary.XboxApp => $"xboxapp_{platformId}",
            GameLibrary.ManuallyAdded => $"manuallyadded_{platformId}",
            GameLibrary.BattleNet => $"battlenet_{platformId}",
            GameLibrary.EAApp => $"eaapp_{platformId}",
            _ => throw new Exception($"Unknown GameLibrary {GameLibrary} while setting ID"),
        };
    }

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
                    }
                    else
                    {
                        gameAsset.LoadHash();
                    }

                    if (oldGameAsset is not null) // DLL existed previously
                    {
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
    /// Keeps a copy of a dll the first time we ever see the game it belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only safe on first sight. On any later scan we cannot tell a dll the game shipped from one
    /// that was swapped in, so backing up then could record a swapped dll as the original and make
    /// "reset to default" restore the wrong thing.
    /// </para>
    /// <para>
    /// Never overwrites an existing backup, so a game that already has one keeps it.
    /// </para>
    /// </remarks>
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
            }
            else
            {
                gameAssetBackup.LoadHash();
            }

            GameAssets.Add(gameAssetBackup);
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

    /// <summary>
    /// Whether a cached cover is worth using.
    /// </summary>
    /// <remarks>
    /// An empty file counts as no cover. A save that fails partway leaves a zero byte png behind,
    /// which renders as nothing, and because the file exists the game never tries to fetch it
    /// again. Treating it as absent makes that self correcting.
    /// </remarks>
    static bool HasUsableCover(string path)
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length > 0;
    }

    /// <summary>
    /// Points a stored cover path at the image cache the app is using now.
    /// </summary>
    /// <returns><see langword="true"/> if the stored path changed and the row needs saving.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="CoverImage"/> is stored absolute, so renaming the data folder - which 3.0.0.0 did,
    /// moving it from "DLSS Swapper" to "Swapshelf" - left every stored path naming a folder that no
    /// longer exists. The art itself moved with the folder and was never lost; only the note of where
    /// it lives went stale, and a cover that cannot be found reads as a game with no cover at all.
    /// </para>
    /// <para>
    /// The repair re-derives the path from <see cref="Storage.GetImageCachePath"/> rather than
    /// rewriting the old folder's name out of the stored string. That costs nothing and covers the
    /// cases a find-and-replace would not: a later rename, a library copied between machines, and a
    /// portable build whose cache sits beside the executable.
    /// </para>
    /// <para>
    /// A custom cover wins over a downloaded one, matching <see cref="LoadCoverImageAsync"/>, so a
    /// repaired row lands on the same file the app would have picked. When neither file is there the
    /// path is cleared, which is the honest answer and lets the normal fetch run.
    /// </para>
    /// </remarks>
    internal bool RepairCoverImagePath()
    {
        var stored = CoverImage;

        if (string.IsNullOrEmpty(stored))
        {
            return false;
        }

        // Already inside the folder in use, so there is nothing to point anywhere else.
        var imageCache = Storage.GetImageCachePath();
        if (stored.StartsWith(imageCache, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? repaired = null;
        if (HasUsableCover(ExpectedCustomCoverImage))
        {
            repaired = ExpectedCustomCoverImage;
        }
        else if (HasUsableCover(ExpectedCoverImage))
        {
            repaired = ExpectedCoverImage;
        }

        if (string.Equals(repaired, stored, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        CoverImage = repaired;
        return true;
    }

    /// <summary>
    /// Writes down whether the cover fetch that just ran produced anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The marker is what lets a failure wait. Its contents are never read - only the timestamp is,
    /// by the backoff in <see cref="ProcessGame"/> - so it is written empty and rewritten each
    /// time the attempt fails again, which is what moves the clock forward.
    /// </para>
    /// <para>
    /// A cover that did arrive clears the marker, so a game whose art appears later - a store
    /// backfilling it, or the user adding a custom one - is not held back by a note about a
    /// failure that no longer describes it.
    /// </para>
    /// <para>
    /// Failing to write the marker is not worth interrupting anything for. The cost is one retry
    /// next launch, which is what happened every launch before it existed.
    /// </para>
    /// </remarks>
    void RecordWhetherACoverWasFound()
    {
        try
        {
            if (HasUsableCover(ExpectedCoverImage))
            {
                if (File.Exists(ExpectedCoverImageUnavailableMarker))
                {
                    File.Delete(ExpectedCoverImageUnavailableMarker);
                }

                return;
            }

            File.WriteAllBytes(ExpectedCoverImageUnavailableMarker, Array.Empty<byte>());
        }
        catch (Exception err)
        {
            Logger.Error(err, $"Could not record the cover lookup outcome for {Title}.");
        }
    }

    public async Task LoadCoverImageAsync()
    {
        if (_isLoadingCoverImage == true)
        {
            return;
        }

        _isLoadingCoverImage = true;

        // try/finally, because nothing awaits this any more: a throw that left the flag true would
        // silently block every future cover load for this game, with nobody watching to notice.
        try
        {

        // TODO: Update if the image last write is > 1 week old or something

        if (HasUsableCover(ExpectedCustomCoverImage))
        {
            // If a custom cover exists use it.
            UiThread.Run(() =>
            {
                CoverImage = ExpectedCustomCoverImage;
            });
        }
        else if (HasUsableCover(ExpectedCoverImage))
        {
            // If a standard cover exists use it.
            UiThread.Run(() =>
            {
                CoverImage = ExpectedCoverImage;
            });
        }
        else if (RecentlyFailedToFindACover())
        {
            // Already asked, recently, and there was nothing to find. This is the second of the two
            // places that fetch a cover - ProcessGame is the other - and until it checked, a
            // game with no cover made its requests twice per launch rather than once, because
            // suppressing one path still left this one asking.
        }
        else
        {
            // If no cover exists use the abstracted method to get the game as expect for this library.
            await UpdateCacheImageAsync();

            RecordWhetherACoverWasFound();
        }

        }
        finally
        {
            _isLoadingCoverImage = false;
        }
    }

    /// <summary>
    /// Whether a cover was looked for recently and was not there.
    /// </summary>
    /// <remarks>
    /// Same window a downloaded cover waits before being refreshed, without the jitter - that
    /// exists to spread a library's refreshes out, and there is nothing to spread here because a
    /// failure costs a request that was never going to return an image.
    /// </remarks>
    bool RecentlyFailedToFindACover()
    {
        var marker = new FileInfo(ExpectedCoverImageUnavailableMarker);

        return marker.Exists && (DateTime.Now - marker.LastWriteTime).TotalDays < CoverLookupRetryDays;
    }

    protected abstract Task UpdateCacheImageAsync();

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

    internal async Task<(bool Success, string Message, bool PromptToRelaunchAsAdmin)> ResetDllAsync(GameAssetType gameAssetType)
    {
        // Restoring the original is the safe direction, but it is still a change to a game the user
        // asked to be left alone. Blocking both means the setting has one meaning rather than two.
        if (SkipUpdates)
        {
            return (false, ResourceHelper.GetString("Game_Swap_UpdatesTurnedOff"), false);
        }

        var backupRecordType = DLLManager.Instance.GetAssetBackupType(gameAssetType);
        var existingBackupRecords = this.GameAssets.Where(x => x.AssetType == backupRecordType).ToList();

        if (existingBackupRecords.Count == 0)
        {
            Logger.Info("No backup records found.");
            return (false, ResourceHelper.GetString("Game_Reset_RepairManually"), false);
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
                return (false, ResourceHelper.GetString("Game_Reset_RepairManually"), false);
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
            return (false, ResourceHelper.GetString("Game_GameRunning_CloseFirst"), false);
        }

        // Each backup goes back only if it still hashes to what was recorded when it was saved. A
        // backup made before hashes were kept has none recorded and is restored without the check -
        // it is still the only original there is.
        var resetResult = new DllSwapExecutor().Reset(restorePairs
            .Select(x => new ResetTarget(x.Current.Path, string.IsNullOrWhiteSpace(x.Backup.Hash) ? null : x.Backup.Hash))
            .ToList());

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
            return (true, ResourceHelper.GetFormattedResourceTemplate("Game_Reset_PartialTemplate", restorePairs.Count, totalCount, unrestorableRecords.Count), false);
        }

        return (true, string.Empty, false);
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

    (bool Success, string Message, bool PromptToRelaunchAsAdmin) DescribeSwapFailure(SwapResult result)
    {
        switch (result.Failure)
        {
            case SwapFailure.SourceMissing:
                return (false, ResourceHelper.GetString("Game_Swap_DownloadedDllNotFound"), false);

            case SwapFailure.NoTargets:
                return (false, ResourceHelper.GetString("Game_Swap_NoDllRecordsToUpdate"), false);

            case SwapFailure.AccessDenied:
                if (Environment.IsPrivilegedProcess is false)
                {
                    return (false, ResourceHelper.GetString("Game_Swap_AccessDeniedAdmin"), true);
                }
                return (false, ResourceHelper.GetString("Game_Swap_AccessDenied"), false);

            case SwapFailure.FileInUse:
                return (false, ResourceHelper.GetString("Game_Swap_FileInUse"), false);

            default:
                return (false, ResourceHelper.GetString("Game_Swap_UnknownError"), false);
        }
    }

    (bool Success, string Message, bool PromptToRelaunchAsAdmin) DescribeResetFailure(SwapResult result)
    {
        switch (result.Failure)
        {
            case SwapFailure.AccessDenied:
                if (Environment.IsPrivilegedProcess is false)
                {
                    return (false, ResourceHelper.GetString("Game_Reset_AccessDeniedAdmin"), true);
                }
                return (false, ResourceHelper.GetString("Game_Reset_RepairManually"), false);

            case SwapFailure.FileInUse:
                return (false, ResourceHelper.GetString("Game_Reset_FileInUse"), false);

            case SwapFailure.BackupTampered:
                return (false, ResourceHelper.GetString("Game_Reset_BackupTampered"), false);

            default:
                return (false, ResourceHelper.GetString("Game_Reset_RepairManually"), false);
        }
    }

    /// <summary>
    /// Attempts to update a DLSS dll in a given game.
    /// </summary>
    /// <param name="dlssRecord"></param>
    /// <returns>Tuple containing a boolean of Success, if this is false there will be an error message in the Message response.</returns>
    internal async Task<(bool Success, string Message, bool PromptToRelaunchAsAdmin)> UpdateDllAsync(DLLRecord dllRecord)
    {
        // Locked means locked, not merely left out of bulk updates. A game excluded because a
        // modified dll gets it flagged by anti cheat is no safer if the swap can still be done by
        // hand, and a promise that only covers one route is worse than no promise.
        if (SkipUpdates)
        {
            return (false, ResourceHelper.GetString("Game_Swap_UpdatesTurnedOff"), false);
        }

        if (dllRecord is null)
        {
            return (false, ResourceHelper.GetString("Game_Swap_DllRecordNotFound"), false);
        }

        if (dllRecord.LocalRecord is null)
        {
            return (false, ResourceHelper.GetString("Game_Swap_LocalDllRecordNotFound"), false);
        }

        if (File.Exists(dllRecord.LocalRecord.ExpectedPath) == false)
        {
            return (false, ResourceHelper.GetString("Game_Swap_DownloadedDllNotFound"), false);
        }

        var existingRecords = this.GameAssets.Where(x => x.AssetType == dllRecord.AssetType).ToList();
        if (existingRecords.Count == 0)
        {
            return (false, ResourceHelper.GetString("Game_Swap_NoDllRecordsToUpdate"), false);
        }

        var backupRecordType = DLLManager.Instance.GetAssetBackupType(dllRecord.AssetType);

        var versionInfo = FileVersionInfo.GetVersionInfo(dllRecord.LocalRecord.ExpectedPath);
        var dllVersion = versionInfo.GetFormattedFileVersion();
        var md5Hash = versionInfo.GetMD5Hash();
        if (dllRecord.MD5Hash != md5Hash)
        {
            return (false, ResourceHelper.GetString("Game_Swap_InvalidHash"), false);
        }


        // Validate new DLL
        if (Settings.Instance.AllowUntrusted == false)
        {
            var isTrusted = WinTrust.VerifyEmbeddedSignature(dllRecord.LocalRecord.ExpectedPath);
            if (isTrusted == false)
            {
                return (false, ResourceHelper.GetString("Game_Swap_UntrustedSignature"), false);
            }
        }

        // A running game holds its dlls open. The executor would notice when the rename failed and
        // roll back cleanly - that safety net stays - but "close the game and try again" is a better
        // answer than a failed swap, and this is where there is still nothing to undo.
        if (IsRunningCheck(InstallPath))
        {
            return (false, ResourceHelper.GetString("Game_GameRunning_CloseFirst"), false);
        }

        // Every location this game keeps the dll in is swapped as one operation. The executor backs up
        // each one that needs it, stages the writes, and puts everything back if any step fails, so a
        // failure here means nothing on disk changed.
        var swapResult = new DllSwapExecutor().Swap(dllRecord.LocalRecord.ExpectedPath, existingRecords.Select(x => x.Path).ToList());

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

        return (true, string.Empty, false);
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

    #region IComparable<Game>
    public int CompareTo(Game? other)
    {
        if (other is null)
        {
            return -1;
        }

        return Title.CompareTo(other.Title);
    }
    #endregion

    /*
    #region INotifyPropertyChanged
    public event PropertyChangedEventHandler? PropertyChanged = null;
    void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    #endregion
    */


    protected async Task ResizeCoverAsync(Stream imageStream)
    {
        // TODO:
        // - find optimal format (eg, is displaying 100 webp images more intense than 100 png images)
        // - load image based on scale
        try
        {
            using (var image = await SixLabors.ImageSharp.Image.LoadAsync(imageStream).ConfigureAwait(false))
            {
                // In future this should be updated to resize to display scale.
                // If the image is smaller than this we are just saving as png.
                var resizeOptions = new ResizeOptions()
                {
                    Size = new Size(CoverDrawnWidth * StoreCoverScale, CoverDrawnHeight * StoreCoverScale),
                    Sampler = KnownResamplers.Lanczos5,
                    Mode = ResizeMode.Min, // If image is smaller it won't be resized up.
                };
                image.Mutate(x => x.Resize(resizeOptions));

                // Written beside the target and moved into place, so a save that fails partway
                // cannot leave a zero byte png where the cover should be. One did, and because the
                // file existed the game treated the cover as cached and never fetched it again.
                var partialPath = ExpectedCoverImage + ".part";
                image.SaveAsPng(partialPath);
                File.Move(partialPath, ExpectedCoverImage, true);
            }

            SetCoverImage(ExpectedCoverImage);
        }
        catch (Exception err)
        {
            Logger.Error(err);
        }
    }


    /// <summary>Reads an image off disk and makes it this game's cover.</summary>
    /// <returns>Whether a cover was written. See <see cref="AddCustomCover(Stream)"/>.</returns>
    public bool AddCustomCover(string imageSource)
    {
        try
        {
            using (var fileStream = File.OpenRead(imageSource))
            {
                return AddCustomCover(fileStream);
            }
        }
        catch (Exception err)
        {
            // Opening it can fail on its own - a file that vanished between the picker and here, or
            // one another program has locked - and that is still "no cover was written".
            Logger.Error(err, $"Could not read {imageSource} as a cover for {Title}.");

            return false;
        }
    }

    /// <summary>
    /// Makes an image this game's cover.
    /// </summary>
    /// <returns>
    /// Whether a cover was actually written. False covers an undecodable or truncated image, a
    /// locked or full disk, and anything else that went wrong.
    /// </returns>
    /// <remarks>
    /// This used to be void and swallow everything, which meant a caller could not tell a written
    /// cover from a failed one - and both callers said so out loud regardless: the picker's last
    /// words were "Cover updated." and the library scan counted the game as done. "Applied 12
    /// covers." could be true of none of them, which is the one thing this app is supposed never
    /// to do.
    /// </remarks>
    public bool AddCustomCover(Stream stream)
    {
        // TODO:
        // - find optimal format (eg, is displaying 100 webp images more intense than 100 png images)
        // - load image based on scale
        try
        {
            using (var image = SixLabors.ImageSharp.Image.Load(stream))
            {
                // In future this should be updated to resize to display scale.
                // If the image is smaller than this we are just saving as png.
                var resizeOptions = new ResizeOptions()
                {
                    Size = new Size(CoverDrawnWidth * CustomCoverScale, CoverDrawnHeight * CustomCoverScale),
                    Sampler = KnownResamplers.Lanczos5,
                    Mode = ResizeMode.Min, // If image is smaller it won't be resized up.
                };
                image.Mutate(x => x.Resize(resizeOptions));

                // Written beside the target and moved into place, for the reason ResizeCoverAsync
                // records: a save that fails partway otherwise leaves a truncated png that is not
                // zero bytes, so it passes HasUsableCover, is preferred over the store's art, and
                // shows a broken cover with no way back except the remove dialog.
                var partialPath = ExpectedCustomCoverImage + ".part";
                image.SaveAsPng(partialPath);
                File.Move(partialPath, ExpectedCustomCoverImage, true);
            }

            SetCoverImage(ExpectedCustomCoverImage);

            return true;
        }
        catch (Exception err)
        {
            Logger.Error(err);

            return false;
        }
    }

    /// <summary>
    /// Points the UI at a cover file, in a way the UI will actually notice.
    /// </summary>
    /// <remarks>
    /// <see cref="CoverImage"/> is an <c>[ObservableProperty]</c> and both cover paths are derived
    /// from <see cref="ID"/> alone, so writing the same path a second time - which is exactly what
    /// replacing a custom cover does - is swallowed by the generated setter's equality check, and
    /// the old image stays on screen. Clearing it first is what makes the change visible.
    ///
    /// Every writer goes through here rather than each remembering to do that. Three of them did
    /// not: drag and drop, the SteamGridDB picker and the library scan all set a cover that only
    /// appeared after the page was reopened.
    /// </remarks>
    void SetCoverImage(string path)
    {
        UiThread.Run(() =>
        {
            CoverImage = null;
            CoverImage = path;
        });
    }

    protected async Task<bool> DownloadCoverAsync(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            Logger.Error($"Tried to download cover image but url was null or empty. Game: {Title}, Library: {GameLibrary}");
            return false;
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == false &&
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == false)
        {
            Logger.Error($"Tried to download cover image but url was not valid. Game: {Title}, Library: {GameLibrary}, Url: {url}");
            return false;
        }


        var extension = Path.GetExtension(url);

        // Path.GetExtension retains query arguments, so ths will remove them if they exist.
        if (extension.Contains('?'))
        {
            extension = extension.Substring(0, extension.IndexOf("?"));
        }
        var tempFile = Path.Combine(Storage.GetTemp(), $"{ID}{extension}");


        try
        {
            using (var memoryStream = new MemoryStream())
            {
                var fileDownloader = new FileDownloader(url, 0);
                await fileDownloader.DownloadFileToStreamAsync(memoryStream).ConfigureAwait(false);
                memoryStream.Position = 0;

                // Now if the image is downloaded lets resize it,
                await ResizeCoverAsync(memoryStream).ConfigureAwait(false);
            }
            return true;
        }
        catch (Exception err)
        {
            Logger.Error(err, $"For url: {url}");
            //Debugger.Break();
            return false;
        }
        finally
        {
            // Cleanup temp file.
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    /// <summary>
    /// What the columns of this row looked like the last time it was known to match the database.
    /// </summary>
    /// <remarks>
    /// A field rather than a property, so sqlite-net does not try to store it.
    /// </remarks>
    string? _savedRowSignature;

    /// <summary>The mappings, which are a reflection walk each, so once per type.</summary>
    static readonly ConcurrentDictionary<Type, TableMapping> _mappings = new ConcurrentDictionary<Type, TableMapping>();

    /// <summary>
    /// Every stored column of this row, as one string to compare against.
    /// </summary>
    /// <remarks>
    /// Read through sqlite-net's own mapping rather than a hand written list of properties. The
    /// mapping is what decides which columns get written, so this cannot fall out of step with it -
    /// a column added to this class, or to one of the per platform subclasses, is included without
    /// anyone remembering to come back here.
    /// </remarks>
    string BuildRowSignature()
    {
        var mapping = _mappings.GetOrAdd(GetType(), type => Database.Instance.Connection.GetConnection().GetMapping(type));

        var builder = new StringBuilder();

        foreach (var column in mapping.Columns)
        {
            // Unit separator, so a value containing the separator cannot make two different rows
            // look alike.
            builder.Append(column.Name).Append('=').Append(Stringify(column.GetValue(this))).Append(UnitSeparator);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The separator between one column and the next, spelled rather than typed.
    /// </summary>
    /// <remarks>
    /// This was a raw control character sitting in the source, which is invisible in every editor
    /// and survives a copy and paste only by luck.
    /// </remarks>
    const char UnitSeparator = '\u001f';

    /// <summary>
    /// One value, written the same way every time.
    /// </summary>
    /// <remarks>
    /// StringBuilder.Append(object) calls ToString() with the current culture and the type's default
    /// format, and neither is good enough to compare two runs by. A DateTime's default format has no
    /// sub-second part at all, so two LastScannedAt values inside the same second read as identical;
    /// and this app changes language while it is running, which changes the current culture
    /// underneath a signature taken before it. Round trip format, invariant culture, so the only
    /// thing that can change the string is the value.
    /// </remarks>
    static string Stringify(object? value)
    {
        return value switch
        {
            null => string.Empty,
            DateTime dateTime => dateTime.ToString("o", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("o", CultureInfo.InvariantCulture),
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    /// <summary>
    /// Says this row is exactly what the database holds, so saving it again would write nothing new.
    /// </summary>
    /// <remarks>
    /// Called on a game the moment it comes out of the cache, before anything has had a chance to
    /// change it. Without this every game's first save of the session always wrote, which is the
    /// whole library on every launch.
    /// </remarks>
    internal void MarkAsMatchingDatabase()
    {
        try
        {
            _savedRowSignature = BuildRowSignature();
        }
        catch (Exception err)
        {
            // Worst case the row is saved when it did not need to be, which is what used to happen
            // to all of them.
            Logger.Error(err);
            _savedRowSignature = null;
        }
    }

    /// <summary>
    /// Writes this game, unless the row is already exactly this.
    /// </summary>
    /// <remarks>
    /// The seven library scanners each save every game they walk past, whether or not anything about
    /// it changed - so a library of a couple of hundred games did a couple of hundred writes on every
    /// launch, all of them replacing a row with itself. They still call this; it just does nothing
    /// when there is nothing to do.
    /// </remarks>
    /// <summary>
    /// Raises the change on the UI thread, wherever it was set from.
    /// </summary>
    /// <remarks>
    /// x:Bind writes straight into a control the moment this is raised, and a control may only be
    /// touched from the thread that made it - so a property set from anywhere else threw
    /// RPC_E_WRONG_THREAD out of the setter, and out of whatever was walking the library at the
    /// time. The library scans run on the thread pool, so the moment a game's title or install path
    /// genuinely changed on disk - a game renamed, or moved to another drive - the scan for that
    /// whole library died on the first one it reached. It could not be seen in normal use, because
    /// an unchanged value raises nothing.
    ///
    /// Here rather than at the seven call sites that assign these. It is the same rule every one of
    /// them needs, and the next one to be written will get it without knowing to ask.
    /// </remarks>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        // Runs inline when this is already the UI thread, so nothing is deferred that need not be.
        if (UiThread.Run(() => base.OnPropertyChanged(e)) != true)
        {
            // No window to marshal through - during startup, or on the way out. Raising it here is
            // no worse than the throw, and the bindings that would object do not exist yet.
            base.OnPropertyChanged(e);
        }
    }

    public async Task SaveToDatabaseAsync()
    {
        try
        {
            string? signature = null;

            try
            {
                signature = BuildRowSignature();

                if (signature == _savedRowSignature)
                {
                    return;
                }
            }
            catch (Exception err)
            {
                // Falls through to the write. Not being able to tell whether a save is needed is a
                // reason to save, not a reason to skip it.
                Logger.Error(err);
            }

            var rowsChanged = -1;
            using (await Database.Instance.Mutex.LockAsync())
            {
                rowsChanged = await Database.Instance.Connection.InsertOrReplaceAsync(this);
                // tODO: Configure await
            }

            if (rowsChanged > 0)
            {
                // Only after the write landed. Recording it before would mean a failed save left the
                // game believing it had been stored, and nothing would try again.
                _savedRowSignature = signature;
            }

            if (rowsChanged == 0)
            {
                // TODO: Fix why this happens occasionally to reandom games.
                // This appears to change to different games in different libraries.
                Logger.Error($"Tried to save game to database but rowsChanged was 0.");
                //Debugger.Break();
            }
        }
        catch (Exception err)
        {
            Logger.Error(err);
            Debugger.Break();
        }
    }

    public async Task DeleteAsync()
    {
        try
        {
            // Sometimes when a game is uninstalled the backup files are not removed, so ensure they are.
            // https://github.com/beeradmoore/dlss-swapper/issues/236

            // Except when the user told us to leave that folder alone.
            //
            // Every library's scan skips a game in an ignored path, and every library then deletes
            // the cached games its scan did not return, on the reasoning that they must have been
            // uninstalled. Ignoring a path therefore ran this on every game underneath it - and this
            // deletes the copies of the dlls those games shipped with. Adding an ignored path
            // destroyed the originals for every game under it, permanently, which is the opposite of
            // what "ignore this folder" asks for.
            //
            // Here rather than at the seven call sites, because it is one rule and the next library
            // to be written should get it without knowing to ask. The rows still go, so the game
            // does leave the app; un-ignoring the path finds the .dlsss files again on the next scan.
            var mayDeleteFiles = IsInIgnoredPath() == false;

            List<GameAsset> gameAssets;
            using (await Database.Instance.Mutex.LockAsync())
            {
                gameAssets = await Database.Instance.Connection.Table<GameAsset>().Where(ga => ga.Id == ID).ToListAsync();
            }
            foreach (var cachedGameAsset in gameAssets)
            {
                // If its a file we made we should attempt to delete it.
                if (mayDeleteFiles && DllTypes.IsBackupAssetType(cachedGameAsset.AssetType))
                {
                    if (File.Exists(cachedGameAsset.Path))
                    {
                        Logger.Info($"Deleting {cachedGameAsset.Path}");
                        try
                        {
                            File.Delete(cachedGameAsset.Path);
                        }
                        catch (Exception err)
                        {
                            Logger.Error(err, $"Could not delete {cachedGameAsset.Path}");
                        }
                    }
                }
            }
            using (await Database.Instance.Mutex.LockAsync())
            {
                await Database.Instance.Connection.Table<GameAsset>().DeleteAsync(ga => ga.Id == ID).ConfigureAwait(false);
            }

            // Delete the thumbnails.
            var thumbnailImages = Directory.GetFiles(Storage.GetImageCachePath(), $"{ID}_*", SearchOption.AllDirectories);
            foreach (var thumbnailImage in thumbnailImages)
            {
                try
                {
                    Logger.Info($"Deleting {thumbnailImage}");
                    File.Delete(thumbnailImage);
                }
                catch (Exception err)
                {
                    Logger.Error(err, $"Could not delete {thumbnailImage}");
                }
            }

            // Delete the game itself.
            using (await Database.Instance.Mutex.LockAsync())
            {
                await Database.Instance.Connection.DeleteAsync(this).ConfigureAwait(false);
            }

            // Remove the game from the list.
            GameManager.Instance.RemoveGame(this);
        }
        catch (Exception err)
        {
            Logger.Error(err);
        }
    }

    public bool Equals(Game? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ID == other.ID)
        {
            return true;
        }

        // Within the same library only. Platform ids are not unique across launchers - Steam app
        // ids, Ubisoft Connect install ids, GOG and EA ids are all bare numbers out of overlapping
        // ranges - so owning a Steam game and a Ubisoft game that happen to share one made the two
        // equal. The list of games is a plain List, so AddGame's Contains then found the first one
        // and handed it back: the second game never entered the library, and the first had its
        // title and install path overwritten with the other's and saved under its own id. Which one
        // won moved between launches, because the libraries are scanned concurrently and added in
        // completion order.
        //
        // ID would be enough on its own - SetID prefixes the platform id with the library, and it
        // is what the database keys on - but this branch is kept, narrowed, rather than removed,
        // because it costs nothing and matching a game that has not been given its id yet is a
        // thing this was presumably written for.
        if (GameLibrary == other.GameLibrary && PlatformId == other.PlatformId)
        {
            return true;
        }

        return false;
    }

    protected bool ParentUpdateFromGame(Game game)
    {
        var didChange = false;

        if (Title != game.Title)
        {
            Title = game.Title;
            didChange = true;
        }

        if (InstallPath != game.InstallPath)
        {
            InstallPath = PathHelpers.NormalizePath(game.InstallPath);
            didChange = true;
        }

        if (CoverImage != game.CoverImage)
        {
            CoverImage = game.CoverImage;
            didChange = true;
        }

        if (HasSwappableItems != game.HasSwappableItems)
        {
            HasSwappableItems = game.HasSwappableItems;
            didChange = true;
        }

        // Compare each installed dll through its slot instead of a chain of named properties.
        // Reference comparison, matching what the properties this replaced did.
        foreach (var assetSlot in _assetSlots)
        {
            var otherAssetSlot = game.GetAssetSlot(assetSlot.AssetType);
            if (assetSlot.CurrentAsset != otherAssetSlot?.CurrentAsset)
            {
                assetSlot.CurrentAsset = otherAssetSlot?.CurrentAsset;
                didChange = true;
            }
        }


        // These two copies have no effect either way, and neither does the absence of a third for
        // DlssGPreset. The presets are not stored by this app at all, they live in the NVIDIA
        // driver profile. These properties are only a cache for the game view, and GameControlModel
        // reads all three back from the driver every time a game is opened. A rescan happens with
        // no game open, so whatever is copied here is overwritten before anything reads it.
        // Left as they are rather than tidied, because changing them cannot fix anything.
        if (DlssPreset != game.DlssPreset)
        {
            DlssPreset = game.DlssPreset;
            didChange = true;
        }

        if (DlssDPreset != game.DlssDPreset)
        {
            DlssDPreset = game.DlssDPreset;
            didChange = true;
        }

        // We don't copy across the following properties as it is assume this object has the latest revisions:
        // - Notes
        // - IsFavourite

        return didChange;
    }

    public abstract bool UpdateFromGame(Game game);

    /// <summary>
    /// Rebuilds the per type slots from the current GameAssets. Internal rather than private so
    /// tests can drive it without going through the database.
    /// </summary>
    internal void UpdateCurrentDLLsFromGameAssets()
    {
        foreach (var assetSlot in _assetSlots)
        {
            var assetsForType = GameAssets.Where(x => x.AssetType == assetSlot.AssetType).ToList();

            assetSlot.MultipleFound = assetsForType.Count > 1;

            // Last one wins, matching the chain of assignments this replaced.
            assetSlot.CurrentAsset = assetsForType.LastOrDefault();
        }

        RefreshUpdateAvailable();
    }

    /// <summary>
    /// The asset types a game can have swapped.
    /// </summary>
    static IEnumerable<GameAssetType> EnumerateSwappableAssetTypes()
    {
        return DllTypes.All.Select(x => x.AssetType);
    }

    /// <summary>
    /// Works out whether any installed dll has a newer version available to swap to.
    /// </summary>
    /// <remarks>
    /// Called whenever the installed dlls change, and again once the manifest loads, because on a
    /// cold start the games are read from cache before we know what versions exist.
    /// </remarks>
    public void RefreshUpdateAvailable()
    {
        // Ranked here, decided in the core library where the rules are tested.
        var latestRankByAssetType = new Dictionary<GameAssetType, ulong>();
        foreach (var assetType in EnumerateSwappableAssetTypes())
        {
            var latestRecord = DLLManager.Instance.GetLatestRecord(assetType);
            if (latestRecord is null)
            {
                continue;
            }

            if (DllVersionRanking.TryGetRank(assetType, latestRecord.InternalName, latestRecord.Version, out var latestRank))
            {
                latestRankByAssetType[assetType] = latestRank;
            }
        }

        var installedDlls = new List<InstalledDll>();
        foreach (var gameAsset in GameAssets)
        {
            // A dll whose version we cannot read is left out rather than guessed at.
            if (TryGetInstalledVersionNumber(gameAsset, gameAsset.AssetType, out var installedRank))
            {
                installedDlls.Add(new InstalledDll(gameAsset.AssetType, installedRank));
            }
        }

        var behindAssetTypes = UpdateAvailability.FindOutdatedTypes(installedDlls, latestRankByAssetType);

        // A pinned dll is deliberately held, so no batch is offered it and no badge nags about
        // it - but the row describing it still needs to know a newer version exists, or "pinned"
        // would read as "current". Two lists, one fact each: what is behind, and what an update
        // run may touch.
        var outdatedAssetTypes = behindAssetTypes.Where(x => IsDllPinned(x) == false).ToList();

        // One badge per vendor rather than per dll, otherwise a game trailing on four Intel dlls
        // would show four identical dots. The tooltip names the specific dlls instead.
        var availableUpdates = outdatedAssetTypes
            .GroupBy(x => DLLManager.Instance.GetAssetVendor(x))
            .Where(x => x.Key != DllVendor.Unknown)
            .OrderBy(x => x.Key)
            .Select(x => new DllVendorUpdate()
            {
                Vendor = x.Key,
                Label = DLLManager.Instance.GetVendorShortName(x.Key),
                ToolTip = ResourceHelper.GetFormattedResourceTemplate("GameGrid_UpdateAvailableTemplate", string.Join(", ", x.Select(y => DLLManager.Instance.GetAssetTypeName(y)))),
            })
            .ToList();

        UiThread.Run(() =>
        {
            OutdatedAssetTypes = outdatedAssetTypes;
            BehindAssetTypes = behindAssetTypes;
            AvailableUpdates = availableUpdates;
            UpdateAvailable = availableUpdates.Count > 0;

            // Last, so the sentence is built from the values just assigned.
            RowStatus = GameRowStatus.For(this);
        });
    }

    /// <summary>
    /// Resolves the installed dll to the manifest's packed version number.
    /// </summary>
    /// <remarks>
    /// Matching on hash is exact, but a dll the game shipped with is often not in the manifest at
    /// all, so we fall back to the version recorded off the file itself.
    /// </remarks>
    static bool TryGetInstalledVersionNumber(GameAsset gameAsset, GameAssetType assetType, out ulong versionNumber)
    {
        if (string.IsNullOrWhiteSpace(gameAsset.Hash) == false)
        {
            var knownRecord = DLLManager.Instance.GetRecords(assetType)?.FirstOrDefault(x => x.MD5Hash == gameAsset.Hash);
            if (knownRecord is not null)
            {
                return DllVersionRanking.TryGetRank(assetType, knownRecord.InternalName, knownRecord.Version, out versionNumber);
            }
        }

        // Not in the manifest, so the game shipped it. DisplayVersion resolves the sdk version for
        // the types that are ranked by it, and is ignored for the rest.
        return DllVersionRanking.TryGetRank(assetType, gameAsset.DisplayVersion, gameAsset.Version, out versionNumber);
    }

    public async Task RemoveGameAssetsFromCacheAsync()
    {
        using (await Database.Instance.Mutex.LockAsync())
        {
            await Database.Instance.Connection.ExecuteAsync("DELETE FROM game_asset WHERE id = ?", ID).ConfigureAwait(false);
        }
    }

    public async Task LoadGameAssetsFromCacheAsync()
    {
        // First, before anything below has touched a stored column. This game came straight out of
        // the database, so right now it is the row - and saying so is what lets the save below skip
        // the games this method leaves alone. See MarkAsMatchingDatabase.
        MarkAsMatchingDatabase();

        // After the signature above, deliberately: the repair is a real change to a stored column,
        // and taking the signature first is what lets the save below notice it. Only a row whose
        // path actually moved is written, so this costs one comparison per game on every launch
        // after the one that fixes it.
        if (RepairCoverImagePath())
        {
            await SaveToDatabaseAsync().ConfigureAwait(false);
        }

        // The cover is presentation, not the row. This await was the one shared gate for every
        // library's cache load, so the game list could not appear until every cover had been read -
        // and a game whose cover was missing held a network fetch inline, in the phase whose whole
        // job is putting cards on screen. The OneWay CoverImage bindings and the placeholder brush
        // are built for filling art in over cards that are already visible. A failure lands in the
        // log rather than aborting the rest of this library's cache load, which the old await also
        // did.
        LoadCoverImageAsync().SafeFireAndForget((err) => Logger.Error(err, $"Cover load failed for {Title}."));

        GameAssets.Clear();

        // Out of the one read GameManager does for the whole library when it can, rather than a
        // query and a lock per game. Null means there is no prefetch - a game loading on its own
        // rather than as part of a cache load - and that game asks for its own.
        var gameAssets = GameManager.Instance.PrefetchedAssetsFor(ID);

        if (gameAssets is null)
        {
            using (await Database.Instance.Mutex.LockAsync())
            {
                gameAssets = await Database.Instance.Connection.Table<GameAsset>().Where(ga => ga.Id == ID).ToListAsync().ConfigureAwait(false);
            }
        }

        if (gameAssets?.Any() == true)
        {
            GameAssets.AddRange(gameAssets);
        }

        UpdateCurrentDLLsFromGameAssets();

        // TODO: Add auto reload by storing last full reload time on game

        if (GameAssets.Any())
        {
            foreach (var gameAsset in GameAssets)
            {
                // Check that each of the game assets exist, after we will check if they are what we expect them to be
                if (File.Exists(gameAsset.Path) == false)
                {
                    NeedsProcessing = true;
                    break;
                }
            }

            if (NeedsProcessing == false)
            {
                var unknownGameAssets = new List<GameAsset>();
                foreach (var gameAsset in GameAssets)
                {
                    if (DLLManager.Instance.IsInKnownGameAsset(gameAsset, this) == false)
                    {
                        unknownGameAssets.Add(gameAsset);
                    }
                }
                if (unknownGameAssets.Any())
                {
                    GameManager.Instance.AddUnknownGameAssets(GameLibrary, Title, unknownGameAssets);
                }

                foreach (var gameAsset in GameAssets)
                {
                    var fileVersionInfo = FileVersionInfo.GetVersionInfo(gameAsset.Path);
                    var freshVersion = fileVersionInfo.GetFormattedFileVersion();

                    if (gameAsset.Version != freshVersion)
                    {
                        NeedsProcessing = true;
                        break;
                    }
                }
            }
        }
        else
        {
            // No recorded dlls, which is either a game that has none - most of a library - or one
            // that has never been looked at. Those were the same state until LastScannedAt existed,
            // and treating both as "never looked at" is what made the cache apply to almost nothing.
            NeedsProcessing = HasNotBeenScannedRecently();
            return;
        }
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
