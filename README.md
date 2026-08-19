# Praxsuite SDK for Unity

[![CI](https://github.com/TesseractSoftwares/Praxsuite-SDK-Unity/actions/workflows/ci.yml/badge.svg)](https://github.com/TesseractSoftwares/Praxsuite-SDK-Unity/actions/workflows/ci.yml)
[![Unity 2021.3+](https://img.shields.io/badge/unity-2021.3%2B-black?logo=unity)](https://unity.com)
[![Licence](https://img.shields.io/badge/licence-Praxsuite%20Open%20SDK-blue)](LICENSE)
[![Dependencies](https://img.shields.io/badge/dependencies-none-brightgreen)](package.json)

Backend for your game — player accounts, saves, leaderboards, inventories, files and
server-authoritative logic — with no server code to write.

Zero dependencies. One field to configure. Built so that shipping a secret key fails the build
rather than shipping.

---

## Install

Unity → **Window → Package Manager → + → Add package from git URL**:

```
https://github.com/TesseractSoftwares/Praxsuite-SDK-Unity.git
```

Or add it to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.tesseractsoftwares.praxsuite": "https://github.com/TesseractSoftwares/Praxsuite-SDK-Unity.git"
  }
}
```

Requires Unity 2021.3 or newer. No other packages needed.

## Configure

**Praxsuite → Create Settings Asset**, then paste your Workspace ID — the GUID in your portal
URL, `/workspace/<this-guid>`.

That is the entire setup. The publishable key is fetched from the workspace's public config
endpoint at startup, so there is no second value to copy and rotating the key in the portal does
not mean touching the Unity project.

> **One thing to get right:** Praxsuite runs several independent tiers, and a workspace lives on
> exactly one. Point at the wrong host and every call returns 404 — not an error message that
> explains itself. Set **Host** to match the URL on your workspace's API Gateway settings page.

## Use

```csharp
using Praxsuite;

// Sign a player in. The session survives app restarts.
var login = await Prax.Auth.LoginAsync(email, password);

// Read their save. No player id anywhere: the server's row filter scopes it to them.
var save = await Prax.Data.From("PlayerSaves").FirstAsync();
Debug.Log($"Level {save.GetInt("Level")}, {save.GetInt("Coins")} coins");

// Write it back.
await Prax.Data.UpdateByIdAsync("PlayerSaves", save.Id, new Dictionary<string, object>
{
    { "Level", 12 },
    { "Coins", 340 }
});

// Anything a cheater would want to forge goes through the server.
var reward = await Prax.Endpoints.CallAsync("claim-daily-reward");
```

Prefer coroutines? `yield return task.AsCoroutine();`

---

## What is in it

| | |
|---|---|
| `Prax.Auth` | Register, sign in, sessions with rotating refresh tokens, password reset, email confirmation, OIDC |
| `Prax.Data` | Queries with filters, ordering, paging, joins and aggregates; insert, update, delete, upsert |
| `Prax.Endpoints` | Call gateway automations — the server-authoritative path |
| `Prax.Files` | Upload and download, including textures; short-lived signed URLs |
| `Prax.Players` | Platform identity links for analytics and account linking |
| `Prax.Schema` | Address tables by name instead of GUID |
| `PraxServer` | Secret-key access for a dedicated server build. Excluded from client builds by construction. |

---

## Security in three lines

A game client is untrusted code running on someone else's machine. This SDK assumes that:

1. **Ship only a publishable key (`pk_live_`), and scope it to nothing.** It is an identifier,
   not a credential — anyone can extract it from the build or fetch it unauthenticated, so
   whatever it can reach, the anonymous internet can reach. Auth still works on a credential
   with zero table scopes, which makes an extracted key worthless. A *secret* key anywhere under
   `Assets/` **fails the build** — four independent controls enforce that, not a doc comment.
2. **Give each player their own identity.** A `__SELF__` row filter on their role scopes every
   read and write to their own rows, server-side, where a modified client cannot reach it.
3. **Put anything valuable behind an endpoint.** Currency, inventory grants and score
   submission belong in an automation you control, not in a client-side table write.

Full reasoning, including what the session store does and does not protect against:
**[docs/security.md](docs/security.md)**.

---

## Samples

Import from Package Manager → Praxsuite SDK → Samples:

- **Quick Start** — sign in, load and save a player's own row
- **Leaderboard** — public reads, server-validated score submission
- **Server-Authoritative Purchase** — a shop a modified client cannot cheat

---

## Error handling

Everything throws `PraxException`, with a stable `Code` and predicates so you never match on
message text:

```csharp
try
{
    await Prax.Data.InsertAsync("Scores", values);
}
catch (PraxException ex) when (ex.IsRateLimited)   { /* already retried with backoff */ }
catch (PraxException ex) when (ex.IsQuotaExceeded) { /* plan exhausted — retrying will not help */ }
catch (PraxException ex) when (ex.IsForbidden)     { /* the role's table scope, not the query */ }
catch (PraxException ex) when (ex.IsNetworkError)  { /* really offline */ }
```

Network errors, 5xx and rate limits are retried automatically with exponential backoff, jitter
and `Retry-After`. Quota errors deliberately are not — retrying an exhausted quota only burns
battery.

---

## Dedicated servers

```csharp
#if PRAXSUITE_SERVER
PraxServer.InitializeFromEnvironment();  // reads PRAXSUITE_SECRET_KEY
#endif
```

Set `PRAXSUITE_SERVER` **only** on the Dedicated Server build target. Set it on a player-facing
target and the build guard stops the build. The key comes from the environment — never an asset,
because assets get committed.

---

## Contributing

Bug reports, fixes, docs and samples are all welcome — see [CONTRIBUTING.md](CONTRIBUTING.md).
It also lists the traps worth knowing before changing code (why the CI harness lives in `ci~/`,
why guardrails must not validate inside an `async` method).

Found a security issue? **Do not open an issue** — see [SECURITY.md](SECURITY.md).

```bash
dotnet test ci~/Praxsuite.CI.csproj
```

That runs the full offline suite. No Unity licence needed.

---

## License

**Praxsuite Open SDK Licence v1.0** — source-available. See [LICENSE](LICENSE).

- ✅ Use it free in anything you build, **including products you sell**
- ✅ Read, fork, modify and publish your changes
- ✅ Build extensions, wrappers and integrations
- ❌ Don't resell the SDK itself, or use it to power a competing backend platform

Build whatever you like on top of Praxsuite and keep every cent. The licence limits what
can be done with *this code*, never what you charge for the product you make with it.

Source-available, not OSI open source — the field-of-use limits fail OSI criteria 5 and 6,
so it isn't described as an open-source licence.
