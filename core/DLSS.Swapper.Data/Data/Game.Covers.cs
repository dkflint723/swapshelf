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
/// The cover art side of a game: where covers live, fetching and resizing them, choosing one by
/// hand, and repairing a stale path.
/// </summary>
/// <remarks>
/// Split out of Game.cs so the code that draws a picture does not share a file with the code that
/// rewrites dlls in a game folder. Three cover bugs in one fortnight all lived in a 2,600-line file
/// next to the swap; this is the same class, in a file of its own.
/// </remarks>
public abstract partial class Game
{
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

    bool _isLoadingCoverImage;

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
}
