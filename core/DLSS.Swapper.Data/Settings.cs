using CommunityToolkit.Mvvm.Messaging;
using DLSS_Swapper.Data;
using DLSS_Swapper.Interfaces;
using System;
using DLSS_Swapper.Pages;
using System.Collections.Generic;
using System.Linq;

namespace DLSS_Swapper;

public class Settings
{
    static Settings? _instance;

    public static Settings Instance => _instance ??= Settings.FromJson();
    //public event EnabledGameLibrariesChangedHandler EnabledGameLibrariesChanged;
    //public delegate Task EnabledGameLibrariesChangedHandler(object sender, EventArgs e);

    // We default this to false to prevent saves firing when loading from json.
    bool _autoSave;

    bool _hasShownWarning;
    public bool HasShownWarning
    {
        get { return _hasShownWarning; }
        set
        {
            if (_hasShownWarning != value)
            {
                _hasShownWarning = value;
                if (_autoSave)
                {
                    SaveJson();
                }
            }
        }
    }

    // How far the undone-swaps notice has been read. Dismissing the bar stores the newest change
    // it showed, so only a swap undone after that brings it back. A boolean would have silenced
    // every future undone swap the first time the bar was closed.
    DateTime _undoneSwapsDismissedAt = DateTime.MinValue;
    public DateTime UndoneSwapsDismissedAt
    {
        get { return _undoneSwapsDismissedAt; }
        set
        {
            if (_undoneSwapsDismissedAt != value)
            {
                _undoneSwapsDismissedAt = value;
                if (_autoSave)
                {
                    SaveJson();
                }
            }
        }
    }

    bool _hideNonDLSSGames;
    public bool HideNonDLSSGames
    {
        get { return _hideNonDLSSGames; }
        set
        {
            if (_hideNonDLSSGames != value)
            {
                _hideNonDLSSGames = value;
                if (_autoSave)
                {
                    SaveJson();
                }
            }
        }
    }


    bool _groupGameLibrariesTogether = true;
    public bool GroupGameLibrariesTogether
    {
        get { return _groupGameLibrariesTogether; }
        set
        {
            if (_groupGameLibrariesTogether != value)
            {
                _groupGameLibrariesTogether = value;
                if (_autoSave)
                {
                    SaveJson();
                }
            }
        }
    }

    AppThemePreference _appTheme = AppThemePreference.Default;
    public AppThemePreference AppTheme
    {
        get { return _appTheme; }
        set
        {
            if (_appTheme != value)
            {
                _appTheme = value;
                if (_autoSave)
                {
                    SaveJson();
                }
            }
        }
    }

    /// <summary>
    /// The accent this settings file starts on, which is an index into AccentPalette.All.
    /// </summary>
    /// <remarks>
    /// Declared here rather than taken from AccentPalette, because the palette is colours and lives
    /// in the app while this file is read with no UI in front of it. AccentPalette.DefaultIndex
    /// forwards to this, so there is still one number.
    /// </remarks>
    /// <summary>
    /// Raised when an accent setting changes, so the app can repaint. Null when there is no app.
    /// </summary>
    /// <remarks>
    /// The settings file is read by things that draw nothing, so it cannot call the accent manager
    /// directly - that lives in the app, with the colours.
    /// </remarks>
    internal static Action? AccentChanged { get; set; }

    internal const int DefaultAccentPreset = 0;

    int _accentPreset = DefaultAccentPreset;
    /// <summary>Index into AccentPalette.All. Out of range values fall back to the default.</summary>
    public int AccentPreset
    {
        get { return _accentPreset; }
        set
        {
            if (_accentPreset != value)
            {
                _accentPreset = value;
                if (_autoSave)
                {
                    SaveJson();
                }
                AccentChanged?.Invoke();
            }
        }
    }

    bool _matchDesktopAccent;
    /// <summary>When on, the Windows personalisation colour overrides the chosen preset. Off by default.</summary>
    public bool MatchDesktopAccent
    {
        get { return _matchDesktopAccent; }
        set
        {
            if (_matchDesktopAccent != value)
            {
                _matchDesktopAccent = value;
                if (_autoSave)
                {
                    SaveJson();
                }
                AccentChanged?.Invoke();
            }
        }
    }

    bool _keepOriginalCopiesInLibrary = true;

    /// <summary>
    /// Whether a saved original is also mirrored into the library folder, where a game update
    /// cannot delete it. On by default; it costs disk, and that is the trade.
    /// </summary>
    public bool KeepOriginalCopiesInLibrary
    {
        get { return _keepOriginalCopiesInLibrary; }
        set
        {
            if (_keepOriginalCopiesInLibrary != value)
            {
                _keepOriginalCopiesInLibrary = value;
                if (_autoSave)
                {
                    SaveJson();
                }
            }
        }
    }

    bool _backupNewGamesAutomatically = true;
    public bool BackupNewGamesAutomatically
    {
        get { return _backupNewGamesAutomatically; }
        set
        {
            if (_backupNewGamesAutomatically != value)
            {
                _backupNewGamesAutomatically = value;
                if (_autoSave)
                {
                    SaveJson();
                }
            }
        }
    }

    bool _allowDebugDlls;
    public bool AllowDebugDlls
    {
        get { return _allowDebugDlls; }
        set
        {
            if (_allowDebugDlls != value)
            {
                _allowDebugDlls = value;
                if (_autoSave)
                {
                    SaveJson();
                    WeakReferenceMessenger.Default.Send(new Messages.DebugDllsVisibilityChangedMessage(_allowDebugDlls));
                }
            }
        }
    }



    bool _allowUntrusted;
    public bool AllowUntrusted
    {
        get { return _allowUntrusted; }
        set
        {
            if (_allowUntrusted != value)
            {
                _allowUntrusted = value;
                if (_autoSave)
                {
                    SaveJson();
                }
            }
        }
    }


    ulong _lastPromptWasForVersion;
    public ulong LastPromptWasForVersion
    {
        get { return _lastPromptWasForVersion; }
        set
        {
            if (_lastPromptWasForVersion != value)
            {
                _lastPromptWasForVersion = value;
                if (_autoSave)
                {
                    SaveJson();
                }
            }
        }
    }


    // Don't forget to change this back to off.
#if DEBUG
    LoggingLevel _loggingLevel = LoggingLevel.Verbose;
#else
    LoggingLevel _loggingLevel = LoggingLevel.Error;
#endif
    public LoggingLevel LoggingLevel
    {
        get { return _loggingLevel; }
        set
        {
            if (_loggingLevel != value)
            {
                _loggingLevel = value;
                if (_autoSave)
                {
                    SaveJson();
                }
            }
        }
    }

    uint _enabledGameLibraries = uint.MaxValue;
    [Obsolete("This property is deprecated. Use GameLibrarySettings array instead.")]
    public uint EnabledGameLibraries
    {
        get { return _enabledGameLibraries; }
        set
        {
            if (_enabledGameLibraries != value)
            {
                _enabledGameLibraries = value;
            }
        }
    }




    bool _dontShowManuallyAddingGamesNotice;
    public bool DontShowManuallyAddingGamesNotice
    {
        get { return _dontShowManuallyAddingGamesNotice; }
        set
        {
            if (_dontShowManuallyAddingGamesNotice != value)
            {
                _dontShowManuallyAddingGamesNotice = value;
                if (_autoSave)
                {
                    SaveJson();
                }
            }
        }
    }

    bool _hasShownAddGameFolderMessage;
    public bool HasShownAddGameFolderMessage
    {
        get { return _hasShownAddGameFolderMessage; }
        set
        {
            if (_hasShownAddGameFolderMessage != value)
            {
                _hasShownAddGameFolderMessage = value;
                if (_autoSave)
                {
                    SaveJson();
                }
            }
        }
    }

    WindowPositionRect _lastWindowSizeAndPosition = new WindowPositionRect();
    public WindowPositionRect LastWindowSizeAndPosition
    {
        get { return _lastWindowSizeAndPosition; }
        set
        {
            if (_lastWindowSizeAndPosition != value)
            {
                _lastWindowSizeAndPosition = value;
                if (_autoSave)
                {
                    SaveJson();
                }
            }
        }
    }

    GameGridViewType _gameGridViewType = GameGridViewType.GridView;
    public GameGridViewType GameGridViewType
    {
        get { return _gameGridViewType; }
        set
        {
            if (_gameGridViewType != value)
            {
                _gameGridViewType = value;
                if (_autoSave)
                {
                    SaveJson();
                }
            }
        }
    }


    int _gridViewItemWidth = 200;
    public int GridViewItemWidth
    {
        get { return _gridViewItemWidth; }
        set
        {
            if (_gridViewItemWidth != value)
            {
                _gridViewItemWidth = value;
                if (_autoSave)
                {
                    SaveJson();
                }
            }
        }
    }

    bool _onlyShowDownloadedDlls;
    public bool OnlyShowDownloadedDlls
    {
        get { return _onlyShowDownloadedDlls; }
        set
        {
            if (_onlyShowDownloadedDlls != value)
            {
                _onlyShowDownloadedDlls = value;
                if (_autoSave)
                {
                    SaveJson();
                }
            }
        }
    }

    string _language = string.Empty;
    public string Language
    {
        get { return _language; }
        set
        {
            if (_language != value)
            {
                _language = value;
                if (_autoSave)
                {
                    SaveJson();
                }
            }
        }
    }

    /// <summary>
    /// The user's own SteamGridDB key, used to search for cover art.
    /// </summary>
    /// <remarks>
    /// Empty until someone chooses to set one, and the feature stays out of the way while it is.
    /// It is per user by necessity rather than by preference: a key shipped inside an open source
    /// client is a key anybody can read out of it, and SteamGridDB issues them to people rather
    /// than to applications. It sits in settings.json in plain text, which is what it is - the key
    /// only reads a public art database, and the file is already the user's own.
    /// </remarks>
    string _steamGridDbApiKey = string.Empty;
    public string SteamGridDbApiKey
    {
        get { return _steamGridDbApiKey; }
        set
        {
            if (_steamGridDbApiKey != value)
            {
                _steamGridDbApiKey = value;
                if (_autoSave)
                {
                    SaveJson();
                }
            }
        }
    }

    string[] _ignoredPaths = Array.Empty<string>();
    public string[] IgnoredPaths
    {
        get { return _ignoredPaths; }
        set
        {
            if (_ignoredPaths != value)
            {
                _ignoredPaths = value;
                if (_autoSave)
                {
                    SaveJson();
                    WeakReferenceMessenger.Default.Send(new Messages.GameLibrariesStateChangedMessage());
                }
            }
        }
    }

    GameLibrarySettings[] _gameLibrarySettings = Array.Empty<GameLibrarySettings>();
    public GameLibrarySettings[] GameLibrarySettings
    {
        get { return _gameLibrarySettings; }
        set
        {
            if (_gameLibrarySettings != value)
            {
                _gameLibrarySettings = value;
                if (_autoSave)
                {
                    SaveJson();
                    WeakReferenceMessenger.Default.Send(new Messages.GameLibrariesOrderChangedMessage());
                }
            }
        }
    }

    string _lastLaunchVersion = string.Empty;
    public string LastLaunchVersion
    {
        get { return _lastLaunchVersion; }
        set
        {
            if (_lastLaunchVersion != value)
            {
                _lastLaunchVersion = value;
                if (_autoSave)
                {
                    SaveJson();
                }
            }
        }
    }

    internal static ProxySettings ProxySettings { get; } = new ProxySettings();

    internal void SaveJson()
    {
        if (CanBeSaved == false)
        {
            return;
        }

        Storage.SaveSettingsJson(this);
    }

    /// <summary>
    /// Whether this session may write settings back to disk.
    /// </summary>
    /// <remarks>
    /// False only when there is a settings file that could not be opened. It may be a perfectly good
    /// file that an antivirus or backup agent had held for a moment, so the session runs on defaults
    /// and writes nothing rather than replacing it - the next launch reads the real one. Every
    /// setter goes through SaveJson, so this is the one place that has to be stopped.
    /// </remarks>
    internal bool CanBeSaved { get; private set; } = true;

    static Settings FromJson()
    {
        var (outcome, loaded) = Storage.LoadSettingsJson();

        var settings = loaded ?? new Settings();

        if (outcome == Storage.SettingsLoadOutcome.Missing)
        {
            // A first run. Writing the defaults over nothing is exactly right.
            settings.SaveJson();
        }
        else if (outcome == Storage.SettingsLoadOutcome.Corrupt)
        {
            // There is a file and it is not settings. Reading it again next launch will not help, so
            // it is kept beside itself and a fresh one written - otherwise the user is stuck on
            // defaults forever with no way to save a change.
            Storage.MoveUnreadableSettingsAside();
            settings.SaveJson();
        }
        else if (outcome == Storage.SettingsLoadOutcome.Unreadable)
        {
            // The file could not be opened, which is very often temporary. It used to be treated the
            // same as no file at all, so a single locked read replaced somebody's api key, ignored
            // paths, library order, language and theme with defaults and saved them over the top.
            settings.CanBeSaved = false;
            Logger.Warning("Running on default settings for this session. The settings file could not be read and has been left alone.");
        }

        var shouldSave = settings.CheckGameLibraries();
        if (shouldSave)
        {
            settings.SaveJson();
        }

        // Re-enable auto save.
        settings._autoSave = true;
        return settings;
    }

    /// <summary>
    /// Checks game libraries to see if there are any new ones to be added, or misconfigured settings.
    /// </summary>
    /// <returns></returns>
    private bool CheckGameLibraries()
    {
        var gameLibraries = Enum.GetValues<GameLibrary>().ToList();

        // Move manually added to the end of the list by default.
        gameLibraries.Remove(GameLibrary.ManuallyAdded);
        gameLibraries.Add(GameLibrary.ManuallyAdded);

        // If no items are in the list we are migrating them all
        // If there are some items in the list we are only checking and adding new libraries
        if (_gameLibrarySettings.Length == 0)
        {
#pragma warning disable CS0618 // Type or member is obsolete
            var enabledGameLibraries = (GameLibrary)EnabledGameLibraries;
#pragma warning restore CS0618 // Type or member is obsolete

            var tempGameLibraries = new List<GameLibrarySettings>(gameLibraries.Count);
            foreach (var gameLibrary in gameLibraries)
            {
                // Enaable libraries based on EnabledGameLibraries property.
                tempGameLibraries.Add(new GameLibrarySettings()
                {
                    GameLibrary = gameLibrary,
                    IsEnabled = enabledGameLibraries.HasFlag(gameLibrary),
                });
            }
            _gameLibrarySettings = tempGameLibraries.ToArray();
            return true;
        }
        else
        {
            // Remove each one of the loaded gameLibraries from the list.
            foreach (var gameLibrarySetting in _gameLibrarySettings)
            {
                gameLibraries.Remove(gameLibrarySetting.GameLibrary);
            }

            // If there are any items it could be a new launch, new library, or misconfigured settings.
            if (gameLibraries.Count > 0)
            {
                var tempGameLibraries = new List<GameLibrarySettings>(_gameLibrarySettings);
                foreach (var gameLibrary in gameLibraries)
                {
                    // Because this is not a full migration new libraries are enabled by default.
                    tempGameLibraries.Add(new GameLibrarySettings()
                    {
                        GameLibrary = gameLibrary,
                        IsEnabled = true,
                    });
                }
                _gameLibrarySettings = tempGameLibraries.ToArray();
                return true;
            }
        }

        return false;
    }
}
