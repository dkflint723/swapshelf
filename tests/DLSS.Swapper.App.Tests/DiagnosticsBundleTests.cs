using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DLSS_Swapper.Data;
using DLSS_Swapper.Data.ManuallyAdded;
using DLSS_Swapper.Diagnostics;
using DLSS_Swapper.Helpers;
using Xunit;

namespace DLSS_Swapper.App.Tests;

/// <summary>
/// The diagnostics text names games and stores, and not where they live.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DiagnosticsBundleTests
{
    static ManuallyAddedGame GameAt(string id, string title, string installPath)
    {
        return new ManuallyAddedGame(id) { Title = title, InstallPath = installPath };
    }

    [Fact]
    public async Task EachGameIsNamedAndEachLibraryRootIsNumberedPerStore()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var games = new List<Game>()
        {
            GameAt("d1", "Alan Wake 2", @"D:\SteamLibrary\steamapps\common\Alan Wake 2"),
            GameAt("d2", "Cyberpunk 2077", @"D:\SteamLibrary\steamapps\common\Cyberpunk 2077"),
            GameAt("d3", "Control", @"E:\Games\Control"),
            GameAt("d4", "No Path", ""),
        };

        var rules = DiagnosticsRedaction.Rules(games);
        var byPath = rules.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

        Assert.Equal("<Game: Alan Wake 2>", byPath[@"D:\SteamLibrary\steamapps\common\Alan Wake 2"]);
        Assert.Equal("<Game: Cyberpunk 2077>", byPath[@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077"]);
        Assert.Equal("<Game: Control>", byPath[@"E:\Games\Control"]);

        // Two games under one root share one library token; a second root gets the next number.
        Assert.Equal("<ManuallyAdded library 1>", byPath[@"D:\SteamLibrary\steamapps\common"]);
        Assert.Equal("<ManuallyAdded library 2>", byPath[@"E:\Games"]);

        // The app's own places and the profile.
        Assert.Equal("<Storage>", byPath[Storage.StoragePath]);
        Assert.Equal("<UserProfile>", byPath[Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)]);
        Assert.DoesNotContain(rules, x => string.IsNullOrWhiteSpace(x.Key));
    }

    [Fact]
    public async Task TheComposedBundleHidesPathsAndKeepsHashesWhenRedacted()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var install = @"D:\SteamLibrary\steamapps\common\Alan Wake 2";
        var rules = DiagnosticsRedaction.Rules(new[] { GameAt("b1", "Alan Wake 2", install) });

        var logSection = "```\n" + $"Swapped {install}\\nvngx_dlss.dll md5=2E3A0F8C2B1D4E5F6A7B8C9D0E1F2A3B\nStoragePath: {Storage.StoragePath}\n" + "```";

        var redacted = DiagnosticsBundle.Compose("```\nsystem\n```", "```\nlibrary\n```", "```\nmanifests\n```", logSection, rules);
        var raw = DiagnosticsBundle.Compose("```\nsystem\n```", "```\nlibrary\n```", "```\nmanifests\n```", logSection, null);

        Assert.DoesNotContain(install, redacted);
        Assert.DoesNotContain(Storage.StoragePath, redacted);
        Assert.Contains("<Game: Alan Wake 2>", redacted);
        Assert.Contains("<Storage>", redacted);
        Assert.Contains("2E3A0F8C2B1D4E5F6A7B8C9D0E1F2A3B", redacted);
        Assert.StartsWith(ResourceHelper.GetString("DiagnosticsPage_RedactedNote"), redacted);

        Assert.Contains(install, raw);
        Assert.DoesNotContain(ResourceHelper.GetString("DiagnosticsPage_RedactedNote"), raw);
    }

    [Fact]
    public async Task TheLogSectionIsTheTailOfTheFileAndCopesWithoutOne()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var logPath = Path.Combine(database.Root, "swapshelf_20260910.log");
        File.WriteAllLines(logPath, Enumerable.Range(1, 500).Select(i => $"line {i}"));

        var section = DiagnosticsBundle.LogSection(logPath);

        Assert.Contains("last 200 lines of swapshelf_20260910.log", section);
        Assert.DoesNotContain("line 300", section);
        Assert.Contains("line 301", section);
        Assert.Contains("line 500", section);

        var missing = DiagnosticsBundle.LogSection(Path.Combine(database.Root, "nope.log"));
        Assert.Contains("(no log file yet)", missing);
    }

    [Fact]
    public async Task TheLogCanBeReadWhileSomethingElseHoldsItOpenForWriting()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var logPath = Path.Combine(database.Root, "open.log");

        using (var writer = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("held open\n");
            writer.Write(bytes, 0, bytes.Length);
            writer.Flush();

            var section = DiagnosticsBundle.LogSection(logPath);
            Assert.Contains("held open", section);
        }
    }

    [Fact]
    public async Task TheManifestSectionSaysWhatIsOnDisk()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var section = DiagnosticsBundle.ManifestSection();

        Assert.Contains("Manifests", section);
        Assert.Contains("Manifest:", section);
        Assert.Contains("Imported:", section);
    }
}
