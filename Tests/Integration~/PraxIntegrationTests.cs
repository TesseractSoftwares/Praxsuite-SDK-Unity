using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using NUnit.Framework;
using Praxsuite;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

/// <summary>
/// Drives the SDK against the real Praxsuite gateway from inside Unity.
///
/// This covers what the offline unit suite cannot: UnityWebRequest transport, main-thread
/// marshalling after an await, the dispatcher, real auth, and the actual wire contract.
/// </summary>
public class PraxIntegrationTests
{
    // Point these at your own workspace and a throwaway end user before running.
    //
    // Never commit real values here. A workspace GUID is all anyone needs to fetch that
    // workspace's publishable key from /auth/config - the endpoint is deliberately
    // unauthenticated - so a GUID plus a working password in a public repository hands over
    // everything that key and that user can reach.
    //
    // Prefer environment variables for anything you would not paste into a pull request:
    //   PRAX_TEST_WORKSPACE, PRAX_TEST_HOST, PRAX_TEST_EMAIL, PRAX_TEST_PASSWORD
    private static string Env(string key, string fallback) =>
        System.Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

    private static readonly string Workspace = Env("PRAX_TEST_WORKSPACE", "00000000-0000-0000-0000-000000000000");
    private static readonly string Host = Env("PRAX_TEST_HOST", PraxRoutes.CloudHost);
    private static readonly string Email = Env("PRAX_TEST_EMAIL", "test-player@example.com");
    private static readonly string Password = Env("PRAX_TEST_PASSWORD", "");

    private const string SaveTable = "UNITY_PlayerSaves";
    private const string ScoreTable = "UNITY_Scores";

    private static string _runTag;

    [OneTimeSetUp]
    public void Configure()
    {
        // Fail with a usable message rather than a wall of 401s.
        if (string.IsNullOrEmpty(Password))
            Assert.Ignore("Set PRAX_TEST_WORKSPACE / PRAX_TEST_EMAIL / PRAX_TEST_PASSWORD to run the live suite.");

        _runTag = "run-" + DateTime.UtcNow.Ticks;

        PraxsuiteClient.Reset();
        PraxsuiteClient.Configure(new PraxsuiteOptions
        {
            WorkspaceId = Workspace,
            BaseUrl = Host,
            // Deliberately left null: this exercises publishable-key auto-discovery, which is
            // the whole "Workspace ID is the only setting" claim.
            PublishableKey = null,
            VerboseLogging = true,
            PersistSession = false,   // keep test runs independent of each other
            TimeoutSeconds = 30
        });
    }

    /// <summary>Runs a Task inside a UnityTest and rethrows failures with a usable stack.</summary>
    private static IEnumerator Await(Task task)
    {
        while (!task.IsCompleted) yield return null;
        if (task.IsFaulted) throw task.Exception.GetBaseException();
    }

    // ------------------------------------------------------------------ 01 config

    [UnityTest]
    public IEnumerator T01_FetchesWorkspaceConfigWithoutAnyKey()
    {
        var task = Prax.Auth.GetWorkspaceConfigAsync();
        yield return Await(task);

        var config = task.Result;
        Debug.Log($"[T01] workspace='{config.WorkspaceName}' key={PraxKeyGuard.Redact(config.PublishableKey)} " +
                  $"lang={config.DefaultLanguage} confirmEmail={config.RequireEmailConfirmation}");

        Assert.IsNotNull(config.PublishableKey, "auto-discovery returned no publishable key");
        StringAssert.StartsWith("pk_live_", config.PublishableKey);
        Assert.IsNotEmpty(config.WorkspaceName);
    }

    // ------------------------------------------------------------------- 02 login

    [UnityTest]
    public IEnumerator T02_SignsInAndExposesTheUser()
    {
        var task = Prax.Auth.LoginAsync(Email, Password);
        yield return Await(task);

        var result = task.Result;
        Debug.Log($"[T02] signedIn={result.IsSignedIn} user={result.User?.DisplayName} " +
                  $"id={result.User?.Id} roles=[{string.Join(",", result.User?.Roles ?? new string[0])}]");

        Assert.IsTrue(result.IsSignedIn, "login did not produce a session");
        Assert.IsTrue(Prax.Auth.IsSignedIn);
        Assert.IsNotNull(Prax.Auth.CurrentUserId, "no end user id on the session");
        Assert.AreEqual(Email, Prax.Auth.CurrentUser.Email);

        // The role drives every table scope, so losing it here would silently explain later 403s.
        CollectionAssert.Contains(Prax.Auth.CurrentUser.Roles, "UnityPlayer");
    }

    // ------------------------------------------------------------------ 03 schema

    [UnityTest]
    public IEnumerator T03_ResolvesTableNamesFromSchema()
    {
        var task = Prax.Schema.FetchAsync(true);
        yield return Await(task);

        var names = new List<string>();
        foreach (var t in Prax.Schema.Tables) names.Add(t.Name);
        Debug.Log($"[T03] visible tables: {string.Join(", ", names)}");

        Assert.IsTrue(Prax.Schema.Has(SaveTable), $"{SaveTable} not visible to this role");
        Assert.IsTrue(Prax.Schema.Has(ScoreTable), $"{ScoreTable} not visible to this role");

        // The Enduser column is what a __SELF__ row filter binds to.
        var saves = Prax.Schema.Describe(SaveTable);
        Assert.IsNotNull(saves, "no metadata for the saves table");
        var columnDesc = new List<string>();
        foreach (var c in saves.Columns) columnDesc.Add($"{c.Name}:{c.Type}");
        Debug.Log($"[T03] {SaveTable} columns: {string.Join(", ", columnDesc)}");
        Assert.IsNotNull(saves.EndUserColumn, "saves table has no Enduser column");
    }

    // ------------------------------------------------------------------ 04 insert

    private static string _saveRowId;

    [UnityTest]
    public IEnumerator T04_InsertsARow()
    {
        var task = Prax.Data.InsertAsync(SaveTable, new Dictionary<string, object>
        {
            { "SaveKey", _runTag + "-save" },
            { "Level", 1 },
            { "Coins", 100 },
            { "DisplayName", "Unity Tester" }
        });
        yield return Await(task);

        var result = task.Result;
        Debug.Log($"[T04] affected={result.AffectedRows} rowId={result.Row?.Id}");

        Assert.AreEqual(1, result.AffectedRows);
        Assert.IsNotNull(result.Row, "insert with returning=true gave no row back");

        _saveRowId = result.Row.Id;
        Assert.IsNotNull(_saveRowId, "inserted row has no ID column");
    }

    // ------------------------------------------------------------------- 05 query

    [UnityTest]
    public IEnumerator T05_ReadsTheRowBack()
    {
        var task = Prax.Data.From(SaveTable)
            .Where(PraxFilter.Eq("SaveKey", _runTag + "-save"))
            .FirstAsync();
        yield return Await(task);

        var row = task.Result;
        Assert.IsNotNull(row, "the row just inserted could not be read back");
        Debug.Log($"[T05] level={row.GetInt("Level")} coins={row.GetInt("Coins")} " +
                  $"name={row.GetString("DisplayName")} owner={row.GetString("Owner")}");

        Assert.AreEqual(1, row.GetInt("Level"));
        Assert.AreEqual(100, row.GetInt("Coins"));
        Assert.AreEqual("Unity Tester", row.GetString("DisplayName"));
    }

    // ------------------------------------------------------------------ 06 update

    [UnityTest]
    public IEnumerator T06_UpdatesTheRow()
    {
        var update = Prax.Data.UpdateByIdAsync(SaveTable, _saveRowId, new Dictionary<string, object>
        {
            { "Level", 7 },
            { "Coins", 4321 }
        });
        yield return Await(update);
        Debug.Log($"[T06] affected={update.Result.AffectedRows}");
        Assert.AreEqual(1, update.Result.AffectedRows);

        var reread = Prax.Data.GetAsync(SaveTable, _saveRowId);
        yield return Await(reread);

        Assert.IsNotNull(reread.Result, "row vanished after update");
        Assert.AreEqual(7, reread.Result.GetInt("Level"));
        Assert.AreEqual(4321, reread.Result.GetInt("Coins"));
    }

    // ------------------------------------------------- 07 bulk insert + loop + page

    [UnityTest]
    public IEnumerator T07_BulkInsertsThenPagesAndLoops()
    {
        // One request for many rows - the thing a per-row loop would get wrong.
        var rows = new List<IDictionary<string, object>>();
        for (var i = 1; i <= 12; i++)
        {
            rows.Add(new Dictionary<string, object>
            {
                { "SaveKey", $"{_runTag}-bulk-{i:D2}" },
                { "Level", i },
                { "Coins", i * 100 },
                { "DisplayName", $"Bulk {i:D2}" }
            });
        }

        var insert = Prax.Data.InsertManyAsync(SaveTable, rows);
        yield return Await(insert);
        Debug.Log($"[T07] bulk inserted affected={insert.Result.AffectedRows}");
        Assert.AreEqual(12, insert.Result.AffectedRows);

        // Filter + order + limit, then iterate the results.
        var query = Prax.Data.From(SaveTable)
            .Select("SaveKey", "Level", "Coins")
            .Where(PraxFilter.Gte("Level", 5), PraxFilter.Lte("Level", 10))
            .OrderByDescending("Level")
            .Limit(5)
            .WithTotalCount()
            .ToPageAsync();
        yield return Await(query);

        var page = query.Result;
        Debug.Log($"[T07] page count={page.Count} limit={page.Limit} total={page.Total} " +
                  $"durationMs={page.DurationMs} hasMore={page.HasMore}");

        Assert.AreEqual(5, page.Rows.Count, "limit was not applied");
        Assert.IsTrue(page.Total.HasValue, "includeTotalCount produced no total");

        var previous = int.MaxValue;
        foreach (var row in page.Rows)
        {
            var level = row.GetInt("Level");
            Debug.Log($"[T07]   {row.GetString("SaveKey")} level={level} coins={row.GetInt("Coins")}");
            Assert.LessOrEqual(level, previous, "rows came back out of descending order");
            Assert.GreaterOrEqual(level, 5);
            Assert.LessOrEqual(level, 10);
            previous = level;
        }

        // Paging: the second page must not repeat the first.
        var pageTwo = Prax.Data.From(SaveTable)
            .Select("SaveKey")
            .Where(PraxFilter.Gte("Level", 5), PraxFilter.Lte("Level", 10))
            .OrderByDescending("Level")
            .Limit(3).Offset(3)
            .ToListAsync();
        yield return Await(pageTwo);
        Debug.Log($"[T07] page two returned {pageTwo.Result.Count} rows");
        Assert.AreEqual(3, pageTwo.Result.Count);
    }

    // -------------------------------------------------------------------- 08 count

    [UnityTest]
    public IEnumerator T08_CountsMatchingRows()
    {
        var task = Prax.Data.From(SaveTable)
            .Where(PraxFilter.Like("SaveKey", _runTag + "-bulk-%"))
            .CountAsync();
        yield return Await(task);

        Debug.Log($"[T08] count={task.Result}");
        Assert.AreEqual(12, task.Result, "count did not match the number of bulk rows inserted");
    }

    // -------------------------------------------------------- 09 OR groups and IN

    [UnityTest]
    public IEnumerator T09_SupportsOrGroupsAndInFilters()
    {
        var orQuery = Prax.Data.From(SaveTable)
            .Where(PraxFilter.Like("SaveKey", _runTag + "-bulk-%"))
            .Where(PraxFilter.Any(
                PraxFilter.Eq("Level", 1),
                PraxFilter.Eq("Level", 2),
                PraxFilter.Eq("Level", 12)))
            .ToListAsync();
        yield return Await(orQuery);

        Debug.Log($"[T09] OR group matched {orQuery.Result.Count} rows");
        Assert.AreEqual(3, orQuery.Result.Count, "OR grouping did not match the expected rows");

        var inQuery = Prax.Data.From(SaveTable)
            .Where(PraxFilter.Like("SaveKey", _runTag + "-bulk-%"))
            .Where(PraxFilter.In("Level", 3, 4))
            .ToListAsync();
        yield return Await(inQuery);

        Debug.Log($"[T09] IN matched {inQuery.Result.Count} rows");
        Assert.AreEqual(2, inQuery.Result.Count, "IN filter did not match the expected rows");
    }

    // ---------------------------------------------------------------- 10 aggregate

    [UnityTest]
    public IEnumerator T10_RunsAggregates()
    {
        var task = Prax.Data.From(SaveTable)
            .Where(PraxFilter.Like("SaveKey", _runTag + "-bulk-%"))
            .Aggregate("sum", "Coins", "totalCoins")
            .Aggregate("max", "Level", "maxLevel")
            .ToListAsync();
        yield return Await(task);

        Assert.IsTrue(task.Result.Count > 0, "aggregate query returned no rows");
        var row = task.Result[0];
        Debug.Log($"[T10] totalCoins={row.GetInt("totalCoins")} maxLevel={row.GetInt("maxLevel")}");

        // 100+200+...+1200
        Assert.AreEqual(7800, row.GetInt("totalCoins"));
        Assert.AreEqual(12, row.GetInt("maxLevel"));
    }

    // ------------------------------------------------------------ 11 typed mapping

    private class SaveRecord
    {
        public string SaveKey;
        public int Level;
        public int Coins;
        public string DisplayName;
    }

    [UnityTest]
    public IEnumerator T11_ProjectsRowsOntoTypes()
    {
        var task = Prax.Data.From(SaveTable)
            .Where(PraxFilter.Like("SaveKey", _runTag + "-bulk-%"))
            .OrderBy("Level")
            .Limit(3)
            .ToListAsync<SaveRecord>();
        yield return Await(task);

        Assert.AreEqual(3, task.Result.Count);
        foreach (var record in task.Result)
            Debug.Log($"[T11]   {record.SaveKey} level={record.Level} coins={record.Coins} name={record.DisplayName}");

        Assert.AreEqual(1, task.Result[0].Level);
        Assert.AreEqual(100, task.Result[0].Coins);
        Assert.IsNotEmpty(task.Result[0].DisplayName);
    }

    // ---------------------------------------------------------------- 12 scope 403

    [UnityTest]
    public IEnumerator T12_DeniesTablesOutsideTheRoleScope()
    {
        // 'Workers' exists in the workspace but is not scoped to the UnityPlayer role.
        var task = Prax.Data.From("Workers").Limit(1).ToListAsync();

        while (!task.IsCompleted) yield return null;

        Assert.IsTrue(task.IsFaulted, "a table outside the role scope was readable");
        var ex = task.Exception.GetBaseException() as PraxException;
        Assert.IsNotNull(ex, $"expected a PraxException, got {task.Exception.GetBaseException()}");
        Debug.Log($"[T12] denied as expected: code={ex.Code} status={ex.StatusCode} msg={ex.Message}");
    }

    // ----------------------------------------------------------------- 13 refresh

    [UnityTest]
    public IEnumerator T13_RefreshesTheSession()
    {
        var before = Prax.Auth.CurrentUserId;

        var task = Prax.Auth.RefreshSessionAsync();
        yield return Await(task);

        Debug.Log($"[T13] refreshed={task.Result} stillSignedIn={Prax.Auth.IsSignedIn}");
        Assert.IsTrue(task.Result, "refresh reported failure");
        Assert.IsTrue(Prax.Auth.IsSignedIn, "session was dropped by refresh");

        // Identity must survive a refresh - the refresh response omits the user block.
        Assert.AreEqual(before, Prax.Auth.CurrentUserId, "user identity was lost across refresh");

        // And the new token must actually work.
        var query = Prax.Data.From(SaveTable).Limit(1).ToListAsync();
        yield return Await(query);
        Debug.Log($"[T13] query after refresh returned {query.Result.Count} row(s)");
    }

    // -------------------------------------------------------------- 14 concurrency

    [UnityTest]
    public IEnumerator T14_HandlesConcurrentRequests()
    {
        // Several in-flight requests at once: this is where main-thread marshalling and any
        // shared-state race in the transport would surface.
        var tasks = new List<Task<IReadOnlyList<PraxRow>>>();
        for (var i = 1; i <= 6; i++)
        {
            var level = i;
            tasks.Add(Prax.Data.From(SaveTable)
                .Where(PraxFilter.Like("SaveKey", _runTag + "-bulk-%"))
                .Where(PraxFilter.Eq("Level", level))
                .ToListAsync());
        }

        var all = Task.WhenAll(tasks);
        yield return Await(all);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < tasks.Count; i++)
        {
            Debug.Log($"[T14] request {i + 1} -> {tasks[i].Result.Count} row(s)");
            Assert.AreEqual(1, tasks[i].Result.Count, $"concurrent request {i + 1} returned the wrong rows");
        }
        sw.Stop();
    }

    // ------------------------------------------------------------------ 15 delete

    [UnityTest]
    public IEnumerator T15_DeletesAndRefusesUnscopedMutations()
    {
        // Guardrail: no filter must be refused before any request is sent.
        Assert.Throws<ArgumentException>(() =>
            Prax.Data.DeleteAsync(SaveTable, new PraxFilter[0]));
        Assert.Throws<ArgumentException>(() =>
            Prax.Data.UpdateAsync(SaveTable, new Dictionary<string, object> { { "Level", 1 } }, null));

        var task = Prax.Data.DeleteAsync(SaveTable,
            new[] { PraxFilter.Like("SaveKey", _runTag + "%") });
        yield return Await(task);

        Debug.Log($"[T15] deleted {task.Result.AffectedRows} row(s)");
        Assert.AreEqual(13, task.Result.AffectedRows, "cleanup did not remove every row this run created");

        var leftover = Prax.Data.From(SaveTable)
            .Where(PraxFilter.Like("SaveKey", _runTag + "%"))
            .CountAsync();
        yield return Await(leftover);
        Assert.AreEqual(0, leftover.Result, "rows survived the delete");
    }

    // ----------------------------------------------------------------- 16 signout

    [UnityTest]
    public IEnumerator T16_SignsOut()
    {
        var task = Prax.Auth.SignOutAsync();
        yield return Await(task);

        Debug.Log($"[T16] signedIn after sign out = {Prax.Auth.IsSignedIn}");
        Assert.IsFalse(Prax.Auth.IsSignedIn);
        Assert.IsNull(Prax.Auth.CurrentUserId);
    }
}
