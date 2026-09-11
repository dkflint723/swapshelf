using System;
using System.IO;
using Microsoft.Win32;

namespace DLSS_Swapper.Helpers;

/// <summary>
/// Keeps the version Add or remove programs shows in step with the installed copy that is running.
/// </summary>
/// <remarks>
/// <para>
/// The installer writes DisplayVersion into this entry, and the entry can still be wrong afterwards.
/// A program running as a Windows app package gives the processes it starts a private overlay of the
/// current user's registry, and an installer started from inside one writes its entry into that
/// overlay: the real entry never hears about the install. That is how this was found, the long way
/// round. The environment these releases are built in is such a package; every check made from it read
/// the overlay's stale copy, and for two weeks it looked as though the installer's write did not stick
/// - 3.0.5.0's notes blamed a running app - while the real entry was right. So the installed copy puts
/// the real entry right when it starts, which covers an install started from anywhere.
/// </para>
/// <para>
/// Only the installed copy corrects it. A build started from anywhere else - a development build, a
/// copy unzipped beside the installed one - would otherwise stamp its own version on the entry for a
/// program it is not. The app already keeps EstimatedSize in this same key.
/// </para>
/// </remarks>
internal static class UninstallEntry
{
    internal const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Swapshelf";

    /// <summary>Left beside a correction, so the next person looking at this key can see one happened.</summary>
    internal const string CorrectedValueName = "SwapshelfDisplayVersionCorrected";

    /// <returns>The version it replaced - empty when there was none - or null when nothing needed changing.</returns>
    internal static string? KeepVersionCurrent(RegistryKey key, string runningDirectory, string currentVersion)
    {
        if (key.GetValue("InstallLocation") is not string installLocation || string.IsNullOrWhiteSpace(installLocation))
        {
            return null;
        }

        if (SameFolder(installLocation, runningDirectory) == false)
        {
            return null;
        }

        var recorded = key.GetValue("DisplayVersion") as string;
        if (string.Equals(recorded, currentVersion, StringComparison.Ordinal))
        {
            return null;
        }

        key.SetValue("DisplayVersion", currentVersion, RegistryValueKind.String);
        key.SetValue(CorrectedValueName, $"{recorded ?? "(none)"} -> {currentVersion} at {DateTime.UtcNow:u}", RegistryValueKind.String);
        return recorded ?? string.Empty;
    }

    static bool SameFolder(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(first).TrimEnd('\\', '/'),
                Path.GetFullPath(second).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
