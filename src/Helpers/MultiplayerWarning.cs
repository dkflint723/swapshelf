using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DLSS_Swapper.Data;
using DLSS_Swapper.UserControls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DLSS_Swapper.Helpers;

/// <summary>
/// The note that anti-cheat can object to swapped dlls - per game, at the moment it matters, and
/// with a way to say no.
/// </summary>
/// <remarks>
/// <para>
/// It used to show once per installation, before the first swap of any game, and then never again.
/// By the time it applied to a multiplayer game carrying an anti-cheat it had been dismissed weeks
/// earlier over a single-player one, unread. Now each game is asked about once; the dialog names
/// the games it is about and the anti-cheat any of them carries; and cancelling it cancels the swap.
/// A game that gains an anti-cheat after being acknowledged is asked about again, with the name.
/// </para>
/// <para>
/// Cancel is the default button. The safe answer is the one that happens on Enter.
/// </para>
/// <para>
/// The covered entry points, so a future write path knows to call this too: the batch runner
/// (GameGridPageModel.RunUpdateBatchAsync), which every update route funnels into, and the per-dll
/// picker (GameControlModel.ChangeRecordAsync), gated BEFORE the picker dialog opens because WinUI
/// allows one ContentDialog per root. Backup and restore paths put originals back and need no gate.
/// </para>
/// </remarks>
internal static class MultiplayerWarning
{
    /// <returns>True to proceed. False means the user declined, and nothing may be written.</returns>
    internal static async Task<bool> EnsureAcknowledgedAsync(XamlRoot xamlRoot, IReadOnlyList<Game> games)
    {
        var pending = RiskAcknowledgement.GamesNeedingAcknowledgement(games);
        if (pending.Count == 0)
        {
            return true;
        }

        var panel = new StackPanel() { Spacing = 10 };
        panel.Children.Add(new TextBlock()
        {
            Text = ResourceHelper.GetString("MainWindow_NoteForMultiplayerGames_Message"),
            TextWrapping = TextWrapping.Wrap,
        });

        var list = new StackPanel() { Spacing = 2 };
        list.Children.Add(new TextBlock()
        {
            Text = ResourceHelper.GetString("RiskAck_AppliesTo"),
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var game in pending)
        {
            list.Children.Add(new TextBlock()
            {
                Text = game.AntiCheat is null
                    ? game.Title
                    : ResourceHelper.GetFormattedResourceTemplate("RiskAck_GameWithAntiCheatTemplate", game.Title, game.AntiCheat),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 0, 0, 0),
            });
        }

        panel.Children.Add(list);

        panel.Children.Add(new TextBlock()
        {
            Text = ResourceHelper.GetString("RiskAck_PlayCleanHint"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        });

        var dialog = new EasyContentDialog(xamlRoot)
        {
            Title = ResourceHelper.GetString("MainWindow_NoteForMultiplayerGames_Title"),
            Content = panel,
            PrimaryButtonText = ResourceHelper.GetString("RiskAck_Continue"),
            CloseButtonText = ResourceHelper.GetString("General_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return false;
        }

        await RiskAcknowledgement.AcknowledgeAsync(pending);
        return true;
    }
}
