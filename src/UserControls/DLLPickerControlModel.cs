using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AsyncAwaitBestPractices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DLSS_Swapper.Data;
using DLSS_Swapper.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DLSS_Swapper.UserControls;

public partial class DLLPickerControlModel : ObservableObject
{
    WeakReference<EasyContentDialog> _parentDialogWeakReference;
    WeakReference<DLLPickerControl> _dllPickerControlWeakReference;

    public Game Game { get; private set; }
    public GameAssetType GameAssetType { get; private set; }

    public List<DLLRecord> DLLRecords { get; private set; }

    /// <summary>The list as shown: filtered by the search box, grouped by release line.</summary>
    /// <remarks>
    /// The same <see cref="DllVersionGroup.Build"/> and <see cref="DllSearch.Matches"/> the
    /// upscalers page uses, so the picker and the page can never disagree about grouping or about
    /// what a query matches. The picker was a flat run of a hundred near identical numbers with no
    /// way to jump - the two tested rules for exactly this were sitting one page over.
    /// </remarks>
    public System.Collections.ObjectModel.ObservableCollection<DllVersionGroup> VersionGroups { get; } = new System.Collections.ObjectModel.ObservableCollection<DllVersionGroup>();

    /// <summary>Whether this type has any versions at all, before the search has a say.</summary>
    /// <remarks>
    /// The search box shows on this rather than on the filtered count, because a query that
    /// matches nothing must leave the box on screen to be cleared.
    /// </remarks>
    [ObservableProperty]
    public partial bool HasAnyRecords { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    partial void OnSearchTextChanged(string value) => RebuildVersionGroups();

    void RebuildVersionGroups()
    {
        VersionGroups.Clear();

        var visible = DLLRecords.Where(x => DllSearch.Matches(x, SearchText)).ToList();

        foreach (var group in DllVersionGroup.Build(visible, DLLManager.Instance.GetAssetTypeName(GameAssetType)))
        {
            VersionGroups.Add(group);
        }

        HasAnyRecords = DLLRecords.Count > 0;
        AnyDLLsVisible = visible.Count > 0;

        // A selection the filter just hid would leave Swap armed for a row that is no longer on
        // screen - the button would do what it says to something invisible.
        if (SelectedDLLRecord is not null && visible.Contains(SelectedDLLRecord) == false)
        {
            SelectedDLLRecord = null;
        }
    }

    [ObservableProperty]
    public partial DLLRecord? SelectedDLLRecord { get; set; } = null;

    [ObservableProperty]
    public partial bool CanSwap { get; set; } = false;

    [ObservableProperty]
    public partial bool AnyDLLsVisible { get; set; } = false;

    [ObservableProperty]
    public partial GameAsset? CurrentGameAsset { get; set; } = null;

    [ObservableProperty]
    public partial GameAsset? BackupGameAsset { get; set; } = null;

    public bool CanCloseParentDialog { get; set; }

    public DLLPickerControlModelTranslationProperties TranslationProperties { get; } = new DLLPickerControlModelTranslationProperties();

    public DLLPickerControlModel(EasyContentDialog parentDialog, DLLPickerControl dllPickerControl, Game game, GameAssetType gameAssetType) : base()
    {
        _parentDialogWeakReference = new WeakReference<EasyContentDialog>(parentDialog);
        _dllPickerControlWeakReference = new WeakReference<DLLPickerControl>(dllPickerControl);

        parentDialog.Closing += (ContentDialog sender, ContentDialogClosingEventArgs args) =>
        {
            if (args.Result == ContentDialogResult.Primary)
            {
                if (CanCloseParentDialog == false)
                {
                    args.Cancel = true;
                }
            }
        };
        Game = game;
        GameAssetType = gameAssetType;
        parentDialog.PrimaryButtonCommand = SwapDllCommand;
        parentDialog.SecondaryButtonCommand = ResetDllCommand;

        var records = DLLManager.Instance.GetRecords(GameAssetType);
        DLLRecords = records is null ? [] : [.. records];

        if (Settings.Instance.OnlyShowDownloadedDlls == true)
        {
            // The version the game currently has stays in the list even when it is not downloaded,
            // otherwise the picker would not show what is installed.
            var currentHash = Game.GetAssetSlot(GameAssetType)?.CurrentAsset?.Hash;
            _ = DLLRecords.RemoveAll(x => x.MD5Hash != currentHash && x.LocalRecord?.IsDownloaded is false);
        }

        if (Settings.Instance.AllowDebugDlls == false)
        {
            DLLRecords.RemoveAll(x => x.IsDevFile == true);
        }

        // Prevent DLSS 1.0 showing up with DLSS 2/3 and vice versa
        if (GameAssetType == GameAssetType.DLSS)
        {
            var dlssRecords = Game.GameAssets.Where(x => x.AssetType == GameAssetType.DLSS).ToList();
            if (dlssRecords.Count > 0)
            {
                if (dlssRecords[0].Version.StartsWith("1."))
                {
                    DLLRecords.RemoveAll(x => x.Version.StartsWith("1.") == false);
                }
                else
                {
                    DLLRecords.RemoveAll(x => x.Version.StartsWith("1.") == true);
                }
            }
        }

        RebuildVersionGroups();

        ResetSelection();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName == nameof(SelectedDLLRecord))
        {
            if (SelectedDLLRecord is null)
            {
                CanSwap = false;
            }
            else if (SelectedDLLRecord.LocalRecord is null)
            {
                // This should never happen
                CanSwap = false;
            }
            else
            {
                CanSwap = true;
            }
        }
        else if (e.PropertyName == nameof(CanSwap))
        {
            if (_parentDialogWeakReference.TryGetTarget(out var dialog))
            {
                dialog.IsPrimaryButtonEnabled = CanSwap;
            }
        }
    }

    [RelayCommand]
    async Task SwapDllAsync()
    {
        if (SelectedDLLRecord?.LocalRecord is null)
        {
            return;
        }

        if (SelectedDLLRecord.LocalRecord.FileDownloader is not null)
        {
            ShowTempInfoBar(string.Empty, ResourceHelper.GetString("GamePage_DllPicker_WaitToDownloadBeforeSwapping"));
            return;
        }
        else if (SelectedDLLRecord.LocalRecord.IsDownloaded == false)
        {
            // One motion. Pressing Swap on a version that is not here yet used to start the
            // download and stop - the user watched the row's progress bar and then pressed Swap a
            // second time. The press already said what they want; the download is a step on the
            // way, not a destination. A second press while this waits lands in the downloader
            // guard above, which is the right answer for it.
            ShowTempInfoBar(string.Empty, ResourceHelper.GetString("GamePage_DllPicker_DownloadingBeforeSwap"), duration: 600);

            var (downloaded, downloadMessage, cancelled) = await SelectedDLLRecord.DownloadAsync();

            if (cancelled)
            {
                ShowTempInfoBar(string.Empty, ResourceHelper.GetString("GamePage_DllPicker_DownloadCancelled"));
                return;
            }

            if (downloaded == false)
            {
                ShowTempInfoBar(ResourceHelper.GetString("General_Error"), downloadMessage, severity: InfoBarSeverity.Error);
                return;
            }

            // Fell through: the file is here now, so the swap the user asked for happens.
        }

        var didUpdate = await Game.UpdateDllAsync(SelectedDLLRecord);

        if (didUpdate.Success == false)
        {
            ShowTempInfoBar(ResourceHelper.GetString("General_Error"), didUpdate.Message, severity: InfoBarSeverity.Error);
            return;
        }

        // Allow the dialog to close
        CanCloseParentDialog = true;

        if (_parentDialogWeakReference.TryGetTarget(out var dialog) == true)
        {
            // Is the dialog already closing when we call this?
            dialog.Hide();
        }
    }

    InfoBar? ShowTempInfoBar(string title, string message, double duration = 5.0, InfoBarSeverity severity = InfoBarSeverity.Informational, int gridIndex = 3, Button? actionButton = null)
    {
        InfoBar? infoBar = null;
        if (_dllPickerControlWeakReference.TryGetTarget(out var dllPickerControl) == true)
        {
            if (dllPickerControl.Content is Grid grid)
            {
                infoBar = new InfoBar();
                infoBar.Message = message;
                infoBar.Severity = severity;
                infoBar.IsOpen = true;
                infoBar.IsClosable = true;
                infoBar.ActionButton = actionButton;

                // Temp workaround until InfoBar has a solid color by default.
                // https://github.com/microsoft/microsoft-ui-xaml/issues/5741
                if (App.Current.Resources.TryGetValue("InfoBarInformationalSeverityBackgroundBrush", out var infoBarInformationalSeverityBackground) && infoBarInformationalSeverityBackground is SolidColorBrush infoBarInformationalSeverityBackgroundBrush)
                {
                    // Temp fix to make download indicator visibile in dark mode.
                    if (WindowManager.CurrentTheme == ElementTheme.Dark)
                    {
                        infoBar.Background = new SolidColorBrush(Color.FromArgb(255, 23, 23, 23));
                    }
                    else
                    {
                        infoBarInformationalSeverityBackgroundBrush.Color = Color.FromArgb(255, infoBarInformationalSeverityBackgroundBrush.Color.R, infoBarInformationalSeverityBackgroundBrush.Color.G, infoBarInformationalSeverityBackgroundBrush.Color.B);
                        infoBar.Background = infoBarInformationalSeverityBackgroundBrush;
                    }
                }

                Grid.SetRow(infoBar, gridIndex);
                grid.Children.Add(infoBar);

                var dispatcherTimer = new DispatcherTimer();
                dispatcherTimer.Tick += (object? sender, object e) =>
                {
                    // If the page has gone away, parent should be null and this should not cause problems
                    if (infoBar?.Parent is Grid parentGrid)
                    {
                        infoBar.IsOpen = false;
                        parentGrid.Children.Remove(infoBar);
                    }

                    if (sender is DispatcherTimer timer)
                    {
                        timer.Stop();
                    }
                };
                dispatcherTimer.Interval = TimeSpan.FromSeconds(duration);
                dispatcherTimer.Start();
            }
        }

        return infoBar;
    }

    [RelayCommand]
    void OpenDllPath()
    {
        if (CurrentGameAsset is null)
        {
            return;
        }

        try
        {
            if (File.Exists(CurrentGameAsset.Path))
            {
                Process.Start("explorer.exe", $"/select,{CurrentGameAsset.Path}");
            }
            else
            {
                var dllPath = Path.GetDirectoryName(CurrentGameAsset.Path) ?? string.Empty;
                if (Directory.Exists(dllPath))
                {
                    Process.Start("explorer.exe", dllPath);
                }
                else
                {
                    throw new Exception(ResourceHelper.GetFormattedResourceTemplate("GamePage_DllPicker_CouldNotFindFileTemplate", CurrentGameAsset.Path));
                }
            }
        }
        catch (Exception err)
        {
            Logger.Error(err);
            ShowTempInfoBar(ResourceHelper.GetString("General_Error"), err.Message, severity: InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    async Task ResetDllAsync()
    {
        await ResetDllAsync(restoreChangedFiles: false);
    }

    /// <summary>
    /// Puts the saved original back, asking first when the dll is no longer what this app put there.
    /// </summary>
    /// <remarks>
    /// Asked in place with an InfoBar rather than a dialog: the picker is itself a ContentDialog and
    /// WinUI allows one per root. The safe answer is the one that needs no click - closing the bar,
    /// or letting it time out, restores nothing.
    /// </remarks>
    async Task ResetDllAsync(bool restoreChangedFiles)
    {
        var didReset = await Game.ResetDllAsync(GameAssetType, restoreChangedFiles);

        if (didReset.Success == false)
        {
            if (didReset.NeedsConfirmation)
            {
                InfoBar? question = null;
                var restoreAnyway = new Button() { Content = ResourceHelper.GetString("GamePage_DllPicker_RestoreAnyway") };
                restoreAnyway.Click += async (_, _) =>
                {
                    if (question is not null)
                    {
                        question.IsOpen = false;
                    }

                    try
                    {
                        await ResetDllAsync(restoreChangedFiles: true);
                    }
                    catch (Exception err)
                    {
                        Logger.Error(err);
                        ShowTempInfoBar(ResourceHelper.GetString("General_Error"), err.Message, severity: InfoBarSeverity.Error, gridIndex: 0);
                    }
                };

                question = ShowTempInfoBar(ResourceHelper.GetString("General_Warning"), ResourceHelper.GetString("GamePage_DllPicker_TargetChanged"), duration: 60, severity: InfoBarSeverity.Warning, gridIndex: 0, actionButton: restoreAnyway);
                return;
            }

            ShowTempInfoBar(ResourceHelper.GetString("General_Error"), didReset.Message, severity: InfoBarSeverity.Error, gridIndex: 0);
            return;
        }

        // A reset can restore some locations and not others. Keep the dialog open when that happens so
        // the warning is actually read, rather than closing the dialog over the top of it.
        if (string.IsNullOrEmpty(didReset.Message) == false)
        {
            ResetSelection();
            ShowTempInfoBar(ResourceHelper.GetString("General_Warning"), didReset.Message, severity: InfoBarSeverity.Warning, gridIndex: 0);
            return;
        }

        if (_parentDialogWeakReference.TryGetTarget(out var parentDialog) == true)
        {
            parentDialog.Hide();
        }
    }

    void ResetSelection()
    {
        // If there are backup records it means we can reset.
        var backupRecordType = DLLManager.Instance.GetAssetBackupType(GameAssetType);
        var existingBackupRecords = Game.GameAssets.Where(x => x.AssetType == backupRecordType).ToList();
        BackupGameAsset = existingBackupRecords.FirstOrDefault();

        // Select the default record
        var existingRecords = Game.GameAssets.Where(x => x.AssetType == GameAssetType).ToList();
        CurrentGameAsset = existingRecords.FirstOrDefault();

        if (CurrentGameAsset is not null)
        {
            SelectedDLLRecord = DLLRecords.FirstOrDefault(x => x.MD5Hash == CurrentGameAsset.Hash);
        }
    }
}
