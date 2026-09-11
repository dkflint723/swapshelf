using System;

namespace DLSS_Swapper;

/// <summary>
/// The line of text on the loading screen, when there is a loading screen.
/// </summary>
/// <remarks>
/// <para>
/// The manifest migration reports its progress through this, and that migration runs in three
/// places: the app, where there is a window to write to; the tests; and the command line, where
/// there is not. It used to reach the window directly, which meant the whole load began by
/// dereferencing a null MainWindow anywhere but the app.
/// </para>
/// <para>
/// Two delegates rather than an interface, because there are exactly two operations and no state
/// worth naming a type for. Unset is the ordinary case outside the app: reading gives null and
/// writing goes nowhere, which is what "no loading screen" should do.
/// </para>
/// </remarks>
internal static class LoadingMessage
{
    /// <summary>Set by the app while it starts. Null everywhere else.</summary>
    internal static Func<string?>? Read { get; set; }

    /// <summary>Set by the app while it starts. Null everywhere else.</summary>
    /// <remarks>
    /// Takes a line of text, never null: the loading screen's text is not nullable, and the one
    /// caller already drops a null before it gets here. Typed as nullable, the app's setter was a
    /// warning on every build about an assignment that could not happen.
    /// </remarks>
    internal static Action<string>? Write { get; set; }
}
