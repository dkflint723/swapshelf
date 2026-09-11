using System;
using System.Collections.Generic;
using DLSS_Swapper.Data;
using DLSS_Swapper.Helpers;
using DLSS_Swapper.Swapping;
using Xunit;

namespace DLSS_Swapper.App.Tests;

/// <summary>
/// The sentence the games page shows after putting an interrupted swap back.
/// </summary>
public class InterruptedSwapNoticeTests
{
    static RecoveredOperation Operation(string? title, params string[] warnings)
    {
        return new RecoveredOperation()
        {
            Record = new OperationRecord() { Id = Guid.NewGuid().ToString("N"), Kind = OperationKind.Swap, GameId = "g", GameTitle = title },
            RestoredPaths = Array.Empty<string>(),
            Warnings = warnings,
        };
    }

    [Fact]
    public void NothingRecoveredMeansNoNotice()
    {
        Assert.Null(InterruptedSwapNotice.For(new RecoveryReport() { Operations = Array.Empty<RecoveredOperation>() }));
    }

    [Fact]
    public void OneOperationNamesTheGameAndSaysItWasPutBack()
    {
        var notice = InterruptedSwapNotice.For(new RecoveryReport() { Operations = new List<RecoveredOperation>() { Operation("Alan Wake 2") } });

        Assert.NotNull(notice);
        Assert.Equal(ResourceHelper.GetString("InterruptedSwaps_TitleOne"), notice.Title);
        Assert.Contains(ResourceHelper.GetFormattedResourceTemplate("InterruptedSwaps_LinePutBackTemplate", "Alan Wake 2"), notice.Message);
        Assert.StartsWith(ResourceHelper.GetString("InterruptedSwaps_Body"), notice.Message);
    }

    [Fact]
    public void SeveralOperationsAreCountedAndOneThatCouldNotBePutBackSaysSo()
    {
        var notice = InterruptedSwapNotice.For(new RecoveryReport()
        {
            Operations = new List<RecoveredOperation>() { Operation("Alan Wake 2"), Operation("Cyberpunk 2077", "Could not restore something") },
        });

        Assert.NotNull(notice);
        Assert.Equal(ResourceHelper.GetFormattedResourceTemplate("InterruptedSwaps_TitleTemplate", 2), notice.Title);
        Assert.Contains(ResourceHelper.GetFormattedResourceTemplate("InterruptedSwaps_LineIncompleteTemplate", "Cyberpunk 2077"), notice.Message);
    }

    [Fact]
    public void AGameWithoutATitleStillGetsALine()
    {
        var notice = InterruptedSwapNotice.For(new RecoveryReport() { Operations = new List<RecoveredOperation>() { Operation(null) } });

        Assert.NotNull(notice);
        Assert.Contains(ResourceHelper.GetString("InterruptedSwaps_UnknownGame"), notice.Message);
    }
}
