using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DLSS_Swapper.Data;
using DLSS_Swapper.Diagnostics;
using DLSS_Swapper.Dlls;

namespace DLSS_Swapper.Helpers;

/// <summary>
/// The text the diagnostics window shows and copies: system details, library summary, what the
/// manifests hold, and the tail of today's log - with paths replaced by placeholders unless asked.
/// </summary>
/// <remarks>
/// <para>
/// The window used to show system and library details alone, unredacted, and the log lived in a
/// folder nobody opened. A report about a swap that went wrong needs the log, and a report that
/// goes on a public tracker should not carry the user's account name or where they keep their
/// games. Redaction is the default; the checkbox that turns it off says what it will reveal.
/// </para>
/// <para>
/// Hashes and versions are never redacted. They are what a report is about.
/// </para>
/// </remarks>
internal static class DiagnosticsBundle
{
    internal const int LogLinesIncluded = 200;

    internal static string Build(bool redact)
    {
        var details = new SystemDetails();
        var games = GameManager.Instance.GetSynchronisedGamesListCopy();

        return Compose(
            details.GetSystemData(),
            details.GetLibraryData(),
            ManifestSection(),
            LogSection(Logger.GetCurrentLogPath()),
            redact ? DiagnosticsRedaction.Rules(games) : null);
    }

    /// <summary>The bundle from its parts. Rules null means unredacted.</summary>
    internal static string Compose(string systemData, string libraryData, string manifestSection, string logSection, IReadOnlyList<KeyValuePair<string, string>>? rules)
    {
        var text = string.Join("\n\n", new[] { systemData.TrimEnd(), libraryData.TrimEnd(), manifestSection.TrimEnd(), logSection.TrimEnd() }) + "\n";

        if (rules is null)
        {
            return text;
        }

        var note = ResourceHelper.GetString("DiagnosticsPage_RedactedNote");
        return note + "\n\n" + new Redactor(rules).Redact(text);
    }

    /// <summary>What each manifest holds and when it was last written, since neither carries a version of its own.</summary>
    internal static string ManifestSection()
    {
        var builder = new StringBuilder();
        builder.AppendLine("```");
        builder.AppendLine("Manifests");
        try
        {
            AppendManifest(builder, "Manifest", Storage.GetManifestPath(), DLLManager.Instance.Manifest);
            AppendManifest(builder, "Imported", Storage.GetImportedManifestPath(), DLLManager.Instance.ImportedManifest);
        }
        catch (Exception err)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"ERROR: {err.Message}");
        }
        finally
        {
            builder.AppendLine("```");
        }

        return builder.ToString();
    }

    static void AppendManifest(StringBuilder builder, string label, string path, Manifest? manifest)
    {
        var written = File.Exists(path)
            ? File.GetLastWriteTimeUtc(path).ToString("u", CultureInfo.InvariantCulture)
            : "not on disk";

        var counts = new List<string>();
        var total = 0;
        foreach (var definition in DllTypes.All)
        {
            var count = manifest?.GetRecords(definition.AssetType)?.Count ?? 0;
            if (count > 0)
            {
                counts.Add($"{definition.ManifestKey} {count}");
                total += count;
            }
        }

        var breakdown = counts.Count == 0 ? string.Empty : " (" + string.Join(", ", counts) + ")";
        builder.AppendLine(CultureInfo.InvariantCulture, $"{label}: written {written}, {total} versions{breakdown}");
    }

    /// <summary>The last lines of the log at <paramref name="logPath"/>, read beside the writer that still has it open.</summary>
    internal static string LogSection(string logPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("```");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Log: last {LogLinesIncluded} lines of {Path.GetFileName(logPath)}");
        try
        {
            if (File.Exists(logPath) == false)
            {
                builder.AppendLine("(no log file yet)");
            }
            else
            {
                var lines = new List<string>();
                using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string? line;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        lines.Add(line);
                    }
                }

                foreach (var line in lines.Skip(Math.Max(0, lines.Count - LogLinesIncluded)))
                {
                    builder.AppendLine(line);
                }
            }
        }
        catch (Exception err)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"ERROR: {err.Message}");
        }
        finally
        {
            builder.AppendLine("```");
        }

        return builder.ToString();
    }
}

/// <summary>
/// Which paths a diagnostics dump replaces, and with what.
/// </summary>
/// <remarks>
/// Each game's install folder becomes its title, so a line stays readable; the folder above it -
/// the library root - is numbered per store, so two games in one library visibly share one. The
/// app's own folders and the profile folder get names of their own. Titles are kept: they are what
/// a report is about, and they are not where anything is.
/// </remarks>
internal static class DiagnosticsRedaction
{
    internal static IReadOnlyList<KeyValuePair<string, string>> Rules(IEnumerable<Game> games)
    {
        var rules = new List<KeyValuePair<string, string>>();
        var libraryRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var perLibrary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in games)
        {
            var install = Redactor.TrimSeparators(game.InstallPath ?? string.Empty);
            if (string.IsNullOrWhiteSpace(install))
            {
                continue;
            }

            rules.Add(new KeyValuePair<string, string>(install, $"<Game: {game.Title}>"));

            var root = Path.GetDirectoryName(install);
            if (string.IsNullOrWhiteSpace(root) || libraryRoots.ContainsKey(root))
            {
                continue;
            }

            var library = game.GameLibrary.ToString();
            perLibrary[library] = perLibrary.TryGetValue(library, out var seen) ? seen + 1 : 1;
            libraryRoots[root] = $"<{library} library {perLibrary[library]}>";
        }

        foreach (var (root, token) in libraryRoots)
        {
            rules.Add(new KeyValuePair<string, string>(root, token));
        }

        rules.Add(new KeyValuePair<string, string>(Storage.StoragePath, "<Storage>"));
        rules.Add(new KeyValuePair<string, string>(Storage.GetTemp(), "<Temp>"));
        rules.Add(new KeyValuePair<string, string>(AppDomain.CurrentDomain.BaseDirectory, "<App>"));
        rules.Add(new KeyValuePair<string, string>(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<UserProfile>"));

        return rules;
    }
}
