using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DLSS_Swapper.Diagnostics;

/// <summary>
/// Replaces the paths in a piece of text with stable placeholders.
/// </summary>
/// <remarks>
/// <para>
/// A diagnostics dump is written to be pasted into a bug report. Every path in it says where the
/// user keeps their games and, through the profile folder, what their Windows account is called -
/// none of which the person reading the report needs. The placeholders keep what does matter: which
/// game a line is about, and that two lines are about the same folder.
/// </para>
/// <para>
/// Longest path first, so a folder inside another gets its own name rather than its parent's, and
/// case-insensitive with either separator, because Windows is and the log is not consistent. Only
/// paths are touched: a hash or a version is never a path, so they come through untouched by
/// construction rather than by exception.
/// </para>
/// </remarks>
public sealed class Redactor
{
    readonly List<(Regex Pattern, string Token)> _rules;

    /// <param name="pathTokens">Paths and the placeholder each becomes. Empty or whitespace paths are ignored.</param>
    public Redactor(IEnumerable<KeyValuePair<string, string>> pathTokens)
    {
        _rules = pathTokens
            .Select(x => (Path: TrimSeparators(x.Key), x.Value))
            .Where(x => string.IsNullOrWhiteSpace(x.Path) == false)
            .OrderByDescending(x => x.Path.Length)
            .Select(x => (PatternFor(x.Path), x.Value))
            .ToList();
    }

    public int RuleCount => _rules.Count;

    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        foreach (var (pattern, token) in _rules)
        {
            text = pattern.Replace(text, token);
        }

        return text;
    }

    /// <summary>A path without its trailing separators, so "C:\Games\" and "C:\Games" are one rule.</summary>
    public static string TrimSeparators(string path)
    {
        return string.IsNullOrWhiteSpace(path) ? string.Empty : path.TrimEnd('\\', '/');
    }

    static Regex PatternFor(string path)
    {
        // Each separator in the path matches either kind in the text.
        var parts = path.Split(new[] { '\\', '/' });
        var pattern = string.Join(@"[\\/]", parts.Select(Regex.Escape));
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
