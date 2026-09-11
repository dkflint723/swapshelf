using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DLSS_Swapper.Data;

/// <summary>
/// Which games still need the anti-cheat note, and recording that they have had it.
/// </summary>
/// <remarks>
/// Pure over the games it is given, so the decision can be tested without a dialog. The dialog
/// itself lives in the app project; this is the rule it asks.
/// </remarks>
internal static class RiskAcknowledgement
{
    /// <summary>The games in <paramref name="games"/> that have not been acknowledged, once each, by title.</summary>
    internal static List<Game> GamesNeedingAcknowledgement(IEnumerable<Game> games)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<Game>();

        foreach (var game in games)
        {
            if (game is null || game.RiskAcknowledgedAt is not null)
            {
                continue;
            }

            if (seen.Add(game.ID))
            {
                pending.Add(game);
            }
        }

        return pending.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Records that the user said "I understand" for each game, and saves it.</summary>
    internal static async Task AcknowledgeAsync(IEnumerable<Game> games)
    {
        var now = DateTime.UtcNow;

        foreach (var game in games)
        {
            game.RiskAcknowledgedAt = now;
            await game.SaveToDatabaseAsync().ConfigureAwait(false);
        }
    }
}
