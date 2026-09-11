using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using DLSS_Swapper.Data;
using DLSS_Swapper.Data.Steam;
using DLSS_Swapper.Dlls;
using DLSS_Swapper.Interfaces;
using DLSS_Swapper.Swapping;

namespace DLSS_Swapper.Cli;

/// <summary>
/// A headless way in to the same swap the app performs.
/// </summary>
/// <remarks>
/// <para>
/// Exists so other things - a Steam client plugin, a script, a scheduled job - can swap dlls
/// without reimplementing any of the rules that decide what a swap is allowed to do. Every one of
/// them lives behind this: which files a type owns, the transactional write and its rollback, the
/// per-path saved original that is never overwritten, pins, the version ranking FSR breaks if you
/// get it wrong. A second implementation of those in another language would drift, and the way it
/// would show up is wrong files written into game folders.
/// </para>
/// <para>
/// So this process does no swapping of its own. It loads what the app loads, finds what was asked
/// for, and calls the same UpdateDllAsync and ResetDllAsync the buttons call.
/// </para>
/// <para>
/// Output is always one JSON object on stdout, whatever happened - including failures, which carry
/// ok:false and a message rather than only an exit code. Callers should read stdout and check "ok".
/// The exit code agrees with it, for shell use.
/// </para>
/// </remarks>
static class Program
{
    /// <summary>
    /// The shape of the JSON below. Callers should refuse a version they do not know.
    /// </summary>
    /// <remarks>
    /// The Steam plugin that reads this ships separately and updates on its own schedule, so the
    /// two will be mismatched at some point. Better it says so plainly than misread a field and
    /// swap something nobody asked for. Bump on any change that removes or repurposes a field.
    /// </remarks>
    const int ContractVersion = 1;

    /// <summary>
    /// Borrows the calling terminal's console, when there is one to borrow.
    /// </summary>
    /// <remarks>
    /// This is built as a windows subsystem executable so that a caller with no
    /// console of its own - the Steam plugin's Lua host, a scheduled task - does not have one
    /// allocated on its behalf, which is a terminal flashing on screen for every call.
    ///
    /// The cost of that is no console when a person runs it themselves, so it attaches to the one
    /// its caller already has. Only when the output is not redirected: with a pipe in place the
    /// handles are the pipe's and must be left exactly as they are, or what the caller is reading
    /// gets rerouted to a window instead.
    /// </remarks>
    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);

    const int AttachParentProcess = -1;

    static async Task<int> Main(string[] args)
    {
        if (Console.IsOutputRedirected == false)
        {
            AttachConsole(AttachParentProcess);
        }

        try
        {
            var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

            if (command is "help" or "-h" or "--help")
            {
                return Write(new { ok = true, contractVersion = ContractVersion, usage = Usage() });
            }

            if (command is "version" or "--version")
            {
                return Write(new { ok = true, contractVersion = ContractVersion });
            }

            await LoadEverythingAsync();

            // Before anything that writes to a game folder or reads one to record it. A swap this
            // command line or the app was cut off in the middle of is put back first, as the app does
            // when it starts - somebody who only ever swaps from Steam may never open the app for it
            // to happen there. An operation another process still has in progress is left to it.
            if (command is "scan" or "swap" or "restore")
            {
                SwapExecutors.RecoverInterrupted();
            }

            return command switch
            {
                "list" => ListGames(args),
                "versions" => ListVersions(args),
                "scan" => await ScanAsync(args),
                "swap" => await SwapAsync(args),
                "restore" => await RestoreAsync(args),
                _ => Fail("Unknown command. Run help to see what there is."),
            };
        }
        catch (Exception err)
        {
            // Never a stack trace on stdout: the caller is parsing this. The inner messages are
            // kept though - a type initializer failure says nothing at all without them, and this
            // is the only channel a headless caller has.
            var messages = new List<string>();
            for (var current = err; current is not null; current = current.InnerException)
            {
                messages.Add(current.GetType().Name + ": " + current.Message);
            }

            // The stack goes to stderr, never stdout: stdout is the contract and a caller is
            // parsing it. Anyone debugging by hand still gets the whole thing.
            Console.Error.WriteLine(err.ToString());

            return Fail(string.Join(" -> ", messages));
        }
    }

    static string[] Usage()
    {
        return new[]
        {
            "list [--with-versions]",
            "versions [--type <type>]",
            "scan [--force]",
            "swap --game <id> --type <dlss|dlss_g|dlss_d|dlss_nr|xess|xell|...> --version <version> [--force]",
            "restore --game <id> [--type <type>] [--force]",
            "version",
        };
    }

    /// <summary>
    /// Brings up exactly what the app brings up before it can swap, minus the window.
    /// </summary>
    /// <remarks>
    /// The same three calls the app makes on startup, in the same order: the database, the
    /// manifests that say which versions exist, then the games from cache. Cache rather than a
    /// rescan on purpose - a rescan walks every install folder of every library, which is the app's
    /// job and not something a caller asking one question should pay for. It also means this
    /// reports the library as the app last saw it, which is the honest thing for it to report.
    /// </remarks>
    static async Task LoadEverythingAsync()
    {
        Database.Instance.Init();
        await DLLManager.Instance.LoadManifestsAsync();
        await GameManager.Instance.LoadGamesFromCacheAsync();
    }

    /// <summary>
    /// Looks at Steam again, and writes what it finds into the library the app reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Steam and nothing else, on purpose. Every other library scan reaches a different launcher's
    /// files - and one of them, Rockstar, installs copies that must never be swapped at all - so a
    /// command that quietly walked all of them would be doing more than its name says. This is the
    /// one a Steam client plugin needs, and it is the one it gets.
    /// </para>
    /// <para>
    /// It writes through the app's own library rather than into a copy: the games are saved to the
    /// same database the app loads at startup, so a game found here is a game the app has - which
    /// is what makes this worth having rather than a second, private list.
    /// </para>
    /// <para>
    /// Games somebody added to Steam themselves are included, because Steam plays them like any
    /// other and they hold the same dlls. See SteamShortcuts for how they are found and what is
    /// refused.
    /// </para>
    /// </remarks>
    static async Task<int> ScanAsync(string[] args)
    {
        var library = SteamLibrary.Instance;

        if (library.IsInstalled() == false)
        {
            return Fail("Steam does not appear to be installed, so there is nothing to scan.");
        }

        // Through the interface because that is where IsEnabled lives, as a default member.
        if (((IGameLibrary)library).IsEnabled == false)
        {
            return Fail("The Steam library is turned off in DLSS Swapper. Turn it back on in the app's settings, because scanning it anyway would ignore a choice already made.");
        }

        // Taken before the scan, because the scan is what changes it. Only Steam's, since only
        // Steam's are up for being added or removed here.
        var before = GameManager.Instance.GetGames<SteamGame>()
            .Select(x => x.ID)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // --force re-reads every game's folder rather than only the ones that look changed. Slow,
        // and the answer to "it should have found something and did not".
        var force = HasFlag(args, "--force");

        var found = await library.ListGamesAsync(force);

        foreach (var game in found)
        {
            GameManager.Instance.AddGame(game);
        }

        // Detection runs on the thread pool and the call above does not wait for it. Returning here
        // would report a game with none of its dlls found yet, and the process would exit mid write.
        var timedOut = await WaitForProcessingAsync(TimeSpan.FromMinutes(10)) == false;

        var foundIds = found.Select(x => x.ID).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = found
            .Where(x => before.Contains(x.ID) == false)
            .OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => new
            {
                id = x.ID,
                title = x.Title,
                installPath = x.InstallPath,
                // Named so a caller can tell which of the two kinds it got, since only one of them
                // has anything on the Steam store behind it.
                nonSteamShortcut = x is SteamGame steamGame && steamGame.IsNonSteamShortcut(),
                // Whether the walk found anything worth swapping. A game with nothing is still
                // reported, because "found it, there is nothing in it" is an answer.
                hasSwappableItems = x.HasSwappableItems,
            })
            .ToList();

        var removed = before.Where(x => foundIds.Contains(x) == false).OrderBy(x => x).ToList();

        return Write(new
        {
            ok = true,
            contractVersion = ContractVersion,
            scanned = "steam",
            forced = force,
            // True when detection was still running when this gave up waiting. The games are saved
            // either way; some may not have had their dlls recorded yet.
            incomplete = timedOut,
            games = found.Count,
            added = added,
            removed = removed,
        });
    }

    /// <summary>
    /// Waits for every game's dll detection to finish, returning false if it did not.
    /// </summary>
    /// <remarks>
    /// ProcessGame queues its walk onto the thread pool and returns immediately, setting Processing
    /// as it goes and clearing it in a finally. That flag is the only completion signal there is,
    /// so this watches it rather than guessing at a duration.
    /// </remarks>
    static async Task<bool> WaitForProcessingAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (GameManager.Instance.GetSynchronisedGamesListCopy().Any(x => x.Processing) == false)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    /// <summary>
    /// Every game, and with --with-versions everything needed to offer a choice for each.
    /// </summary>
    /// <remarks>
    /// The combined form exists because starting this process is the expensive part - a database
    /// and two manifests before it can answer anything - so a caller that needs both lists pays
    /// that twice for no reason. It also matters somewhere less obvious: on Windows every
    /// invocation from a windowless host flashes a console, and one flash is half of two.
    /// </remarks>
    static int ListGames(string[] args)
    {
        var games = GameManager.Instance.GetSynchronisedGamesListCopy()
            .OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(DescribeGame)
            .ToList();

        if (HasFlag(args, "--with-versions") == false)
        {
            return Write(new { ok = true, contractVersion = ContractVersion, games = games });
        }

        return Write(new
        {
            ok = true,
            contractVersion = ContractVersion,
            games = games,
            types = DescribeVersionsFor(DllTypes.All.Select(x => x.AssetType)),
        });
    }

    /// <summary>
    /// Every version of one dll type that could be swapped to.
    /// </summary>
    /// <remarks>
    /// For callers offering a choice rather than just "take the newest" - the picker in the app
    /// does, and anything embedding this should be able to. The curated note rides along, because
    /// a list of ninety version numbers with nothing to separate them is the problem that note was
    /// written to solve, and a caller cannot reconstruct it.
    ///
    /// Debug builds are included only when the app's own setting says to show them, so this cannot
    /// offer a file the app would hide.
    /// </remarks>
    static int ListVersions(string[] args)
    {
        var requested = Option(args, "--type");

        // No --type means every type, in one answer. Starting this process costs
        // a database and two manifests, so a caller that wants a choice for each
        // of ten dll types should not have to pay that ten times - and one that
        // needs the answer before a click, rather than after it, cannot.
        var assetTypes = new List<GameAssetType>();
        if (string.IsNullOrWhiteSpace(requested))
        {
            assetTypes.AddRange(DllTypes.All.Select(x => x.AssetType));
        }
        else
        {
            var assetType = ParseAssetType(requested, out var typeError);
            if (assetType is null)
            {
                return Fail(typeError);
            }

            assetTypes.Add(assetType.Value);
        }

        return Write(new
        {
            ok = true,
            contractVersion = ContractVersion,
            types = DescribeVersionsFor(assetTypes),
        });
    }

    /// <summary>The versions of each given type, in the shape both commands report.</summary>
    static List<object> DescribeVersionsFor(IEnumerable<GameAssetType> assetTypes)
    {
        var allowDebugDlls = Settings.Instance.AllowDebugDlls;
        var types = new List<object>();

        foreach (var assetType in assetTypes)
        {
            var versions = (DLLManager.Instance.GetRecords(assetType) ?? new ObservableCollection<DLLRecord>())
                .Where(x => allowDebugDlls || x.IsDevFile == false)
                .Select(x => new
                {
                    version = x.DisplayVersion,
                    name = x.DisplayName,
                    downloaded = x.LocalRecord?.IsDownloaded == true,
                    imported = x.LocalRecord?.IsImported == true,
                    recommended = x.IsRecommended,
                    note = x.RecommendationNote ?? string.Empty,
                })
                .ToList();

            types.Add(new
            {
                type = DllTypes.ForAssetType(assetType)?.ManifestKey ?? string.Empty,
                name = DLLManager.Instance.GetAssetTypeName(assetType),
                versions = versions,
            });
        }

        return types;
    }

    static object DescribeGame(Game game)
    {
        var dlls = new List<object>();

        foreach (var definition in DllTypes.All)
        {
            var installedAssets = game.GameAssets.Where(x => x.AssetType == definition.AssetType).ToList();
            if (installedAssets.Count == 0)
            {
                // A dll the game does not have is not a row. The app hides these too.
                continue;
            }

            var pin = game.DllPinFor(definition.AssetType);
            var first = installedAssets[0];

            dlls.Add(new
            {
                type = definition.ManifestKey,
                name = DLLManager.Instance.GetAssetTypeName(definition.AssetType),
                installed = first.DisplayVersion,
                newest = DLLManager.Instance.GetLatestRecord(definition.AssetType)?.DisplayVersion ?? string.Empty,

                // BehindAssetTypes, not OutdatedAssetTypes: a pinned dll is left out of the second
                // so batches skip it, but a caller still wants to be told a newer one exists.
                behind = game.BehindAssetTypes.Contains(definition.AssetType),
                pinned = pin is not null,
                pinReason = pin?.Reason ?? string.Empty,
                savedOriginal = SavedOriginalOf(game, definition, first),
                locations = installedAssets.Count,
            });
        }

        return new
        {
            id = game.ID,
            title = game.Title,
            library = game.GameLibrary.ToString(),
            installPath = game.InstallPath,
            skipUpdates = game.SkipUpdates,
            dlls = dlls,
        };
    }

    /// <summary>What restoring would put back, per path, the way the game page's row reads it.</summary>
    static string SavedOriginalOf(Game game, DllTypeDefinition definition, GameAsset installed)
    {
        var backupPath = DllSwapExecutor.GetBackupPath(installed.Path);

        return game.GameAssets
            .FirstOrDefault(x => x.AssetType == definition.BackupAssetType
                && string.Equals(x.Path, backupPath, StringComparison.OrdinalIgnoreCase))
            ?.DisplayVersion ?? string.Empty;
    }

    static async Task<int> SwapAsync(string[] args)
    {
        var game = FindGame(Option(args, "--game"), out var gameError);
        if (game is null)
        {
            return Fail(gameError);
        }

        var assetType = ParseAssetType(Option(args, "--type"), out var typeError);
        if (assetType is null)
        {
            return Fail(typeError);
        }

        // A pin means no batch moves this dll. The picker in the app may, because pressing it is a
        // deliberate act on one named file in front of you; a call arriving from a script or
        // another process is not, so it is refused unless the caller says it meant it.
        if (game.IsDllPinned(assetType.Value) && HasFlag(args, "--force") == false)
        {
            var pin = game.DllPinFor(assetType.Value);
            var because = string.IsNullOrEmpty(pin?.Reason) ? string.Empty : " Reason given: " + pin!.Reason;

            return Fail(DLLManager.Instance.GetAssetTypeName(assetType.Value) + " is pinned in " + game.Title + "."
                + because + " Pass --force to swap it anyway.");
        }

        var version = Option(args, "--version");
        if (string.IsNullOrWhiteSpace(version))
        {
            return Fail("--version is required. Run list to see what is installed.");
        }

        var records = DLLManager.Instance.GetRecords(assetType.Value);
        var record = records?.FirstOrDefault(x => x.Version == version || x.DisplayVersion == version);
        if (record is null)
        {
            return Fail("No " + DLLManager.Instance.GetAssetTypeName(assetType.Value) + " version " + version
                + " is known. Versions come from the manifest, or from importing the file in the app.");
        }

        // Downloading rather than refusing: the caller asked for a version, and "not on this
        // machine yet" is a step rather than an answer. The app's update run does the same.
        if (record.LocalRecord?.IsDownloaded == false)
        {
            var download = await record.DownloadAsync();
            if (download.Success == false)
            {
                return Fail(download.Cancelled ? "Download cancelled." : "Could not download it: " + download.Message);
            }
        }

        var result = await game.UpdateDllAsync(record);

        return Write(new
        {
            ok = result.Success,
            contractVersion = ContractVersion,
            game = game.Title,
            dll = DLLManager.Instance.GetAssetTypeName(assetType.Value),
            version = record.DisplayVersion,
            message = result.Message,
            failure = result.Failure == SwapFailure.None ? null : result.Failure.ToString(),
            needsAdmin = result.PromptToRelaunchAsAdmin,
        }, result.Success ? 0 : 1);
    }

    static async Task<int> RestoreAsync(string[] args)
    {
        var game = FindGame(Option(args, "--game"), out var gameError);
        if (game is null)
        {
            return Fail(gameError);
        }

        // No --type restores everything with a saved original, through the same list the app's
        // restore reads - so pins are honoured without this having to know about them.
        var assetTypes = new List<GameAssetType>();
        var requested = Option(args, "--type");
        if (string.IsNullOrWhiteSpace(requested))
        {
            assetTypes.AddRange(DllUpdateRunner.GetRevertableAssetTypes(game));
        }
        else
        {
            var assetType = ParseAssetType(requested, out var typeError);
            if (assetType is null)
            {
                return Fail(typeError);
            }

            assetTypes.Add(assetType.Value);
        }

        if (assetTypes.Count == 0)
        {
            return Fail("There are no saved originals to restore in " + game.Title + ".");
        }

        // Without --force a dll that has changed since the app last swapped it is left alone and
        // reported, the way the app asks before restoring over one. A script cannot be asked, so
        // the flag is how it answers in advance.
        var force = HasFlag(args, "--force");

        var restored = new List<object>();
        var allSucceeded = true;

        foreach (var assetType in assetTypes)
        {
            var result = await game.ResetDllAsync(assetType, restoreChangedFiles: force);
            allSucceeded = allSucceeded && result.Success;

            var message = result.Message;
            if (result.NeedsConfirmation)
            {
                message += " Pass --force to restore it anyway.";
            }

            restored.Add(new
            {
                dll = DLLManager.Instance.GetAssetTypeName(assetType),
                ok = result.Success,
                message = message,
                failure = result.Failure == SwapFailure.None ? null : result.Failure.ToString(),
                needsAdmin = result.PromptToRelaunchAsAdmin,
            });
        }

        return Write(new
        {
            ok = allSucceeded,
            contractVersion = ContractVersion,
            game = game.Title,
            restored = restored,
        }, allSucceeded ? 0 : 1);
    }

    static Game? FindGame(string? id, out string error)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            error = "--game is required. Run list to see the ids.";
            return null;
        }

        var games = GameManager.Instance.GetSynchronisedGamesListCopy();

        var game = games.FirstOrDefault(x => string.Equals(x.ID, id, StringComparison.OrdinalIgnoreCase));
        if (game is not null)
        {
            error = string.Empty;
            return game;
        }

        // Titles are what a person has to hand; ids are what list prints. Both work, but an
        // ambiguous title is refused rather than guessed at - the next thing this does is write
        // into a game folder.
        var byTitle = games.Where(x => string.Equals(x.Title, id, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (byTitle.Count == 1)
        {
            error = string.Empty;
            return byTitle[0];
        }

        error = byTitle.Count > 1
            ? id + " matches " + byTitle.Count + " games. Use the id from list instead."
            : "No game with id or title " + id + ".";

        return null;
    }

    /// <summary>Accepts the manifest key (dlss_g) or the enum name (DLSS_G).</summary>
    static GameAssetType? ParseAssetType(string? requested, out string error)
    {
        var known = string.Join(", ", DllTypes.All.Select(x => x.ManifestKey));

        if (string.IsNullOrWhiteSpace(requested))
        {
            error = "--type is required. One of: " + known;
            return null;
        }

        var definition = DllTypes.ForManifestKey(requested);
        if (definition is not null)
        {
            error = string.Empty;
            return definition.AssetType;
        }

        if (Enum.TryParse<GameAssetType>(requested, true, out var parsed) && DllTypes.ForAssetType(parsed) is not null)
        {
            error = string.Empty;
            return parsed;
        }

        error = requested + " is not a swappable dll type. One of: " + known;
        return null;
    }

    static string? Option(string[] args, string name)
    {
        var index = Array.FindIndex(args, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    static bool HasFlag(string[] args, string name)
    {
        return args.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
    }

    static int Fail(string message)
    {
        return Write(new { ok = false, contractVersion = ContractVersion, error = message }, 1);
    }

    static int Write(object payload, int exitCode = 0)
    {
        var options = new JsonSerializerOptions() { WriteIndented = true };
        Console.Out.Write(JsonSerializer.Serialize(payload, options));
        Console.Out.Write(Environment.NewLine);
        return exitCode;
    }
}
