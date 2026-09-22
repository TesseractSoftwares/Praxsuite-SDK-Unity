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

## Guides

- [Use Case](https://learn.praxsuite.com/examples/unity/unity-sdk-use-case/)
- [Implementation](https://learn.praxsuite.com/examples/unity/unity-sdk-implementation/)

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
| `Prax.Bus` | The Event Bus - ephemeral realtime between connected players (not on WebGL) |
| `PraxServer` | Secret-key access for a dedicated server build. Excluded from client builds by construction. |

---

## The Event Bus

Ephemeral realtime between connected players: avatars, cursors, "is typing", a lobby. State
that is *changing*, where losing a message is fine because a newer one is 100ms behind it.

```csharp
await Prax.Auth.LoginAsync(email, password);   // the bus needs a signed-in player, not the key

var room = Prax.Bus.Topic("office").Channel("hq");   // the bus "office:hq"

room.On("move", e => MoveAvatar(e.FromUserId, e.Payload));
room.OnPeerLeft(RemoveAvatar);

// JoinAsync returns everyone already in the room, so a player who arrives late sees the
// world rather than an empty one until somebody happens to move.
foreach (var peer in await room.JoinAsync())
    MoveAvatar(peer.UserId, peer.Payload);

await room.PublishAsync("move", new { x, y });
```

**Handlers run on Unity's main thread**, so they may touch Transforms, instantiate prefabs and
read scene state directly - the receive loop is a worker thread and the SDK marshals for you.

**A topic must exist before anyone can join it.** Declare it once in the portal under
API Gateway / Event Bus and pick its access rule: open to any signed-in player, gated on a role
from their token, or gated on a grant on that one bus instance. An undeclared topic is refused -
which is what stops another game's client squatting in your namespace.

`Prax.Bus.Self` is the player's own bus, `user:self`. The server resolves it to their id, so it
can never address anybody else - useful for pushing to one player across their devices.

Publish **decisions, not frames**. One message per movement decision (`from`, `to`) rather than
one per rendered frame: a two-second walk becomes one message instead of a hundred, and the
receiving client interpolates. The rate limit is priced by RECIPIENTS, so a busy room exhausts
it far faster than an empty one.

Three things about it are not obvious and will bite:

- **Nothing is persisted.** No history, no retry, no delivery to a player who was not connected.
  The test is one question: *if this is lost, does it matter?* Yes - a purchase, a score, an
  inventory grant - means a table or an automation, and a server-authoritative one at that. No,
  because a newer one is coming, means the bus.
- **Payloads are hostile.** The bus relays opaque JSON between *players* and parses none of it,
  so every server-side check is bypassed. A position is a hint, never an authority.
- **You never receive your own event.** Apply your own change locally.

`PublishAsync` does not throw when the bus refuses a frame - a game loop that throws on a rate
limit is worse than one that skips a frame. Read the result when you care:

```csharp
var r = await room.PublishAsync("move", new { x, y });
if (!r.Ok) Debug.Log(r.Error);        // e.g. "rate_limited"
if (r.Recipients == 0) { }            // it went out, and nobody was joined
```

`JoinAsync` is the opposite and throws: a publish that does not land is one lost frame, a join
that does not land leaves this player silently absent for the whole session.

**Not on WebGL.** The bus runs on `ClientWebSocket`, which Unity supports on standalone, iOS,
Android and the editor but not in a WebGL build - that target has no socket API and every
connection attempt fails at runtime. Everything else in this SDK works on WebGL; only the bus
does not.

---

## Signing in with an external provider

```csharp
var config = await Prax.Auth.GetWorkspaceConfigAsync();
foreach (var provider in config.Providers)
    AddSignInButton(provider.Slug, provider.DisplayName);

var start = await Prax.Auth.StartOidcLoginAsync("tesseract");
Application.OpenURL(start.AuthorizationUrl);

// ...once the provider redirects back (deep link, or a loopback listener) with code and state:
await Prax.Auth.CompleteOidcLoginAsync(
    "tesseract", code, state,
    "https://app.example/callback");   // byte-identical to the configured redirect URI
```

All four arguments are required, and three of them are why an external sign-in fails when it
fails: the gateway scopes its one-time `state` per provider, consumes it once, and compares the
redirect URI against the value configured for that provider. Pass the URI you were actually
redirected to rather than rebuilding it.

The session lands in the same store as a password login, so refresh, sign-out and every
authenticated call behave identically afterwards.

Only the authorization-code flow exists - there is no route that accepts a provider's own
id_token - so even a native Google or Apple button has to make this browser hop, which makes it
a fit for desktop and mobile rather than for a console.

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

## Conformance is the law

Praxsuite has SDKs in several languages. Where they touch the gateway they do **not** get to
disagree. A single normative contract defines the shared behaviour, and every SDK implements it
identically:

1. **The contract is normative.** Where this SDK and the contract differ, this SDK is wrong.
2. **Every rule cites the backend source it derives from.** No rule rests on memory.
3. **Every rule exists because getting it wrong fails silently.** Wrong data, not an error.
4. **A behaviour change is a contract change first.** Not an implementation detail.

The contract is internal and deliberately has no public repository. Its value is that it is
authoritative for us, not that it is browsable — and it cites backend internals that are not ours
to publish. Everything a consumer of this SDK needs to know is in this README.

What it pins down, and why each one earned its place:

- **Operators.** Only the thirteen the parser accepts. A friendlier name is a runtime 400.
- **`meta.total`, never `meta.totalCount`.** Reading the wrong name returns nothing and reports
  zero, silently, forever. One SDK shipped that for months.
- **Three response envelopes.** `/query` is bare, `/auth/*` nests under `.data`, `/files` errors
  are a bare string. Assuming one shape mis-parses the other two.
- **`limit` is clamped up to a minimum of 1.** A zero-row count request quietly returns a row.
- **Unscoped updates and deletes refused before sending**, synchronously.
- **Secret keys refused wherever a credential would be exposed.** No flag, no override.
- **No client-supplied identity parameter.** The server ignores it, so it would read as a
  security boundary while being decorative.

The suite runs offline — no workspace, no network, no credentials:

```bash
dotnet test ci~/Praxsuite.CI.csproj
```


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
