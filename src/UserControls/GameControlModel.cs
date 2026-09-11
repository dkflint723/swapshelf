using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DLSS_Swapper.Data;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using System.Diagnostics;
using System.IO;
using Windows.System;
using DLSS_Swapper.Helpers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DLSS_Swapper.Data.DLSS;
using DLSS_Swapper.Data.SteamGridDb;
using System.ComponentModel;

namespace DLSS_Swapper.UserControls;

public partial class GameControlModel : ObservableObject
{
    WeakReference<Pages.GameDetailPage> gameControlWeakReference;

    public Game Game { get; init; }

    public bool IsManuallyAdded => Game.GameLibrary == Interfaces.GameLibrary.ManuallyAdded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameTitleHasChanged))]
    public partial string GameTitle { get; set; }

    [ObservableProperty]
    public partial PresetOption? SelectedDlssPreset { get; set; }

    [ObservableProperty]
    public partial PresetOption? SelectedDlssDPreset { get; set; }

    [ObservableProperty]
    public partial PresetOption? SelectedDlssGPreset { get; set; }

    public bool CanSelectDlssPreset { get; private set; }

    public bool CanSelectDlssDPreset { get; private set; }

    public bool CanSelectDlssGPreset { get; private set; }

    public List<PresetOption> DlssPresetOptions { get; } = new List<PresetOption>();

    public List<PresetOption> DlssDPresetOptions { get; } = new List<PresetOption>();

    public List<PresetOption> DlssGPresetOptions { get; } = new List<PresetOption>();

    PresetOption? _previousDlssPreset;
    PresetOption? _previousDlssDPreset;
    PresetOption? _previousDlssGPreset;

    public bool GameTitleHasChanged
    {
        get
        {
            if (IsManuallyAdded == false)
            {
                return false;
            }

            if (string.IsNullOrEmpty(GameTitle))
            {
                return false;
            }

            return GameTitle.Equals(Game.Title) == false;
        }
    }

    public GameControlModelTranslationProperties TranslationProperties { get; } = new GameControlModelTranslationProperties();

    /// <summary>
    /// Whether each preset control is shown.
    /// </summary>
    /// <remarks>
    /// A preset belongs to a dll, so it has to disappear with it. Hiding only the pickers left
    /// their partners stranded: a game with no DLSS showed three preset dropdowns reading "Not
    /// supported" beside an empty column. These are computed once here rather than combined in the
    /// binding, because x:Bind cannot express "has the dll and the preset is selectable".
    /// </remarks>
    [ObservableProperty]
    public partial Visibility DlssPresetVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility DlssDPresetVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility DlssGPresetVisibility { get; set; } = Visibility.Collapsed;

    static Visibility VisibleIfPresent(GameEngineSplit engines, GameAssetType assetType)
    {
        return engines.Present.Contains(assetType) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Whether the presets section has any row in it at all.</summary>
    public Visibility AnyPresetVisibility => DlssPresetVisibility == Visibility.Visible
        || DlssDPresetVisibility == Visibility.Visible
        || DlssGPresetVisibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// What a preset is, or which of the three reasons it cannot be set.
    /// </summary>
    /// <remarks>
    /// The three were distinguishable all along — no NVIDIA driver, no driver profile for this
    /// game, a permission problem — and were collapsed into one word, "Not supported", beside a
    /// dropdown that had greyed itself out. An error icon appeared for exactly one of the three.
    /// A disabled control with no reason reads as broken rather than as unavailable.
    /// </remarks>
    public string DlssPresetDescription => PresetAvailability.Describe(
        NVAPIHelper.Instance.IsSupported,
        NVAPIHelper.Instance.PermissionIssue,

        // The driver profile is what CanSelect is set from, a few lines further down the
        // constructor: it is only ever true once FindGameProfile has returned one.
        CanSelectDlssPreset || CanSelectDlssDPreset || CanSelectDlssGPreset);

    /// <summary>
    /// Hides the buttons that change dlls when the game is locked.
    /// </summary>
    /// <remarks>
    /// The swap path refuses anyway, but a button whose only outcome is a refusal is worse than no
    /// button: it invites the click and then explains why it was wrong.
    /// </remarks>
    [ObservableProperty]
    public partial Visibility CanChangeDllsVisibility { get; set; } = Visibility.Visible;

    /// <summary>
    /// Shown only for libraries this app can actually start a game through.
    /// </summary>
    /// <remarks>
    /// The same reasoning as the note above, applied to the button that had been ignoring it:
    /// Launch was always shown, and for a library that cannot be launched from its only possible
    /// outcome was an error dialog saying so. <see cref="GameManager.CanLaunchGame"/> knows the
    /// answer before the click.
    /// </remarks>
    public Visibility CanLaunchVisibility => GameManager.Instance.CanLaunchGame(Game)
        ? Visibility.Visible
        : Visibility.Collapsed;

    /// <summary>
    /// Shown only when there is something to update.
    /// </summary>
    /// <remarks>
    /// It used to be offered whatever the game's state, and on an up-to-date game it did nothing
    /// and said nothing.
    /// </remarks>
    public Visibility HasUpdatesVisibility => CanChangeDllsVisibility == Visibility.Visible
        && Game.OutdatedAssetTypes.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>Reads as "Never update this game", checked when it is set.</summary>
    public bool SkipUpdates => Game.SkipUpdates;

    /// <summary>
    /// The setting that greys out every row on this page, finally reachable from it.
    /// </summary>
    /// <remarks>
    /// It was only ever settable by right-clicking the game on another page, which the lock's own
    /// tooltip had to tell people to go and do.
    /// </remarks>
    [RelayCommand]
    async Task ToggleSkipUpdatesAsync()
    {
        Game.SkipUpdates = Game.SkipUpdates == false;
        await Game.SaveToDatabaseAsync();

        CanChangeDllsVisibility = Game.SkipUpdates ? Visibility.Collapsed : Visibility.Visible;

        OnPropertyChanged(nameof(SkipUpdates));
        OnPropertyChanged(nameof(HasUpdatesVisibility));

        // Every row's sentence says whether the game is behind, and a locked game is not.
        RefreshUpscalerRows();

        // The Hidden and "Have an update" counts are taken from the library, and this changes what
        // one of them contains.
        App.CurrentApp.MainWindow?.GameGridPage?.ViewModel.RefreshFilterTabs();
    }

    /// <summary>Names the upscalers this game does not have, in place of eight empty pickers.</summary>
    [ObservableProperty]
    public partial string NotPresentSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility NotPresentSummaryVisibility { get; set; } = Visibility.Collapsed;

    /// <summary>
    /// One row per upscaler this game actually has.
    /// </summary>
    /// <remarks>
    /// Nine hardcoded controls before this, each deciding for itself whether the game had its dll,
    /// which is <see cref="GameEngines.Split"/>'s question asked a second time. These come from
    /// <see cref="UpscalerRows.For"/>, which produces the rows and the "not in this game" line from
    /// one answer.
    /// </remarks>
    public ObservableCollection<UpscalerRowStatus> UpscalerRowList { get; } = new ObservableCollection<UpscalerRowStatus>();

    /// <summary>
    /// Rebuilds the rows and the line naming what is missing.
    /// </summary>
    /// <remarks>
    /// Called whenever something has written to the game, because every row's sentence is about
    /// what is on disk right now — which version, whether an original was kept, whether there are
    /// two copies. A row built once at construction goes stale the first time a swap succeeds.
    /// </remarks>
    void RefreshUpscalerRows()
    {
        var rows = UpscalerRows.For(Game);

        UpscalerRowList.Clear();
        foreach (var row in rows.Rows)
        {
            UpscalerRowList.Add(row);
        }

        NotPresentSummary = rows.AbsentSummary;

        // The launch-then-restore item exists only while something could be restored, and every
        // caller of this rebuild has just changed what is on disk.
        OnPropertyChanged(nameof(PlayCleanVisibility));

        // A game with no upscalers at all would otherwise get a line listing all nine, which is
        // just a long way of saying the app has nothing to do here.
        NotPresentSummaryVisibility = string.IsNullOrEmpty(rows.AbsentSummary) == false && rows.Rows.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public GameControlModel(Pages.GameDetailPage gameControl, Game game) : base()
    {
        gameControlWeakReference = new WeakReference<Pages.GameDetailPage>(gameControl);
        Game = game;
        GameTitle = game.Title;

        CanChangeDllsVisibility = game.SkipUpdates ? Visibility.Collapsed : Visibility.Visible;

        var engines = GameEngines.Split(game);

        DlssPresetVisibility = VisibleIfPresent(engines, GameAssetType.DLSS);
        DlssDPresetVisibility = VisibleIfPresent(engines, GameAssetType.DLSS_D);
        DlssGPresetVisibility = VisibleIfPresent(engines, GameAssetType.DLSS_G);

        RefreshUpscalerRows();

        // A watch outlives the page that started it, so a page opened mid-session has to pick the
        // strip back up rather than showing nothing about a restore that is still armed.
        if (PlayCleanSession.Current?.Game == game)
        {
            AttachToPlayCleanSession(PlayCleanSession.Current);
        }


        // Make sure NVAPIHelper is supported and the game has DLSS.
        if (NVAPIHelper.Instance.IsSupported && game.GetAssetSlot(GameAssetType.DLSS)?.CurrentAsset is not null)
        {
            // Try load the DriverSettingProfile for the given game. If it is not found the game is not supported.

            var gameProfile = NVAPIHelper.Instance.FindGameProfile(game);
            if (gameProfile is not null)
            {
                var gameDLSSPresetResult = NVAPIHelper.Instance.GetGameDLSSPreset(game);
                if (gameDLSSPresetResult.Success)
                {
                    CanSelectDlssPreset = true;
                    game.DlssPreset = gameDLSSPresetResult.Result;
                    DlssPresetOptions.AddRange(NVAPIHelper.Instance.DlssPresetOptions);
                    if (game.DlssPreset is null)
                    {
                        // If it was never set, ensure it goes to default.
                        SelectedDlssPreset = DlssPresetOptions.FirstOrDefault(x => x.Value == 0);
                    }
                    else
                    {
                        SelectedDlssPreset = DlssPresetOptions.FirstOrDefault(x => x.Value == game.DlssPreset);
                    }


                    if (Game.GetAssetSlot(GameAssetType.DLSS_D)?.CurrentAsset is not null)
                    {
                        var gameDLSSDPresetResult = NVAPIHelper.Instance.GetGameDLSSDPreset(game);
                        if (gameDLSSDPresetResult.Success)
                        {
                            CanSelectDlssDPreset = true;

                            game.DlssDPreset = gameDLSSDPresetResult.Result;
                            DlssDPresetOptions.AddRange(NVAPIHelper.Instance.DlssDPresetOptions);
                            if (game.DlssDPreset is null)
                            {
                                // If it was never set, ensure it goes to default.
                                SelectedDlssDPreset = DlssDPresetOptions.FirstOrDefault(x => x.Value == 0);
                            }
                            else
                            {
                                SelectedDlssDPreset = DlssDPresetOptions.FirstOrDefault(x => x.Value == game.DlssDPreset);
                            }
                        }
                    }


                    if (Game.GetAssetSlot(GameAssetType.DLSS_G)?.CurrentAsset is not null)
                    {
                        var gameDLSSGPresetResult = NVAPIHelper.Instance.GetGameDLSSGPreset(game);
                        if (gameDLSSGPresetResult.Success)
                        {
                            CanSelectDlssGPreset = true;

                            game.DlssGPreset = gameDLSSGPresetResult.Result;
                            DlssGPresetOptions.AddRange(NVAPIHelper.Instance.DlssGPresetOptions);
                            if (game.DlssGPreset is null)
                            {
                                // If it was never set, ensure it goes to default.
                                SelectedDlssGPreset = DlssGPresetOptions.FirstOrDefault(x => x.Value == 0);
                            }
                            else
                            {
                                SelectedDlssGPreset = DlssGPresetOptions.FirstOrDefault(x => x.Value == game.DlssGPreset);
                            }
                        }
                    }
                }
            }
        }

        if (CanSelectDlssPreset == false)
        {
            var disabledPresetOption = new PresetOption(ResourceHelper.GetString("General_NotSupported"), 0);
            DlssPresetOptions.Add(disabledPresetOption);
            SelectedDlssPreset = disabledPresetOption;
        }

        if (CanSelectDlssDPreset == false)
        {
            var disabledPresetOption = new PresetOption(ResourceHelper.GetString("General_NotSupported"), 0);
            DlssDPresetOptions.Add(disabledPresetOption);
            SelectedDlssDPreset = disabledPresetOption;
        }

        if (CanSelectDlssGPreset == false)
        {
            var disabledPresetOption = new PresetOption(ResourceHelper.GetString("General_NotSupported"), 0);
            DlssGPresetOptions.Add(disabledPresetOption);
            SelectedDlssGPreset = disabledPresetOption;
        }
    }

    partial void OnSelectedDlssPresetChanging(PresetOption? value)
    {
        _previousDlssPreset = SelectedDlssPreset;
    }

    partial void OnSelectedDlssDPresetChanging(PresetOption? value)
    {
        _previousDlssDPreset = SelectedDlssDPreset;
    }

    partial void OnSelectedDlssGPresetChanging(PresetOption? value)
    {
        _previousDlssGPreset = SelectedDlssGPreset;
    }


    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName == nameof(SelectedDlssPreset))
        {
            WritePreset(
                CanSelectDlssPreset,
                SelectedDlssPreset,
                Game.DlssPreset,
                value => NVAPIHelper.Instance.SetGameDLSSPreset(Game, value).Success,
                () => SelectedDlssPreset = _previousDlssPreset);
        }
        else if (e.PropertyName == nameof(SelectedDlssDPreset))
        {
            WritePreset(
                CanSelectDlssDPreset,
                SelectedDlssDPreset,
                Game.DlssDPreset,
                value => NVAPIHelper.Instance.SetGameDLSSDPreset(Game, value).Success,
                () => SelectedDlssDPreset = _previousDlssDPreset);
        }
        else if (e.PropertyName == nameof(SelectedDlssGPreset))
        {
            WritePreset(
                CanSelectDlssGPreset,
                SelectedDlssGPreset,
                Game.DlssGPreset,
                value => NVAPIHelper.Instance.SetGameDLSSGPreset(Game, value).Success,
                () => SelectedDlssGPreset = _previousDlssGPreset);
        }
    }

    /// <summary>
    /// Writes one preset to the driver, and puts the dropdown back if the driver refuses.
    /// </summary>
    /// <remarks>
    /// One copy instead of three. The guard, the call, the failure check, the rollback and the
    /// error dialog were written out once per preset kind, which is three chances for one of them
    /// to drift. The guard itself is <see cref="PresetAvailability.ShouldWrite"/>, which is the
    /// part that can be run in a test — everything left here needs a driver and a window.
    ///
    /// The rollback goes through the dispatcher because it is assigning to the property whose
    /// change notification is currently running.
    /// </remarks>
    void WritePreset(bool canSet, PresetOption? selected, uint? currentValue, Func<uint, bool> write, Action rollback)
    {
        if (PresetAvailability.ShouldWrite(canSet, selected?.Value, currentValue) == false)
        {
            return;
        }

        if (write(selected!.Value))
        {
            return;
        }

        if (gameControlWeakReference.TryGetTarget(out Pages.GameDetailPage? gameControl))
        {
            gameControl.DispatcherQueue.TryEnqueue(() => rollback());
            _ = NVAPIHelper.Instance.DisplayNVAPIErrorAsync(gameControl.XamlRoot);
        }
    }

    [RelayCommand]
    async Task OpenInstallPathAsync()
    {
        try
        {
            if (Directory.Exists(Game.InstallPath))
            {
                Process.Start("explorer.exe", Game.InstallPath);
            }
            else
            {
                throw new Exception(ResourceHelper.GetFormattedResourceTemplate("GamePage_CouldNotFindGameInstallPathTemplate", Game.InstallPath));
            }
        }
        catch (Exception err)
        {
            Logger.Error(err);

            if (gameControlWeakReference.TryGetTarget(out Pages.GameDetailPage? gameControl))
            {
                var dialog = new EasyContentDialog(gameControl.XamlRoot)
                {
                    Title = ResourceHelper.GetString("General_Error"),
                    CloseButtonText = ResourceHelper.GetString("General_Okay"),
                    Content = err.Message,
                };
                await dialog.ShowAsync();
            }
        }
    }

    [RelayCommand]
    async Task LaunchAsync()
    {
        if (GameManager.Instance.CanLaunchGame(Game))
        {
            await GameManager.Instance.LaunchGameAsync(Game);
        }
        else
        {
            if (gameControlWeakReference.TryGetTarget(out Pages.GameDetailPage? gameControl))
            {
                var dialog = new EasyContentDialog(gameControl.XamlRoot)
                {
                    Title = ResourceHelper.GetString("General_Error"),
                    CloseButtonText = ResourceHelper.GetString("General_Okay"),
                    DefaultButton = ContentDialogButton.Close,
                    Content = ResourceHelper.GetFormattedResourceTemplate("GamePage_CantLaunchFromLibraryTemplate", Game.GameLibrary),
                };
                await dialog.ShowAsync();
            }
        }
    }

    /// <summary>
    /// Launches the game and puts the originals back the moment it closes.
    /// </summary>
    /// <remarks>
    /// The confirmation names every dll that will go back, through the same preview builder every
    /// revert asks with — the write happens later, but it is agreed to now, so it is shown now.
    /// </remarks>
    [RelayCommand]
    async Task PlayCleanAsync()
    {
        if (gameControlWeakReference.TryGetTarget(out var gameControl) == false)
        {
            return;
        }

        if (PlayCleanSession.Current is not null)
        {
            var busyDialog = new EasyContentDialog(gameControl.XamlRoot)
            {
                Title = ResourceHelper.GetString("GamePage_PlayClean"),
                CloseButtonText = ResourceHelper.GetString("General_Okay"),
                DefaultButton = ContentDialogButton.Close,
                Content = ResourceHelper.GetFormattedResourceTemplate("PlayClean_AlreadyWatchingTemplate", PlayCleanSession.Current.Game.Title),
            };
            await busyDialog.ShowAsync();
            return;
        }

        var preview = DllUpdateRunner.GetRevertPreview(Game);
        if (preview.Count == 0)
        {
            var nothingDialog = new EasyContentDialog(gameControl.XamlRoot)
            {
                Title = ResourceHelper.GetString("GamePage_PlayClean"),
                CloseButtonText = ResourceHelper.GetString("General_Okay"),
                DefaultButton = ContentDialogButton.Close,
                Content = ResourceHelper.GetString("DllRevert_NothingToRevert"),
            };
            await nothingDialog.ShowAsync();
            return;
        }

        var confirmDialog = new EasyContentDialog(gameControl.XamlRoot)
        {
            Title = ResourceHelper.GetString("GamePage_PlayClean"),
            PrimaryButtonText = ResourceHelper.GetString("PlayClean_LaunchButton"),
            CloseButtonText = ResourceHelper.GetString("General_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            Content = DllUpdatePrompt.BuildConfirmContent(
                ResourceHelper.GetFormattedResourceTemplate("PlayClean_ConfirmBodyTemplate", Game.Title),
                preview.Select(x => $"{x.EngineName}: {x.VersionChange}").ToList()),
        };

        if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var session = PlayCleanSession.Start(Game);
        if (session is null)
        {
            return;
        }

        AttachToPlayCleanSession(session);

        await GameManager.Instance.LaunchGameAsync(Game);
    }

    /// <summary>
    /// Keeps this page's strip telling the session's truth, and lets go when the session ends.
    /// </summary>
    /// <remarks>
    /// Also called at construction when a session for this game is already live, because the page
    /// that started a watch can be closed and reopened while it runs. The completed handler
    /// removes itself, so a model that outlives its dialog is not pinned by the static event.
    /// </remarks>
    void AttachToPlayCleanSession(PlayCleanSession session)
    {
        session.PhaseChanged += () => App.CurrentApp.RunOnUIThread(RefreshPlayCleanStrip);

        Action<PlayCleanSession, PlayCleanOutcome, DllUpdateResult?>? completedHandler = null;
        completedHandler = (endedSession, outcome, result) =>
        {
            PlayCleanSession.SessionCompleted -= completedHandler;

            App.CurrentApp.RunOnUIThread(() =>
            {
                RefreshPlayCleanStrip();

                // The restore has just rewritten what is on disk, and every row describes disk.
                RefreshUpscalerRows();
                OnPropertyChanged(nameof(HasUpdatesVisibility));
            });
        };
        PlayCleanSession.SessionCompleted += completedHandler;

        RefreshPlayCleanStrip();
    }

    /// <summary>Offered only when it could both launch and restore something afterwards.</summary>
    public Visibility PlayCleanVisibility => GameManager.Instance.CanLaunchGame(Game)
        && DllUpdateRunner.GetRevertableAssetTypes(Game).Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public bool PlayCleanStripIsOpen => PlayCleanSession.Current?.Game == Game;

    public string PlayCleanStripText
    {
        get
        {
            var session = PlayCleanSession.Current;
            if (session is null || session.Game != Game)
            {
                return string.Empty;
            }

            return session.Phase switch
            {
                PlayCleanPhase.WaitingForStart => ResourceHelper.GetFormattedResourceTemplate("PlayClean_WaitingTemplate", Game.Title),
                PlayCleanPhase.Running => ResourceHelper.GetFormattedResourceTemplate("PlayClean_RunningTemplate", Game.Title),
                _ => ResourceHelper.GetString("Update_Undoing"),
            };
        }
    }

    void RefreshPlayCleanStrip()
    {
        OnPropertyChanged(nameof(PlayCleanStripIsOpen));
        OnPropertyChanged(nameof(PlayCleanStripText));
    }

    [RelayCommand]
    void StopPlayClean()
    {
        PlayCleanSession.Current?.Stop();
    }

    [RelayCommand]
    async Task EditNotesAsync()
    {
        if (gameControlWeakReference.TryGetTarget(out Pages.GameDetailPage? gameControl))
        {
            var textBox = new TextBox()
            {
                MinHeight = 400,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
            };
            // This needs to be set after AcceptsReturn otherwise it will strip out the \r
            textBox.Text = Game.Notes;

            var dialog = new EasyContentDialog(gameControl.XamlRoot)
            {
                Title = $"{ResourceHelper.GetString("GamePage_Notes")} - {Game.Title}",
                PrimaryButtonText = ResourceHelper.GetString("General_Save"),
                CloseButtonText = ResourceHelper.GetString("General_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                Content = textBox,
            };
            dialog.Resources["ContentDialogMinWidth"] = 700;
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                Game.Notes = textBox.Text ?? string.Empty;
                await Game.SaveToDatabaseAsync();
            }
        }
    }

    [RelayCommand]
    async Task ViewHistoryAsync()
    {
        if (gameControlWeakReference.TryGetTarget(out var control))
        {
            var dialog = new EasyContentDialog(control.XamlRoot)
            {
                Title = $"{ResourceHelper.GetFormattedResourceTemplate("GamePage_History")} - {Game.Title}",
                PrimaryButtonText = ResourceHelper.GetString("General_Close"),
                DefaultButton = ContentDialogButton.Primary,
                Content = new GameHistoryControl(Game),
            };
            dialog.Resources["ContentDialogMinWidth"] = 800;

            await dialog.ShowAsync();
        }
    }

    [RelayCommand]
    async Task AddCoverImageAsync()
    {
        if (Game.CoverImage == Game.ExpectedCustomCoverImage)
        {
            await Game.PromptToRemoveCustomCover();
            return;
        }

        Game.PromptToBrowseCustomCover();
    }

    /// <summary>
    /// Searches SteamGridDB for a replacement cover.
    /// </summary>
    /// <remarks>
    /// A second door onto the cover the existing button already sets, for the case that button
    /// cannot help with: wanting a better cover without already having the file. Nothing is written
    /// until something is picked, so backing out of this leaves the current cover alone.
    /// </remarks>
    [RelayCommand]
    async Task FindCoverArtAsync()
    {
        if (gameControlWeakReference.TryGetTarget(out Pages.GameDetailPage? gameControl) == false)
        {
            return;
        }

        // Said here rather than inside the picker, because an empty search box with an error under
        // it reads as a broken feature rather than one that has not been set up. Setting a key up
        // carries straight on into the search that asked for it.
        if (await SteamGridDbKeyPrompt.EnsureKeyAsync(gameControl.XamlRoot, ResourceHelper.GetString("CoverArt_Title")) == false)
        {
            return;
        }

        var picker = new CoverArtPicker(Game);

        var dialog = new EasyContentDialog(gameControl.XamlRoot)
        {
            Title = ResourceHelper.GetString("CoverArt_Title"),
            Content = picker,
            CloseButtonText = ResourceHelper.GetString("General_Cancel"),
        };

        // A ContentDialog is 548x756 whatever its content asks for, and it clips rather than
        // scrolls. Both caps were hit: at 548 wide the picker lost the two controls furthest right,
        // Search and the button that applies the cover, so it rendered as a search box that could
        // not search; at 756 tall the grid of covers pushed that same button row off the bottom.
        // Raised on this dialog's own resources, so only this one is affected.
        dialog.Resources["ContentDialogMaxWidth"] = 760d;
        dialog.Resources["ContentDialogMaxHeight"] = 960d;

        // The model closes the dialog once it has written, rather than the dialog cancelling its own
        // close and waiting on a command to let it through - which is what the dll picker does, and
        // why that one carries a comment wondering whether it is already closing.
        picker.ViewModel.Finished += (sender, args) => dialog.Hide();

        _ = await dialog.ShowAsync();

        // Closing the dialog has to stop whatever it had in flight, or a search that comes back
        // afterwards writes into a model nothing is showing any more.
        picker.ViewModel.Cancel();
    }

    /// <summary>
    /// Goes back to the games list.
    /// </summary>
    /// <remarks>
    /// This used to hide a dialog. As a page there is nothing to hide, so it navigates — which is
    /// also why the games page is cached: coming back lands where you left, with the same scroll
    /// position and the same tab.
    /// </remarks>
    [RelayCommand]
    void Close()
    {
        App.CurrentApp.MainWindow?.GoToGames();
    }

    [RelayCommand]
    async Task RemoveAsync()
    {
        if (gameControlWeakReference.TryGetTarget(out Pages.GameDetailPage? gameControl))
        {
            // This should never happen
            if (IsManuallyAdded == false)
            {
                var cantDeleteDialog = new EasyContentDialog(gameControl.XamlRoot)
                {
                    Title = ResourceHelper.GetString("General_Error"),
                    CloseButtonText = ResourceHelper.GetString("General_Okay"),
                    DefaultButton = ContentDialogButton.Close,
                    Content = ResourceHelper.GetString("GamePage_ManuallyAdded_CantBeRemoved"),
                };
                await cantDeleteDialog.ShowAsync();
                return;
            }



            // This needs to be set after AcceptsReturn otherwise it will strip out the \r
            var dialog = new EasyContentDialog(gameControl.XamlRoot)
            {
                Title = $"{ResourceHelper.GetString("General_Remove")} {Game.Title}?",
                PrimaryButtonText = ResourceHelper.GetString("General_Remove"),
                CloseButtonText = ResourceHelper.GetString("General_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                Content = ResourceHelper.GetFormattedResourceTemplate("GamePage_ManuallyAdded_RemoveGameTemplate", Game.Title),
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await Game.DeleteAsync();
                GameManager.Instance.RemoveGame(Game);
                App.CurrentApp.MainWindow?.GoToGames();
            }
        }
    }

    [RelayCommand]
    async Task FavouriteAsync()
    {
        Game.IsFavourite = !Game.IsFavourite;
        await Game.SaveToDatabaseAsync();

        // Favourites are one of the counted collections, for the same reason as above.
        GameManager.Instance.NotifyGamesChanged();
    }

    [RelayCommand]
    async Task ChangeRecordAsync(GameAssetType gameAssetType)
    {
        if (gameControlWeakReference.TryGetTarget(out Pages.GameDetailPage? gameControl))
        {
            // Before the picker opens, not inside its swap - the picker is itself a ContentDialog
            // and WinUI allows one per root, so a warning shown from within it would throw.
            if (await MultiplayerWarning.EnsureAcknowledgedAsync(gameControl.XamlRoot, new[] { Game }) == false)
            {
                return;
            }

            var dialog = new EasyContentDialog(gameControl.XamlRoot)
            {
                Title = ResourceHelper.GetFormattedResourceTemplate("GamePage_SelectDllTemplateTitle", DLLManager.Instance.GetAssetTypeName(gameAssetType)),
                PrimaryButtonText = ResourceHelper.GetString("General_Swap"),
                IsPrimaryButtonEnabled = false,
                CloseButtonText = ResourceHelper.GetString("General_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
            };

            var dllPickerControl = new DLLPickerControl(dialog, Game, gameAssetType);
            dialog.Content = dllPickerControl;
            await dialog.ShowAsync();

            // The rows say what is on disk, and something just wrote to it. Without this the row
            // kept claiming the version it had before the swap — the file changed and the sentence
            // describing it did not, which is the exact failure this rebuild set out to remove.
            RefreshUpscalerRows();
            OnPropertyChanged(nameof(HasUpdatesVisibility));
        }
    }

    [RelayCommand]
    async Task SaveTitleAsync()
    {
        Game.Title = GameTitle;
        await Game.SaveToDatabaseAsync();
        OnPropertyChanged(nameof(GameTitleHasChanged));
    }

    /// <summary>
    /// Hands this game's outdated dlls to the games page's preview sheet.
    /// </summary>
    /// <remarks>
    /// It used to run <c>DllUpdatePrompt</c>: a confirmation giving only a count, then a modal
    /// progress dialog, then a summary — and no way back. The games page replaced all three with a
    /// sheet that lists exactly which files will be written and a strip that keeps what it wrote so
    /// it can put that batch back, and there was no reason this surface should not have the same.
    ///
    /// Closes first, because the sheet and the strip live on the page behind this dialog. That is
    /// the honest order: the review happens where the result will be visible.
    /// </remarks>
    [RelayCommand]
    void UpdateAllDlls()
    {
        var gameGridPageModel = App.CurrentApp.MainWindow?.GameGridPage?.ViewModel;
        if (gameGridPageModel is null)
        {
            return;
        }

        // The sheet is put up before navigating, not after. Setting it after the frame's content
        // changes left the games page showing nothing: the sheet exists on that page, and it has to
        // be there to be found when the page comes back.
        gameGridPageModel.ShowUpdatePreviewFor(Game);
        Close();
    }

    [RelayCommand]
    async Task ResetAllAsync()
    {
        if (gameControlWeakReference.TryGetTarget(out var gameControl) == false)
        {
            return;
        }

        var preview = DllUpdateRunner.GetRevertPreview(Game);

        var confirmation = preview.Count == 1
            ? ResourceHelper.GetFormattedResourceTemplate("DllRevert_ConfirmOneDllTemplate", Game.Title)
            : ResourceHelper.GetFormattedResourceTemplate("DllRevert_ConfirmOneGameTemplate", preview.Count, Game.Title);

        await DllUpdatePrompt.RunAsync(
            gameControl.XamlRoot,
            [Game],
            ResourceHelper.GetString("GamePage_ResetAll"),
            preview.Count,
            confirmation,

            // The button and the progress dialog say restore, because that is what this does. They
            // used to say "Update" and "Updating dlls" - the prompt hardcoded the update voice
            // whoever called it. Update_Undoing doubles as the batch strip's undo text, on purpose:
            // both are the same act of putting originals back, and one string cannot drift.
            ResourceHelper.GetString("General_Restore"),
            ResourceHelper.GetString("Update_Undoing"),
            ResourceHelper.GetString("DllRevert_NothingToRevert"),
            (games, progress, cancellationToken) => DllUpdateRunner.RevertGamesAsync(games, progress, cancellationToken),
            "DllRevert_Reverted",

            // The rows this will actually touch, named the way the update preview names its files:
            // what each dll is now, and what it goes back to. A count alone made the reset the one
            // write in the app that asked for a yes without showing its work.
            preview.Select(x => $"{x.EngineName}: {x.VersionChange}").ToList());

        // Same reason as the picker: this one has just put every dll back.
        RefreshUpscalerRows();
        OnPropertyChanged(nameof(HasUpdatesVisibility));
    }

    /// <summary>
    /// Pins a dll where it is, asking for the why, or releases the pin.
    /// </summary>
    /// <remarks>
    /// The reason is asked for at the moment of pinning because that is when it is known: "newer
    /// versions ghost in this game" is obvious on the day you rolled back and gone a month later,
    /// when update all offers the bad version again. It is optional — the pin holds either way.
    /// </remarks>
    [RelayCommand]
    async Task TogglePinAsync(GameAssetType assetType)
    {
        if (gameControlWeakReference.TryGetTarget(out var gameControl) == false)
        {
            return;
        }

        if (Game.IsDllPinned(assetType))
        {
            await Game.UnpinDllAsync(assetType);
        }
        else
        {
            var reasonBox = new TextBox()
            {
                PlaceholderText = ResourceHelper.GetString("GamePage_PinDialog_ReasonPlaceholder"),
            };

            var panel = new StackPanel() { Spacing = 12 };
            panel.Children.Add(new TextBlock()
            {
                Text = ResourceHelper.GetString("GamePage_PinDialog_Body"),
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(reasonBox);

            var dialog = new EasyContentDialog(gameControl.XamlRoot)
            {
                Title = ResourceHelper.GetFormattedResourceTemplate("GamePage_PinDialog_TitleTemplate", DLLManager.Instance.GetAssetTypeName(assetType)),
                PrimaryButtonText = ResourceHelper.GetString("GamePage_PinDialog_PinButton"),
                CloseButtonText = ResourceHelper.GetString("General_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                Content = panel,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await Game.PinDllAsync(assetType, reasonBox.Text);
        }

        // The row's sentence, this page's update button, and the "Have an update" tab all read
        // the pin, and the last lives on the games page behind this one.
        RefreshUpscalerRows();
        OnPropertyChanged(nameof(HasUpdatesVisibility));
        App.CurrentApp.MainWindow?.GameGridPage?.ViewModel.RefreshFilterTabs();
    }

    [RelayCommand]
    async Task MultipleDLLsFoundAsync(GameAssetType gameAssetType)
    {
        if (gameControlWeakReference.TryGetTarget(out Pages.GameDetailPage? gameControl))
        {
            var dialog = new EasyContentDialog(gameControl.XamlRoot)
            {
                Title = ResourceHelper.GetFormattedResourceTemplate("GamePage_MultipleDllsFoundTemplate", DLLManager.Instance.GetAssetTypeName(gameAssetType)),
                PrimaryButtonText = ResourceHelper.GetString("General_Okay"),
                DefaultButton = ContentDialogButton.Primary,
                Content = new MultipleDLLsFoundControl(Game, gameAssetType),
            };

            await dialog.ShowAsync();
        }
    }

    [RelayCommand]
    async Task ReadyToPlayStateMoreInformationAsync()
    {
        await Launcher.LaunchUriAsync(new Uri("https://github.com/beeradmoore/dlss-swapper/wiki/Troubleshooting#game-is-not-in-a-ready-to-play-state"));
    }

    [RelayCommand]
    async Task DLSSPresetInfoAsync()
    {
        if (gameControlWeakReference.TryGetTarget(out Pages.GameDetailPage? gameControl))
        {
            var dialog = new EasyContentDialog(gameControl.XamlRoot)
            {
                Title = ResourceHelper.GetString("GamePage_DLSSPresetInfo_Title"),
                PrimaryButtonText = ResourceHelper.GetString("General_Okay"),
                SecondaryButtonText = ResourceHelper.GetString("GamePage_DLSSPresetInfo_OnScreenIndicator"),
                DefaultButton = ContentDialogButton.Primary,
                Content = ResourceHelper.GetString("GamePage_DLSSPresetInfo_Message"),
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Secondary)
            {
                await Launcher.LaunchUriAsync(new Uri("https://github.com/beeradmoore/dlss-swapper/wiki/DLSS-Developer-Options#on-screen-indicator"));
            }
        }
    }

    TaskCompletionSource? _reloadGameTaskCompletionSource;

    [RelayCommand]
    async Task ReloadGameAsync()
    {
        if (_reloadGameTaskCompletionSource is not null)
        {
            _reloadGameTaskCompletionSource.SetCanceled();
        }

        if (gameControlWeakReference.TryGetTarget(out var control))
        {
            _reloadGameTaskCompletionSource = new TaskCompletionSource();

            Game.PropertyChanged += Game_PropertyChanged;
            Game.NeedsProcessing = true;
            Game.ProcessGame();

            var dialogStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var dialog = new EasyContentDialog(App.CurrentApp.MainWindow.Content.XamlRoot)
            {
                Title = ResourceHelper.GetString("GamesPage_ReloadingGame"),
                Content = new ProgressRing()
                {
                    IsIndeterminate = true,
                },
                PrimaryButtonText = ResourceHelper.GetString("General_Cancel")
            };
            var dialogTask = dialog.ShowAsync().AsTask();

            await Task.WhenAny(dialogTask, _reloadGameTaskCompletionSource.Task);

            Game.PropertyChanged -= Game_PropertyChanged;


            if (dialogTask.IsCompleted)
            {
                // User clicked cancel, close the current dialog.
                Close();
            }
            else
            {
                var loadingDuration = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - dialogStart;

                if (loadingDuration < 1000)
                {
                    // Force loading dialog to exist for at least 1 second
                    await Task.Delay(1000 - (int)loadingDuration);
                }

                Close();

                if (dialogTask.IsCompleted == true)
                {
                    return;
                }

                // Game finished reloading, so open it again — a fresh page rather than this one,
                // because everything it shows was rebuilt from what the rescan found.
                _reloadGameTaskCompletionSource = null;
                dialog.Hide();
                App.CurrentApp.MainWindow?.ShowGame(Game);
            }
        }
    }

    private void Game_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Game.Processing))
        {
            if (Game.Processing == true)
            {
                _reloadGameTaskCompletionSource?.SetResult();
            }
        }
        else if (e.PropertyName == nameof(Game.CoverImage))
        {
            // The button below says which of its two things it does, and setting or removing a
            // cover is what swaps them over.
            OnPropertyChanged(nameof(CoverButtonText));
        }
    }

    /// <summary>
    /// What the cover button does, rather than only ever what it did on a game with no custom cover.
    /// </summary>
    /// <remarks>
    /// It was labelled "Add custom cover" always, while AddCoverImageAsync diverts to
    /// PromptToRemoveCustomCover whenever one is already set - so the only labelled control
    /// offering to change a cover said Add and opened "Remove custom cover?". Same check drives
    /// both, so the word and the behaviour cannot disagree.
    /// </remarks>
    public string CoverButtonText => Game.CoverImage == Game.ExpectedCustomCoverImage
        ? ResourceHelper.GetString("GamePage_RemoveCustomCover")
        : ResourceHelper.GetString("GamePage_AddCustomCover");

    [RelayCommand]
    async Task ShowHideGameAsync()
    {
        if (Game.IsHidden is null)
        {
            Game.IsHidden = true;
        }
        else
        {
            Game.IsHidden = !Game.IsHidden;
        }
        await Game.SaveToDatabaseAsync();

        // The views observe IsHidden and drop the row on their own; the counts beside them do not,
        // so "All games" went on reading one more than the list it labels.
        GameManager.Instance.NotifyGamesChanged();
    }

    [RelayCommand]
    async Task NVAPIErrorAsync()
    {
        if (gameControlWeakReference.TryGetTarget(out var control))
        {
            await NVAPIHelper.Instance.DisplayNVAPIErrorAsync(control.XamlRoot);
        }
    }
}
