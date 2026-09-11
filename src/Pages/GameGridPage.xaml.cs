using DLSS_Swapper.Data;
using DLSS_Swapper.UserControls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Windows.System;
using AsyncAwaitBestPractices;
using CommunityToolkit.WinUI;
using System.Threading;
using DLSS_Swapper.Helpers;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace DLSS_Swapper.Pages;


/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class GameGridPage : Page
{
    public static string PageTag { get; } = "PageTag_Games";

    /*
    public List<IGameLibrary> GameLibraries { get; } = new List<IGameLibrary>();

    Dictionary<GameLibrary, ObservableCollection<Game>> allGames = new Dictionary<GameLibrary, ObservableCollection<Game>>();


    public List<GameGroup> GroupedGameGroups { get; } = new List<GameGroup>();
    public List<GameGroup> UngroupedGameGroups { get; } = new List<GameGroup>();

    ObservableCollection<Game> FavouriteGames = new ObservableCollection<Game>();
    ObservableCollection<Game> AllGames = new ObservableCollection<Game>();
    */

    bool _loadingGamesAndDlls;
    Timer? _saveScrollSizeTimer;

    public GameGridPageModel ViewModel { get; private set; }

    public GameGridPage()
    {
        this.InitializeComponent();
        ViewModel = new GameGridPageModel(this);
        DataContext = ViewModel;

        // The library asks for a game to be brought into view after adding one; this is the page
        // that can do it. Registered here rather than reached through the window, because the
        // library is compiled without a window now.
        GameManager.ScrollToGameRequested = ScrollToGame;

        // The preview sheet is a modal drawn inside the page rather than a dialog, so nothing gives
        // it a focus scope for free. See UpdatePreviewOpened.
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        GettingFocus += RootGameGridPage_GettingFocus;
    }

    void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameGridPageModel.UpdatePreview))
        {
            if (ViewModel.UpdatePreview is null)
            {
                UpdatePreviewClosed();
            }
            else
            {
                UpdatePreviewOpened();
            }
        }
    }

    /// <summary>
    /// Opens one game's details over the list.
    /// </summary>
    /// <remarks>
    /// The page is built here rather than bound, because it is constructed per game and holds a
    /// view model for that game - see GameDetailPage. Everything that used to navigate to it still
    /// calls MainWindow.ShowGame, which comes here now.
    /// </remarks>
    internal void ShowGameDetail(Game game)
    {
        if (ViewModel.OpenGame == game && GameDetailHost.Content is not null)
        {
            return;
        }

        _focusBeforeGameDetail = FocusManager.GetFocusedElement(XamlRoot) as Control;

        GameDetailHost.Content = new GameDetailPage(game);
        ViewModel.OpenGame = game;

        // Queued, for the reason on UpdatePreviewOpened: the sheet is made visible by a binding on
        // the property just set, so nothing in it is focusable yet.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ViewModel.OpenGame is null)
            {
                return;
            }

            if (FocusManager.FindFirstFocusableElement(GameDetailSheet) is Control first)
            {
                first.Focus(FocusState.Programmatic);
            }
        });
    }

    /// <summary>Closes it, and lets go of the page so the game it was about can be collected.</summary>
    internal void CloseGameDetail()
    {
        if (ViewModel.OpenGame is null)
        {
            return;
        }

        ViewModel.OpenGame = null;
        GameDetailHost.Content = null;

        // A pending restore that points inside the sheet is about to become a detached element, so
        // it is re-pointed at whatever opened the sheet. Reachable: the game page's "Update all
        // dlls" opens the preview and closes the sheet in that order, so the preview recorded a
        // button that this line is about to tear out of the tree, and cancelling the preview then
        // restored focus nowhere at all.
        if (_focusBeforeUpdatePreview is not null && IsInside(_focusBeforeUpdatePreview, GameDetailSheet))
        {
            _focusBeforeUpdatePreview = _focusBeforeGameDetail;
        }

        var restoreTo = _focusBeforeGameDetail;
        _focusBeforeGameDetail = null;

        RestoreFocusTo(restoreTo);
    }

    /// <summary>
    /// Puts focus back on a control, if it is still there to put it on.
    /// </summary>
    /// <remarks>
    /// Focus() on a detached element returns false and does nothing, which leaves the window with no
    /// focused element at all: the next Tab starts again from the top of the page with no visible
    /// ring in between. XamlRoot goes null when an element leaves the tree, which is the cheapest
    /// way to ask.
    /// </remarks>
    void RestoreFocusTo(Control? restoreTo)
    {
        if (restoreTo is null)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (restoreTo.XamlRoot is null)
            {
                return;
            }

            restoreTo.Focus(FocusState.Programmatic);
        });
    }

    Control? _focusBeforeGameDetail;

    void GameDetailBackdrop_Tapped(object sender, TappedRoutedEventArgs e)
    {
        CloseGameDetail();
    }

    /// <summary>
    /// Whichever sheet is over the list, or null when none is.
    /// </summary>
    /// <remarks>
    /// The focus trap and Escape both need this, and both used to name the update preview directly.
    /// Adding a second overlay meant either teaching them about it in two places or asking once.
    /// </remarks>
    FrameworkElement? OpenSheet()
    {
        if (ViewModel.OpenGame is not null)
        {
            return GameDetailSheet;
        }

        if (ViewModel.UpdatePreview is not null)
        {
            return UpdatePreviewSheet;
        }

        return null;
    }

    bool hasFirstLoaded;
    void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (hasFirstLoaded)
        {
            return;
        }
        hasFirstLoaded = true;

        if (DataContext is GameGridPageModel gameGridPageModel)
        {
            gameGridPageModel.InitialLoadAsync().SafeFireAndForget((err) =>
            {
                Logger.Error(err, $"Unable to perform initial load");
            });
        }

        //await LoadGamesAndDlls();
        //await LoadGamesFromCacheAsync();
        //UpdateGameLibraries();
        //await LoadGames();
    }


    async Task LoadGamesAndDlls()
    {
        // TODO: REMOVE
        await Task.Delay(1);

        if (_loadingGamesAndDlls)
            return;

        _loadingGamesAndDlls = true;

        // TODO: Fade?
        //LoadingStackPanel.Visibility = Visibility.Visible;

        /*
        var tasks = new List<Task>();
        tasks.Add(LoadGamesAsync());


        await Task.WhenAll(tasks);

        */
        App.CurrentApp.RunOnUIThread(() =>
        {
            //LoadingStackPanel.Visibility = Visibility.Collapsed;
            _loadingGamesAndDlls = false;
        });
    }

    internal void ScrollToGame(Game game)
    {
        if (MainContentControl.ContentTemplateRoot is GridView mainGridView)
        {
            App.CurrentApp.RunOnUIThreadAsync(async () =>
            {
                var indexOfGame = mainGridView.Items.IndexOf(game);
                if (indexOfGame >= 0)
                {
                    await mainGridView.SmoothScrollIntoViewWithItemAsync(indexOfGame);
                }
            }).SafeFireAndForget();
        }
        else if (MainContentControl.ContentTemplateRoot is ListView mainListView)
        {
            App.CurrentApp.RunOnUIThreadAsync(async () =>
            {
                var indexOfGame = mainListView.Items.IndexOf(game);
                if (indexOfGame >= 0)
                {
                    await mainListView.SmoothScrollIntoViewWithItemAsync(indexOfGame);
                }
            }).SafeFireAndForget();
        }
    }

    internal void ReloadMainContentControl()
    {
        MainContentControl.Content = null;
        MainContentControl.Content = ViewModel;
    }

    // This fires for both the GridView and the ListView
    void GridAndListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Game selectedGame)
        {
            if (selectedGame.Processing)
            {
                var dialog = new EasyContentDialog(XamlRoot)
                {
                    Title = ResourceHelper.GetString("Game_CurrentlyProcessing"),
                    CloseButtonText = ResourceHelper.GetString("General_Okay"),
                    Content = ResourceHelper.GetFormattedResourceTemplate("GamePage_ProcessingPleaseWaitTemplate", selectedGame.Title),
                };
                _ = dialog.ShowAsync();
                return;
            }

            // A page now, not a dialog over this one. This page stays cached underneath, so coming
            // back lands on the same scroll position and the same tab.
            App.CurrentApp.MainWindow?.ShowGame(selectedGame);
        }
    }


    void MainGridView_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            var delta = e.GetCurrentPoint((UIElement)sender).Properties.MouseWheelDelta;

            if (sender is GridView gridView)
            {
                double scaleAmount = delta > 0 ? 1.05 : 0.95;
                var newWidth = (int)(ViewModel.GridViewItemWidth * scaleAmount);

                if (newWidth > 60 && newWidth < 600)
                {
                    ViewModel.GridViewItemWidth = newWidth;

                    if (_saveScrollSizeTimer is not null)
                    {
                        _saveScrollSizeTimer.Dispose();
                        _saveScrollSizeTimer = null;
                    }

                    _saveScrollSizeTimer = new Timer((state) =>
                    {
                        Settings.Instance.GridViewItemWidth = ViewModel.GridViewItemWidth;
                    }, null, 500, Timeout.Infinite);
                }
            }

            e.Handled = true;
        }
    }

    private void ClearSearchBox_Click(object sender, RoutedEventArgs e)
    {
        ClearSearchBox();
    }

    private void UndoneSwapsInfoBar_CloseButtonClick(InfoBar sender, object args)
    {
        ViewModel.DismissUndoneSwaps();
    }

    private void InterruptedSwapsInfoBar_CloseButtonClick(InfoBar sender, object args)
    {
        ViewModel.DismissInterruptedSwaps();
    }

    /// <summary>
    /// Empties the search box, which is what actually re-runs the search.
    /// </summary>
    /// <remarks>
    /// The box owns the text, so the empty state's "Clear search" has to go through it rather than
    /// clearing the collection behind its back and leaving the query still sitting there.
    /// </remarks>
    internal void ClearSearchBox()
    {
        SearchBox.Text = string.Empty;
    }

    /// <summary>
    /// Clicking the dimmed area behind the preview sheet cancels it.
    /// </summary>
    /// <remarks>
    /// One of three ways out, all of them the same command, because a sheet that writes to game
    /// folders must never be harder to escape than to accept.
    /// </remarks>
    void UpdatePreviewBackdrop_Tapped(object sender, TappedRoutedEventArgs e)
    {
        ViewModel.CancelUpdatePreviewCommand.Execute(null);
    }

    /// <summary>
    /// Puts focus into the preview sheet, and keeps it there while it is open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sheet that is about to write to game folders was, to the keyboard, not there at all. Focus
    /// stayed on the button that opened it, so Tab walked the list, the filters and the command bar
    /// underneath the dimming - all of it invisible behind a scrim and all of it still reachable and
    /// still clickable by Space. There was no way to reach Confirm without a mouse.
    /// </para>
    /// <para>
    /// Both halves are needed. Moving focus in is what makes the sheet operable; refusing to let it
    /// back out is what keeps the scrimmed page from being reachable by keyboard.
    /// </para>
    /// <para>
    /// This handler is hooked on the page, so it only sees focus arriving somewhere inside the page.
    /// The shell sidebar is outside it, in the window, and this cannot redirect focus away from
    /// there. The sheet carries TabFocusNavigation="Cycle" for that, the way a ContentDialog does;
    /// see the comment above it in the markup.
    /// </para>
    /// </remarks>
    void UpdatePreviewOpened()
    {
        _focusBeforeUpdatePreview = FocusManager.GetFocusedElement(XamlRoot) as Control;

        // Queued rather than called straight. The sheet is made visible by a binding on this same
        // property change, so at this point it has not been laid out and has nothing focusable in it
        // yet - focusing it here silently did nothing.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ViewModel.UpdatePreview is null)
            {
                return;
            }

            if (FocusManager.FindFirstFocusableElement(UpdatePreviewSheet) is Control first)
            {
                first.Focus(FocusState.Programmatic);
            }
        });
    }

    /// <summary>Hands focus back to whatever opened the sheet.</summary>
    /// <remarks>
    /// Otherwise focus is left on a button that has just been collapsed, and the next Tab starts
    /// again from the top of the page rather than from where the person was.
    /// </remarks>
    void UpdatePreviewClosed()
    {
        var restoreTo = _focusBeforeUpdatePreview;
        _focusBeforeUpdatePreview = null;

        RestoreFocusTo(restoreTo);
    }

    Control? _focusBeforeUpdatePreview;

    void RootGameGridPage_GettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        var sheet = OpenSheet();
        if (sheet is null)
        {
            return;
        }

        if (args.NewFocusedElement is DependencyObject candidate && IsInside(candidate, sheet))
        {
            return;
        }

        // Wrapped in the direction it was going, so Tab off the end lands on the first control in
        // the sheet and Shift+Tab off the start lands on the last - the same wrap a dialog gives.
        var replacement = args.Direction == FocusNavigationDirection.Previous
            ? FocusManager.FindLastFocusableElement(sheet)
            : FocusManager.FindFirstFocusableElement(sheet);

        if (replacement is Control control && args.TrySetNewFocusedElement(control))
        {
            args.Handled = true;

            return;
        }

        args.TryCancel();
    }

    static bool IsInside(DependencyObject element, DependencyObject container)
    {
        var current = element;

        while (current is not null)
        {
            if (ReferenceEquals(current, container))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // The game sheet first: it is the one drawn on top, so it is the one Escape is about when
        // both are somehow open.
        if (ViewModel.OpenGame is not null)
        {
            CloseGameDetail();

            args.Handled = true;

            return;
        }

        if (ViewModel.UpdatePreview is null)
        {
            return;
        }

        ViewModel.CancelUpdatePreviewCommand.Execute(null);

        // Only swallowed when it did something, so Esc stays free for everything else on the page.
        args.Handled = true;
    }

    /// <summary>
    /// Folds or unfolds a launcher section, and keeps its heading where it was.
    /// </summary>
    /// <remarks>
    /// Folding removes the games from the view rather than hiding them, because a GridView sizes
    /// every cell from the first item it measures and hiding rows collapsed the whole grid. The cost
    /// is that the content gets shorter, so the scroll viewer clamps how far down it is; unfolding
    /// puts the games back above where it now sits, and they arrive off the top of the screen.
    ///
    /// So the heading is brought back into view afterwards. No alignment ratio, which means the
    /// least scrolling that works: a heading still on screen does not move at all, and one that has
    /// gone off the top comes back just far enough to see. Queued rather than called here, because
    /// at this point the list has not been laid out again and the heading's new position does not
    /// exist yet.
    /// </remarks>
    void GroupHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GameGroup group } header)
        {
            group.ToggleExpanded();

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                header.StartBringIntoView(new BringIntoViewOptions() { AnimationDesired = false });
            });
        }
    }
}
