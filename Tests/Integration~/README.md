# Live integration tests

These drive the SDK against a **real Praxsuite gateway** from inside Unity. They cover what the
offline suite in `../Runtime` cannot: `UnityWebRequest` transport, main-thread marshalling after
an `await`, the dispatcher, real authentication, and the actual wire contract.

The folder is suffixed `~` so Unity ignores it. That is deliberate — these tests hit the network,
need a configured workspace, and write rows, so they must never run as part of a consumer's build.
Copy them into a test project when you want to run them.

## What they caught

Everything here was found by running it, not by reading the code:

| Finding | Fix |
|---|---|
| `dotnet test` output landed in `ci/bin/`, and Unity imports any DLL inside a package — the CI assembly shadowed every SDK type and collided with `UnityEngine` attributes, breaking any project that ran the tests then opened Unity | renamed to `ci~/` |
| `Update`/`Delete`/`Insert` guardrails threw from inside an `async` method, so the exception went into the returned Task — a fire-and-forget caller got silence instead of a refusal | validation moved out of the `async` core so it throws at the call site |
| `PraxSchema` had no `Has()` | added |
| **Docs claimed the gateway auto-fills the `Enduser` column on insert. It does not.** Rows landed with a NULL owner, which a `__SELF__` row filter then hides — a player saves and cannot read the save back | corrected; both required settings now documented |

## Running them

1. Create a Unity project and add the SDK:
   ```json
   {
     "dependencies": {
       "com.tesseractsoftwares.praxsuite": "file:/path/to/Praxsuite-SDK-Unity",
       "com.unity.test-framework": "1.4.5"
     },
     "testables": ["com.tesseractsoftwares.praxsuite"]
   }
   ```
2. Copy both files in this folder into `Assets/Tests/`.
3. Set `PRAX_TEST_WORKSPACE`, `PRAX_TEST_HOST`, `PRAX_TEST_EMAIL` and `PRAX_TEST_PASSWORD`
   in the environment. Never commit real values - a workspace GUID alone is enough to fetch
   that workspace's publishable key, because `/auth/config` is unauthenticated by design.
4. Run them:
   ```bash
   Unity.exe -batchmode -nographics -projectPath <project> -runTests -testPlatform PlayMode -testResults results.xml -logFile unity.log
   ```

## Workspace the tests expect

- Table `UNITY_PlayerSaves` — `SaveKey` (key), `Owner` (Enduser), `Level` (Number),
  `Coins` (Number), `DisplayName` (ShortText), `LastPlayed` (DateTime)
- Table `UNITY_Scores` — `ScoreKey` (key), `Player` (Enduser), `PlayerName` (ShortText),
  `Score` (Number), `Level` (ShortText)
- A role scoped ReadWrite on `UNITY_PlayerSaves` and Read on `UNITY_Scores`, assigned to the
  test end user
- At least one table the role is **not** scoped to, so the denial test has something to be
  refused by

`T15` deletes every row it created, keyed on a per-run tag, so repeated runs do not accumulate
data. A run that fails partway leaves its rows behind — clean them up with a
`SaveKey like 'run-%'` delete.

## Not covered

- The `__SELF__` row filter and the `{{claim:sub}}` default value template. Neither is reachable
  through the MCP/agent tools (`set_role_table_access` exposes neither `rowFilterTemplate` nor
  column scopes), so the run could not configure them. Per-player isolation is therefore
  **unverified end to end** — set both in the portal and re-run to close that gap.
- File upload and download.
- Gateway endpoints, which need an automation to call.
- Retry and backoff, which need an induced failure.
