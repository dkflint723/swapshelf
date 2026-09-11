using System;
using System.Reflection;

namespace DLSS_Swapper.Helpers;

/// <summary>
/// The version of the program that is running - the app or the command line - rather than of this library.
/// </summary>
/// <remarks>
/// Assembly.GetExecutingAssembly() in this project is DLSS.Swapper.Data, which is versioned 1.0.0. So
/// everything here that asked it for "the version" - the diagnostics text and the user agent sent to
/// GitHub - has said 1.0.0.0 since the data layer moved out of the app, and a diagnostics bundle pasted
/// into an issue named the wrong release. The entry assembly is the program that was started, and it
/// carries the release's number.
/// </remarks>
internal static class AppVersion
{
    internal static Version Current => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>Four parts, the way the installer writes it into Add or remove programs.</summary>
    internal static string Display
    {
        get
        {
            var version = Current;
            return $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}.{Math.Max(version.Revision, 0)}";
        }
    }
}
