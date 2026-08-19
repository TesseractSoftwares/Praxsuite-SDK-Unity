using System.Collections.Generic;
using System.Threading.Tasks;
using Praxsuite;
using UnityEngine;

/// <summary>
/// A shop, done so that the client cannot cheat it.
///
/// The temptation is obvious: read the player's coin balance, subtract the price, write both the
/// new balance and the item. Three lines, and completely broken - every one of those writes is
/// a value the client chose, so a trainer sets coins to a billion and grants every item.
///
/// The version below never writes anything valuable. The client asks to buy an item; a gateway
/// automation reads the real price, checks the real balance, deducts it and grants the item in
/// one server-side transaction, and answers with what happened. The client's only input is
/// which item it wants.
///
/// Setup in the portal:
///   1. Tables:
///        Wallets   - Owner (Enduser), Coins (Number)
///        Inventory - Owner (Enduser), ItemId (Text), Quantity (Number)
///        ShopItems - ItemId (Text), Name (Text), Price (Number)
///   2. API Gateway / Roles, "Player" role:
///        ShopItems -> Read, no row filter        (the catalogue is public)
///        Wallets   -> Read, row filter __SELF__  (see your own balance, cannot change it)
///        Inventory -> Read, row filter __SELF__  (see your own items, cannot grant them)
///      Every one of these is Read. The player role has no write access to anything with value.
///   3. Sync endpoint "purchase-item" bound to an automation that:
///        - identifies the player from the request token claim, never from the payload
///        - looks up the item's real price in ShopItems
///        - reads the player's real balance in Wallets
///        - refuses when the balance is short, and says so in the response
///        - otherwise deducts and grants, then returns the new balance
///
/// The division is the familiar one between a client API and trusted server-side logic. Here
/// that server-side half is an automation you build in the portal - there is no separate
/// function app to deploy.
/// </summary>
public class PraxShop : MonoBehaviour
{
    public class ShopItem
    {
        public string ItemId;
        public string Name;
        public int Price;
    }

    public class InventoryItem
    {
        public string ItemId;
        public int Quantity;
    }

    /// <summary>Reads the catalogue. Public data, so a direct query is fine.</summary>
    public async Task<List<ShopItem>> GetCatalogueAsync()
    {
        return await Prax.Data.From("ShopItems")
            .Select("ItemId", "Name", "Price")
            .OrderBy("Price")
            .ToListAsync<ShopItem>();
    }

    /// <summary>
    /// Reads the player's balance.
    ///
    /// This is a display value only. Never branch a purchase decision on it - the server checks
    /// the balance again when it processes the purchase, because this number arrived at a client
    /// that can edit it. Use it to grey out a button, not to authorise anything.
    /// </summary>
    public async Task<int> GetCoinsAsync()
    {
        var wallet = await Prax.Data.From("Wallets").Select("Coins").FirstAsync();
        return wallet?.GetInt("Coins") ?? 0;
    }

    /// <summary>Reads the player's own inventory. The row filter scopes it to them.</summary>
    public async Task<List<InventoryItem>> GetInventoryAsync()
    {
        return await Prax.Data.From("Inventory")
            .Select("ItemId", "Quantity")
            .ToListAsync<InventoryItem>();
    }

    public class PurchaseResult
    {
        public bool Success;
        public string Message;
        public int RemainingCoins;
    }

    /// <summary>
    /// Buys an item. The server decides everything; this method only reports the outcome.
    ///
    /// Note what is not sent: no price, no balance, no player id. Sending a price would let a
    /// client name its own; sending a balance would let it invent one; sending a player id would
    /// let it buy as someone else. The only thing the client knows better than the server is
    /// which item the player tapped.
    /// </summary>
    public async Task<PurchaseResult> BuyAsync(string itemId)
    {
        if (string.IsNullOrEmpty(itemId))
            return new PurchaseResult { Success = false, Message = "No item selected." };

        try
        {
            var response = await Prax.Endpoints.CallAsync("purchase-item",
                new Dictionary<string, object> { { "itemId", itemId } });

            var row = PraxRowReader.ReadRow(response);

            // Default to failure. A malformed or unexpected response must not read as a
            // successful purchase - that would be a free item on every server hiccup.
            return new PurchaseResult
            {
                Success = row.GetBool("success", false),
                Message = row.GetString("message", "The purchase could not be completed."),
                RemainingCoins = row.GetInt("remainingCoins", -1)
            };
        }
        catch (PraxException ex) when (ex.IsQuotaExceeded)
        {
            // The workspace is out of plan allowance. Retrying will not help; the owner has to
            // upgrade, so say something a player can act on and stop.
            Debug.LogError("The game's backend is over its usage limit: " + ex.Message);
            return new PurchaseResult
            {
                Success = false,
                Message = "The shop is temporarily unavailable. Please try again later."
            };
        }
        catch (PraxException ex)
        {
            Debug.LogWarning("Purchase failed (" + ex.Code + "): " + ex.Message);
            return new PurchaseResult
            {
                Success = false,
                Message = "The purchase could not be completed."
            };
        }
    }

    /// <summary>
    /// Records a gameplay event for analytics.
    ///
    /// Fire-and-forget: it never throws and never blocks, because a dropped telemetry event is
    /// not worth interrupting play for. Use an Async endpoint so the gateway acknowledges
    /// immediately and the automations run behind it.
    /// </summary>
    public void TrackShopOpened()
    {
        _ = Prax.Endpoints.FireAsync("game-telemetry", new Dictionary<string, object>
        {
            { "event", "shop_opened" },
            { "platform", Application.platform.ToString() },
            { "version", Application.version }
        });
    }
}
