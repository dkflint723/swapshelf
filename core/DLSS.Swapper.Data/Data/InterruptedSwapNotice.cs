using System;
using System.Collections.Generic;
using DLSS_Swapper.Helpers;
using DLSS_Swapper.Swapping;

namespace DLSS_Swapper.Data;

/// <summary>
/// What the games page says about swaps the previous session did not finish.
/// </summary>
/// <remarks>
/// Said once, on the launch that put them back, because the games it names look untouched now and
/// nothing else in the app would mention it. A line per operation, each ending in whether the game
/// is as it was or needs a look.
/// </remarks>
public sealed class InterruptedSwapNotice
{
    public required string Title { get; init; }
    public required string Message { get; init; }

    public static InterruptedSwapNotice? For(RecoveryReport report)
    {
        if (report.IsEmpty)
        {
            return null;
        }

        var lines = new List<string>();
        foreach (var operation in report.Operations)
        {
            var game = string.IsNullOrWhiteSpace(operation.Record.GameTitle)
                ? ResourceHelper.GetString("InterruptedSwaps_UnknownGame")
                : operation.Record.GameTitle;

            lines.Add(operation.IsComplete
                ? ResourceHelper.GetFormattedResourceTemplate("InterruptedSwaps_LinePutBackTemplate", game)
                : ResourceHelper.GetFormattedResourceTemplate("InterruptedSwaps_LineIncompleteTemplate", game));
        }

        return new InterruptedSwapNotice()
        {
            Title = lines.Count == 1
                ? ResourceHelper.GetString("InterruptedSwaps_TitleOne")
                : ResourceHelper.GetFormattedResourceTemplate("InterruptedSwaps_TitleTemplate", lines.Count),
            Message = ResourceHelper.GetString("InterruptedSwaps_Body")
                + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine, lines),
        };
    }
}
