using System.Collections.Generic;
using Praxsuite;
using UnityEngine;

/// <summary>
/// The smallest complete Praxsuite integration: sign a player in, load their save, write it back.
///
/// Setup in the portal, once:
///   1. Create a table "PlayerSaves" with columns
///        Owner  (Enduser)   &lt;- what makes the row belong to a player
///        Level  (Number)
///        Coins  (Number)
///   2. API Gateway / Roles: create a "Player" role and scope it to PlayerSaves (ReadWrite).
///   3. On that table scope, set the row filter to __SELF__.
///   4. On the Owner column's scope, set the default value template to {{claim:sub}}.
///   5. Assign the role to your end users.
///
/// Steps 3 and 4 are both required, and they do different jobs. Getting only one is the
/// common mistake:
///
///   - The row filter (3) scopes SELECT, UPDATE and DELETE to rows the player already owns.
///     It does nothing for INSERT, because an insert has no WHERE clause to filter.
///   - The default value template (4) is what stamps Owner on insert. The gateway resolves
///     {{claim:sub}} from the caller's verified token, and - importantly - a column with a
///     default template CANNOT be set by the client at all: supplying it is rejected outright.
///     That is what stops a modified build inserting a row owned by someone else.
///
/// Configure only the row filter and new rows are written with a NULL owner, which the filter
/// then excludes - so a player saves successfully and cannot read their own save back.
/// </summary>
public class PraxQuickStart : MonoBehaviour
{
    [Header("Test credentials")]
    [SerializeField] private string email = "player@example.com";
    [SerializeField] private string password = "";

    private const string SaveTable = "PlayerSaves";

    private async void Start()
    {
        // Optional, but it turns a bad workspace id or host into one clear error here instead
        // of a confusing one during sign-in.
        var workspace = await Prax.InitializeAsync();
        Debug.Log("Connected to " + workspace.WorkspaceName);

        // A stored session survives app restarts, so returning players skip the sign-in screen.
        if (!Prax.Auth.IsSignedIn)
        {
            if (!await SignInAsync()) return;
        }

        Debug.Log("Playing as " + Prax.Auth.CurrentUser.DisplayName);

        var save = await LoadOrCreateSaveAsync();
        Debug.Log("Level " + save.GetInt("Level") + ", " + save.GetInt("Coins") + " coins");

        // Simulate progress and persist it.
        await SaveAsync(save.Id, save.GetInt("Level") + 1, save.GetInt("Coins") + 25);
    }

    private async System.Threading.Tasks.Task<bool> SignInAsync()
    {
        try
        {
            var result = await Prax.Auth.LoginAsync(email, password);

            if (result.RequiresEmailConfirmation)
            {
                // The account exists but is unusable until the link is clicked. Show this
                // rather than a generic failure, or players will keep retrying a correct password.
                Debug.LogWarning("Confirm your email address before signing in.");
                return false;
            }

            return result.IsSignedIn;
        }
        catch (PraxException ex) when (ex.IsAuthFailure)
        {
            Debug.LogWarning("Wrong email or password.");
            return false;
        }
        catch (PraxException ex) when (ex.IsNetworkError)
        {
            // The SDK already retried with backoff, so reaching here means it is really offline.
            Debug.LogWarning("No connection. Try again once you are online.");
            return false;
        }
    }

    /// <summary>
    /// Loads this player's save, creating one on first run.
    ///
    /// Note the absence of a "where Owner = me" clause: the role's row filter supplies it. That
    /// is not a shortcut, it is the security model - a filter the client writes is a filter the
    /// client can remove.
    /// </summary>
    private async System.Threading.Tasks.Task<PraxRow> LoadOrCreateSaveAsync()
    {
        var existing = await Prax.Data.From(SaveTable).FirstAsync();
        if (existing != null) return existing;

        Debug.Log("First run - creating a save.");

        var created = await Prax.Data.InsertAsync(SaveTable, new Dictionary<string, object>
        {
            { "Level", 1 },
            { "Coins", 0 }
            // Owner is deliberately absent. With a {{claim:sub}} default template on that
            // column's scope, the gateway stamps it from the caller's verified token and
            // rejects any attempt by the client to supply it - so ownership is not something
            // this build can influence, whatever a cheater edits.
        });

        return created.Row;
    }

    private async System.Threading.Tasks.Task SaveAsync(string rowId, int level, int coins)
    {
        await Prax.Data.UpdateByIdAsync(SaveTable, rowId, new Dictionary<string, object>
        {
            { "Level", level },
            { "Coins", coins }
        });

        Debug.Log("Saved: level " + level + ", " + coins + " coins");
    }
}
