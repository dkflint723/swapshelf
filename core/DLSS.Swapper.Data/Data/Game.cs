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
    /// When the user last said "I understand" to the anti-cheat note for this game, or null if never.
    /// </summary>
    /// <remarks>
    /// Per game, not per install. The note used to show once, ever, before the first swap of any
    /// game and then never again - so by the time it applied to a multiplayer game with an
    /// anti-cheat it had been dismissed weeks earlier over a single-player one, unread. Cleared by
    /// <see cref="RecordAntiCheat"/> when an anti-cheat is first found, so the question is asked
    /// again with the name in it.
    /// </remarks>
    [Column("risk_acknowledged_at")]
    public DateTime? RiskAcknowledgedAt { get; set; } = null;

    /// <summary>
    /// The anti-cheat system found in this game's folder on the last scan, or null when none was.
    /// </summary>
    /// <remarks>
    /// Advisory. It decides what the note says and whether to ask again, never whether a swap may
    /// happen. A plain column on purpose: sqlite-net materialises a row by setting properties in
    /// declaration order, so logic in a setter would run on load and clear an acknowledgement that
    /// was just read.
    /// </remarks>
    [Column("anti_cheat")]
    public string? AntiCheat { get; set; } = null;

    /// <summary>
    /// Whether the game's folder carries sl.interposer.dll, the entry point of NVIDIA Streamline.
    /// </summary>
    /// <remarks>
    /// A fact for the game page and an input to swap confidence, never a gate. A Streamline game
    /// loads DLSS Frame Generation and Ray Reconstruction through a matched set of plugins, so those
    /// two are less of a drop-in there than the same dlls in a game that calls NGX directly. The
    /// app does not offer Streamline's own plugins for swapping at all.
    /// </remarks>
    [Column("streamline")]
    public bool UsesStreamline { get; set; } = false;

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
}
