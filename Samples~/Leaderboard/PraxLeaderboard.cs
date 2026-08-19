using System.Collections.Generic;
using System.Threading.Tasks;
using Praxsuite;
using UnityEngine;

/// <summary>
/// A public leaderboard, read directly and written through the server.
///
/// The split is the whole point. Reading the top ten is harmless, so the client does it in one
/// query. Writing a score is not harmless - a client that can write to the Scores table can
/// write 999999999 - so submission goes through a gateway endpoint where an automation you
/// control validates the claim before it writes anything.
///
/// Setup in the portal:
///   1. Table "Scores" with columns
///        Player (Enduser), PlayerName (Text), Score (Number), Level (Text)
///   2. API Gateway / Roles, "Player" role:
///        Scores -> Read  ONLY, no row filter (a leaderboard everyone can see)
///      Read-only is what makes this safe. Leave it at ReadWrite and the endpoint below is
///      decoration - a cheater simply writes the row directly.
///   3. Build an automation that:
///        - reads {{context.request.body.score}} and {{context.request.body.level}}
///        - sanity-checks the score against what that level allows
///        - identifies the player from the request's token claim, NOT from the payload
///        - inserts or updates their Scores row
///      Bind it to a Sync endpoint named "submit-score".
///
/// The third bullet matters most: the automation must take the player's identity from the
/// verified token, never from a field the client filled in. Trusting a playerId in the body
/// hands every player the ability to write scores as anyone.
/// </summary>
public class PraxLeaderboard : MonoBehaviour
{
    public class Entry
    {
        public string PlayerName;
        public int Score;
        public string Level;
    }

    private const string ScoresTable = "Scores";

    /// <summary>Reads the top entries. A direct query - reading is public.</summary>
    public async Task<List<Entry>> GetTopAsync(int count = 10, string level = null)
    {
        var query = Prax.Data.From(ScoresTable)
            .Select("PlayerName", "Score", "Level")
            .OrderByDescending("Score")
            .Limit(count);

        if (!string.IsNullOrEmpty(level)) query.Where("Level", level);

        return await query.ToListAsync<Entry>();
    }

    /// <summary>
    /// The signed-in player's rank, as a 1-based position.
    ///
    /// Counting the players ahead of them is one cheap query; fetching the whole board and
    /// searching it client-side would move megabytes to answer a single integer.
    /// </summary>
    public async Task<long> GetMyRankAsync()
    {
        var mine = await Prax.Data.From(ScoresTable)
            .Select("Score")
            .Where(PraxFilter.Eq("Player", Prax.Auth.CurrentUserId))
            .FirstAsync();

        if (mine == null) return 0;

        var ahead = await Prax.Data.From(ScoresTable)
            .Where(PraxFilter.Gt("Score", mine.GetInt("Score")))
            .CountAsync();

        return ahead + 1;
    }

    /// <summary>
    /// Submits a score through the server-authoritative endpoint.
    ///
    /// The player's identity is not in the payload - the SDK attaches their session token and
    /// the automation reads the claim. Anything the client says about who it is would be
    /// unverifiable.
    /// </summary>
    public async Task<bool> SubmitScoreAsync(int score, string level)
    {
        try
        {
            var response = await Prax.Endpoints.CallAsync("submit-score",
                new Dictionary<string, object>
                {
                    { "score", score },
                    { "level", level }
                });

            var row = PraxRowReader.ReadRow(response);

            // The automation is free to reject the submission; treat that as a normal outcome
            // rather than an error, and do not assume acceptance just because HTTP said 200.
            if (!row.GetBool("accepted", true))
            {
                Debug.LogWarning("Score rejected: " + row.GetString("reason", "no reason given"));
                return false;
            }

            return true;
        }
        catch (PraxException ex) when (ex.IsRateLimited)
        {
            // Submitting on every kill will hit this. Batch, or submit at end of run.
            Debug.LogWarning("Submitting too fast; try again shortly.");
            return false;
        }
        catch (PraxException ex)
        {
            Debug.LogWarning("Could not submit the score: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Pages through the whole board, for a full standings screen.
    ///
    /// Read the page's Limit rather than assuming yours was honoured: the gateway clamps limit
    /// to the table scope's cap, so a page size larger than that silently comes back smaller.
    /// </summary>
    public async Task<List<Entry>> GetPageAsync(int page, int pageSize = 25)
    {
        var result = await Prax.Data.From(ScoresTable)
            .Select("PlayerName", "Score", "Level")
            .OrderByDescending("Score")
            .Limit(pageSize)
            .Offset(page * pageSize)
            .WithTotalCount()
            .ToPageAsync();

        if (result.Limit < pageSize)
            Debug.Log("The server capped this page at " + result.Limit + " rows.");

        var entries = new List<Entry>(result.Rows.Count);
        foreach (var row in result.Rows) entries.Add(row.As<Entry>());
        return entries;
    }
}
