using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ByteSizeLib;
using CommunityToolkit.WinUI.Controls;
using DLSS_Swapper.Extensions;
using DLSS_Swapper.Helpers;
using DLSS_Swapper.UserControls;
using DLSS_Swapper.Releases;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DLSS_Swapper.Data.GitHub;

/// <summary>
/// Helper class to be notified of updates of the app (which is Debug and Release builds)
/// </summary>
internal class GitHubUpdater
{
    /// <summary>
    /// The repository whose releases count as updates to this build.
    /// </summary>
    /// <remarks>
    /// This fork, not the project it was forked from. Pointed at the original it was not an
    /// updater at all: every release there is a different application, and taking one would have
    /// overwritten this build with upstream's binary while calling it a new version.
    ///
    /// One constant, because there are two callers - the launch check and the settings button -
    /// and a repository named separately in each is a repository that ends up different in each.
    /// </remarks>
    const string Repository = "dkflint723/swapshelf";

    /// <summary>
    /// Queries GitHub and returns the latest GitHubRelease object, or null if the request failed.
    /// </summary>
    /// <returns>Latest GitHubRelease object, or null if the request failed</returns>
    internal async Task<GitHubRelease?> FetchLatestRelease(bool forceCheck)
    {
        var shouldDownload = true;
        var releasesFile = Storage.GetReleasesPath();
        if (File.Exists(releasesFile))
        {
            var fileInfo = new FileInfo(releasesFile);
            var lastModifiedTime = DateTime.Now - fileInfo.LastWriteTime;
            if (lastModifiedTime.TotalMinutes < 30)
            {
                shouldDownload = false;

                // If we are not downloading and we are not forced to check then return the existing object.
                if (forceCheck == false)
                {
                    // Inside a try, because this is a cache and an unusable cache is a miss rather
                    // than an error. It was outside every try: a zero byte releases.json - which the
                    // truncating write below could leave behind - threw a JsonException out of this
                    // method, up through an async void Loaded handler, to an unhandled exception
                    // handler that logs without marking it handled. The window disappeared, after
                    // the game list had already been drawn, on every launch until the file aged past
                    // thirty minutes.
                    try
                    {
                        using (var fileStream = File.OpenRead(releasesFile))
                        {
                            var githubRelease = JsonSerializer.Deserialize(fileStream, SourceGenerationContext.Default.GitHubRelease);
                            if (githubRelease is not null)
                            {
                                return githubRelease;
                            }
                        }
                    }
                    catch (Exception err)
                    {
                        Logger.Error(err, "The cached release could not be read, asking GitHub instead.");

                        shouldDownload = true;
                    }
                }
            }
        }

        if (shouldDownload == true || forceCheck == true)
        {
            try
            {
                using (var memoryStream = new MemoryStream())
                {
                    var fileDownloader = new FileDownloader($"https://api.github.com/repos/{Repository}/releases/latest", 0);
                    await fileDownloader.DownloadFileToStreamAsync(memoryStream).ConfigureAwait(false);

                    memoryStream.Position = 0;

                    var githubRelease = JsonSerializer.Deserialize(memoryStream, SourceGenerationContext.Default.GitHubRelease);
                    if (githubRelease is null)
                    {
                        throw new Exception("Could not load GitHub release data.");
                    }

                    memoryStream.Position = 0;

                    // If we did load the json, save it to disk. Beside and moved over, so an
                    // interrupted write cannot leave a truncated cache for the read above to trip
                    // on - see Storage.WriteFileAtomicallyAsync.
                    await Storage.WriteFileAtomicallyAsync(releasesFile, async fileStream =>
                    {
                        await memoryStream.CopyToAsync(fileStream).ConfigureAwait(false);
                    }).ConfigureAwait(false);

                    return githubRelease;
                }
            }
            catch (Exception err)
            {
                // NOOP
                Logger.Error(err);
                return null;
            }
        }

        return null;
    }

    internal async Task<GitHubRelease?> GetReleaseFromTag(string tag)
    {
        try
        {
            using (var memoryStream = new MemoryStream())
            {
                var fileDownloader = new FileDownloader($"https://api.github.com/repos/{Repository}/releases/tags/{tag}", 0);
                await fileDownloader.DownloadFileToStreamAsync(memoryStream).ConfigureAwait(false);
                memoryStream.Position = 0;
                var githubRelease = JsonSerializer.Deserialize(memoryStream, SourceGenerationContext.Default.GitHubRelease);
                if (githubRelease is null)
                {
                    throw new Exception("Could not load GitHub release data.");
                }

                return githubRelease;
            }
        }
        catch (Exception err)
        {
            // NOOP
            Logger.Error(err);
            Debugger.Break();
            return null;
        }
    }

    /// <summary>
    /// Queries GitHub and returns a GitHubRelease only if a newer version was detected, otherwise null
    /// </summary>
    /// <returns>GitHubRelease object if an update is available, otherwise null.</returns>
    /// <summary>
    /// Whether the last check failed to get an answer at all, as opposed to answering "no update".
    /// </summary>
    /// <remarks>
    /// The settings page used to read null as "no new updates are available" and say so - to a
    /// machine that was offline, or rate limited, or behind a broken proxy. Telling somebody they
    /// are up to date when nothing was checked is the one wrong answer an update checker can give.
    /// </remarks>
    internal bool CheckFailed { get; private set; }

    internal async Task<GitHubRelease?> CheckForNewGitHubRelease(bool forceCheck)
    {
        var latestRelease = await FetchLatestRelease(forceCheck).ConfigureAwait(false);
        if (latestRelease is null)
        {
            // Whoever asked cannot see the difference between "you are current" and "GitHub never
            // answered" from a null alone - see CheckFailed.
            CheckFailed = true;
            return null;
        }

        CheckFailed = false;

        var latestVersion = latestRelease.GetVersionNumber();
        var version = App.CurrentApp.GetVersion();
        var currentVersion = ((ulong)version.Major << 48) +
            ((ulong)version.Minor << 32) +
            ((ulong)version.Build << 16) +
            ((ulong)version.Revision);

        // New version is available.
        if (latestVersion > currentVersion)
        {
            return latestRelease;
        }

        return null;
    }


    internal bool HasPromptedBefore(GitHubRelease gitHubRelease)
    {
        return HasPromptedBefore(gitHubRelease.GetVersionNumber(), Settings.Instance.LastPromptWasForVersion);
    }

    /// <summary>
    /// Whether this release has already been offered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule DisplayNewUpdateDialog states four lines below where it writes the setting: do not
    /// prompt again for this version or lower. So a release is old news when it is at or below the
    /// last one prompted for, and new when it is above.
    /// </para>
    /// <para>
    /// It used to answer "already prompted" for everything except a release LOWER than the last one
    /// - the exact inverse. Close one update dialog and no future release was ever announced again,
    /// permanently and silently. It only showed the first time a genuinely newer version appeared,
    /// because the everyday case - the latest release being the one already prompted for - happens
    /// to give the right answer either way. Nothing tested it.
    /// </para>
    /// <para>
    /// A never-prompted setting of 0 needs no special case: any real release encodes above zero, so
    /// it is not "at or below" and the prompt is shown.
    /// </para>
    /// </remarks>
    internal static bool HasPromptedBefore(ulong thisVersion, ulong lastVersionPromptedFor)
    {
        return thisVersion <= lastVersionPromptedFor;
    }

    internal async Task DisplayNewUpdateDialog(GitHubRelease gitHubRelease, XamlRoot xamlRoot)
    {
        // Update settings so we won't auto prompt for this version (or lower) ever again.
        var versionNumber = gitHubRelease.GetVersionNumber();
        if (versionNumber > Settings.Instance.LastPromptWasForVersion)
        {
            Settings.Instance.LastPromptWasForVersion = versionNumber;
        }


        var currentVerion = App.CurrentApp.GetVersionString();

        var yourVersion = ResourceHelper.GetFormattedResourceTemplate("GitHubUpdater_CurrentVersionIsActualTemplate", currentVerion);
        var contentUpdate = new MarkdownTextBlock()
        {
            Text = $"{yourVersion}\n\n{gitHubRelease.Body}",
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Config = new MarkdownConfig(),
        };

        await App.CurrentApp.RunOnUIThreadAsync(async () =>
        {
            var dialog = new EasyContentDialog(xamlRoot)
            {
                Title = $"{ResourceHelper.GetString("GitHubUpdater_UpdateAvailable")} - {gitHubRelease.Name}",
                SecondaryButtonText = ResourceHelper.GetString("GitHubUpdater_ViewUpdate"),
                DefaultButton = ContentDialogButton.Secondary,
                CloseButtonText = ResourceHelper.GetString("General_Cancel"),
                Content = new ScrollViewer()
                {
                    Content = contentUpdate,
                },
            };

            GitHubReleaseAsset? installerAsset = null;

#if PORTABLE == false
            // Only show the update button if we could fetch the update that is ready to install.
            foreach (var gitHubAsset in gitHubRelease.Assets)
            {
                // Check all the strings we want to use exist.
                if (string.IsNullOrWhiteSpace(gitHubAsset.Name) ||
                    string.IsNullOrWhiteSpace(gitHubAsset.ContentType) ||
                    string.IsNullOrWhiteSpace(gitHubAsset.State) ||
                    string.IsNullOrWhiteSpace(gitHubAsset.Digest))
                {
                    continue;
                }

                // Check that we are looking at a exe file.
                if (gitHubAsset.ContentType.Equals("application/x-msdownload", StringComparison.OrdinalIgnoreCase) == false)
                {
                    continue;
                }

                // Check that the state is uploaded.
                if (gitHubAsset.State.Equals("uploaded", StringComparison.OrdinalIgnoreCase) == false)
                {
                    continue;
                }

                // Check if we are looking at something like "DLSS.Swapper-a.b.c.d-installer.exe"
                if (gitHubAsset.Name.EndsWith("-installer.exe", StringComparison.OrdinalIgnoreCase) == false)
                {
                    continue;
                }

                if (installerAsset is not null)
                {
                    // Something happened, we found TWO installer assets. Because we don't know what one should be used we will use none and auto-update will be disabled.
                    installerAsset = null;
                    break;
                }

                installerAsset = gitHubAsset;
            }

            // If the installer asset is found we add the update button and make it the primary response.
            if (installerAsset is not null)
            {
                dialog.PrimaryButtonText = ResourceHelper.GetString("General_Update");
                dialog.DefaultButton = ContentDialogButton.Primary;
            }
#endif

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary && installerAsset is not null)
            {
                await DownloadAndInstallAsync(gitHubRelease, installerAsset, xamlRoot);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await Launcher.LaunchUriAsync(new Uri(gitHubRelease.HtmlUrl));
            }
        });
    }

    /// <summary>
    /// The downloaded installer is not the file GitHub published for this release.
    /// </summary>
    /// <remarks>
    /// Its own type so the download failure handler can tell "the network let us down" from "the
    /// bytes are wrong", and delete the file in the second case rather than offer to reuse it.
    /// </remarks>
    sealed class UpdateIntegrityException : Exception
    {
        public UpdateIntegrityException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Hashes the file on disk and compares it to the digest GitHub published for the asset.
    /// </summary>
    /// <remarks>
    /// Called twice: once as the download lands, and again immediately before the installer is
    /// started, because the file sits in a folder any process running as this user can write to and
    /// the first check does not cover the seconds between it and the launch.
    /// </remarks>
    static bool DownloadedUpdateMatchesRelease(string path, GitHubReleaseAsset gitHubAsset)
    {
        using (var fileStream = File.OpenRead(path))
        {
            var actual = fileStream.GetSha256Hash();
            return ReleaseDigest.Matches(gitHubAsset.Digest, actual);
        }
    }

    async Task DownloadAndInstallAsync(GitHubRelease gitHubRelease, GitHubReleaseAsset gitHubAsset, XamlRoot xamlRoot)
    {
#if PORTABLE
        // You should not have got here.
        return;
#else
        var filesProgressBar = new ProgressBar()
        {
            IsIndeterminate = true
        };
        var progressTextBlock = new TextBlock()
        {
            Text = string.Empty,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        progressTextBlock.Inlines.Add(new Run()
        {
            Text = $"{ResourceHelper.GetString("GitHubUpdater_DownloadProgress")}: "
        });
        var progressRun = new Run() { Text = "-" };
        progressTextBlock.Inlines.Add(progressRun);
        var progressStackPanel = new StackPanel()
        {
            Spacing = 16,
            Orientation = Orientation.Vertical,
            Children =
            {
                filesProgressBar,
                progressTextBlock,
            }
        };




        var updatesFolder = Storage.GetUpdatesFolder();
        var tempDownloadFile = Path.Combine(updatesFolder, gitHubAsset.Name);
        if (Directory.Exists(updatesFolder) == false)
        {
            Directory.CreateDirectory(updatesFolder);
        }

        var shouldDownload = true;
        if (File.Exists(tempDownloadFile))
        {
            using (FileStream fileStream = File.OpenRead(tempDownloadFile))
            {
                var hash = fileStream.GetSha256Hash();
                if (gitHubAsset.Digest.Equals($"sha256:{hash}", StringComparison.OrdinalIgnoreCase))
                {
                    shouldDownload = false;
                }
            }
        }


        if (shouldDownload)
        {
            var cancellationTokenSource = new CancellationTokenSource();

            var downloadingDialog = new EasyContentDialog(xamlRoot)
            {
                Title = ResourceHelper.GetString("GitHubUpdater_DownloadingUpdate_Title"),
                Content = progressStackPanel,
                CloseButtonText = ResourceHelper.GetString("General_Cancel"),
            };
            downloadingDialog.CloseButtonClick += (sender, args) =>
            {
                try
                {
                    cancellationTokenSource.Cancel();
                }
                catch (Exception)
                {
                    // NOOP
                }
            };
            _ = downloadingDialog.ShowAsync();

            var totalSizeString = ByteSize.FromBytes(gitHubAsset.Size).ToString("MB", CultureInfo.CurrentCulture);
            var fileDownloader = new FileDownloader(gitHubAsset.BrowserDownloadUrl);

            try
            {
                using (var fileStream = File.Create(tempDownloadFile))
                {
                    var downloaderTask = fileDownloader.DownloadFileToStreamAsync(fileStream, cancellationTokenSource.Token, progressCallback: (downloadedBytes, totalBytes, percent) =>
                    {
                        var displayPercent = percent * 100;
                        progressRun.Text = $"{ByteSize.FromBytes(downloadedBytes).MegaBytes.ToString("F2", CultureInfo.CurrentCulture)} / {totalSizeString} ({percent:F1}%)";
                        filesProgressBar.IsIndeterminate = false;
                        filesProgressBar.Value = percent;
                    });


                    var didDownload = await downloaderTask;
                    if (didDownload == false)
                    {
                        throw new Exception("DownloadFileToStreamAsync returned false.");
                    }

                    // The digest was already in hand - it decided above whether a leftover file
                    // could be reused - and a fresh download went from here to Process.Start without
                    // being asked the same question. For an unsigned installer this comparison is
                    // the only thing between "GitHub served this" and "this ran as you".
                    fileStream.Position = 0;
                    var downloadedHash = fileStream.GetSha256Hash();
                    if (ReleaseDigest.Matches(gitHubAsset.Digest, downloadedHash) == false)
                    {
                        throw new UpdateIntegrityException($"Downloaded {gitHubAsset.Name} hashed to {downloadedHash}, GitHub published {gitHubAsset.Digest}.");
                    }

                    downloadingDialog.Hide();
                }

            }
            catch (TaskCanceledException) when (cancellationTokenSource.IsCancellationRequested)
            {
                // User cancelled.
                downloadingDialog.Hide();
                return;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                downloadingDialog.Hide();

                // A file that does not match must not sit in the updates folder where the next
                // attempt would find it and, its digest now failing, download over it anyway - or
                // worse, where something else finds it first.
                var integrityFailure = ex is UpdateIntegrityException;
                if (integrityFailure)
                {
                    try
                    {
                        File.Delete(tempDownloadFile);
                    }
                    catch (Exception deleteErr)
                    {
                        Logger.Error(deleteErr);
                    }
                }

                var downloadErrorDialog = new EasyContentDialog(xamlRoot)
                {
                    Title = ResourceHelper.GetString("General_Error"),
                    Content = ResourceHelper.GetString(integrityFailure ? "GitHubUpdater_UpdateDidNotMatch" : "GitHubUpdater_UpdateDownloadFailed"),
                    PrimaryButtonText = ResourceHelper.GetString("GitHubUpdater_ViewUpdate"),
                    CloseButtonText = ResourceHelper.GetString("General_Cancel"),
                    DefaultButton = ContentDialogButton.Primary,
                };

                var downloadErrorResult = await downloadErrorDialog.ShowAsync();
                if (downloadErrorResult == ContentDialogResult.Primary)
                {
                    await Launcher.LaunchUriAsync(new Uri(gitHubRelease.HtmlUrl));
                }

                return;
            }
        }


        var installDialog = new EasyContentDialog(xamlRoot)
        {
            Title = ResourceHelper.GetString("GitHubUpdater_DownloadComplete_Title"),
            Content = ResourceHelper.GetString("GitHubUpdater_UpdateReadyToInstall"),
            PrimaryButtonText = ResourceHelper.GetString("GitHubUpdater_Install"),
            CloseButtonText = ResourceHelper.GetString("General_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        var installDialogResult = await installDialog.ShowAsync();

        if (installDialogResult == ContentDialogResult.Primary)
        {

            var updatingDialog = new EasyContentDialog(xamlRoot)
            {
                Title = ResourceHelper.GetString("GitHubUpdater_Updating_Title"),
                Content = new ProgressRing() { IsIndeterminate = true },
            };
            _ = updatingDialog.ShowAsync();

            // Give the popup time to show.
            await Task.Delay(500);

            // Verified again on the way in, not only on the way down. The file has sat in a folder
            // any process running as this user can write to, for however long the install dialog was
            // open. If it is no longer the file GitHub published, it does not run.
            if (DownloadedUpdateMatchesRelease(tempDownloadFile, gitHubAsset) == false)
            {
                Logger.Error($"{tempDownloadFile} no longer matches the published digest at launch time. Not starting it.");
                updatingDialog.Hide();

                try
                {
                    File.Delete(tempDownloadFile);
                }
                catch (Exception deleteErr)
                {
                    Logger.Error(deleteErr);
                }

                var mismatchDialog = new EasyContentDialog(xamlRoot)
                {
                    Title = ResourceHelper.GetString("General_Error"),
                    Content = ResourceHelper.GetString("GitHubUpdater_UpdateDidNotMatch"),
                    CloseButtonText = ResourceHelper.GetString("General_Okay"),
                    DefaultButton = ContentDialogButton.Close,
                };
                await mismatchDialog.ShowAsync();
                return;
            }

            try
            {
                var processStartInfo = new ProcessStartInfo()
                {
                    FileName = tempDownloadFile,
                    UseShellExecute = true,
                };
                var installerProcess = Process.Start(processStartInfo);
                if (installerProcess is null)
                {
                    throw new Exception("Could not launch installer");
                }

                // Close DLSS Swapper so the installer can install
                Application.Current.Exit();
            }
            catch (Exception err)
            {
                Logger.Error(err);

                updatingDialog.Hide();

                var errorDialog = new EasyContentDialog(xamlRoot)
                {
                    Title = ResourceHelper.GetString("General_Error"),
                    Content = ResourceHelper.GetString("GitHubUpdater_CouldNotRunInstaller"),
                    PrimaryButtonText = ResourceHelper.GetString("GitHubUpdater_ViewUpdate"),
                    CloseButtonText = ResourceHelper.GetString("General_Cancel"),
                    DefaultButton = ContentDialogButton.Primary,
                };
                var errorDialogResult = await errorDialog.ShowAsync();

                if (errorDialogResult == ContentDialogResult.Primary)
                {
                    await Launcher.LaunchUriAsync(new Uri(gitHubRelease.HtmlUrl));
                }
            }
        }
#endif
    }

    internal async Task DisplayWhatsNewDialog(GitHubRelease gitHubRelease, XamlRoot xamlRoot)
    {
        var contentUpdate = new MarkdownTextBlock()
        {
            Text = gitHubRelease.Body,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Config = new MarkdownConfig(),
        };

        var dialog = new EasyContentDialog(xamlRoot)
        {
            Title = $"{ResourceHelper.GetString("GitHubUpdater_DlssSwapperUpdated")} - {gitHubRelease.Name}",
            CloseButtonText = ResourceHelper.GetString("General_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new ScrollViewer()
            {
                Content = contentUpdate,
            },
        };
        await dialog.ShowAsync();
    }
}
