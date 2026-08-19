# Security

The one thing to understand: **a game client is untrusted code running on someone else's
machine.** Anything shipped inside it can be extracted, and anything it sends can be forged.
This SDK is built on that assumption rather than around it.

That is not pessimism about Unity. It is true of every client SDK ever written. The question is
never "can the client be trusted" but "what did we let an untrusted client do".

---

## The three rules

### 1. Never ship a secret key

Praxsuite issues two kinds of gateway credential:

| Key | Scope | Where it belongs |
|---|---|---|
| `pk_live_…` | Only what its scopes allow; a player role's row filter narrows it further | Anywhere, including a shipped build |
| `sk_live_…` | Everything the credential is scoped to, no row filter | A server you control. Nowhere else. |

A secret key in a Unity project ends up in the build, and pulling strings out of a build takes
minutes. Whoever does it gains that key's full access to your workspace data.

The SDK enforces this in four independent places, because a rule that lives only in
documentation is not a control:

1. **`PraxKeyGuard.RequireClientSafe`** throws at every client entry point that accepts a
   credential. There is no opt-out flag — every "just for testing" opt-out eventually ships.
2. **Assembly platform scope.** `Praxsuite.Server.asmdef` lists only Editor and standalone
   desktop, so the secret-key code does not compile at all for Android, iOS, WebGL or console.
3. **Define constraint.** That same assembly requires `PRAXSUITE_SERVER`. Without it the
   assembly is skipped even on a listed platform, so the default for every project is that
   `PraxServer` does not exist.
4. **Build guard.** `PraxBuildGuard` scans `Assets/` and `ProjectSettings/` before every build
   and fails it outright on an `sk_live_` match, or when `PRAXSUITE_SERVER` is defined for a
   target players can run.

If the build guard fires: **revoke that key first**, in the portal under API Gateway →
Credentials. By the time it is in your project folder it is probably also in git history, and
git history is forever.

#### What the publishable key is, and is not

A publishable key is an **identifier, not a credential**. It says *which workspace*, not *who
you are*. It is public by design, in two ways that both matter:

- it ships inside the build, and can be pulled out of one in minutes;
- `GET /{workspaceId}/auth/config` returns it **unauthenticated**, so anyone holding the
  workspace GUID can simply fetch it. That endpoint is what the SDK's auto-discovery uses.

So embedding the key in the settings asset versus letting the SDK fetch it makes **no security
difference whatsoever**. Auto-discovery is a convenience — one less value to keep in sync when a
key is rotated — not a protection.

The consequence is the sharp edge:

> **Every table scope granted to the publishable credential is granted to the anonymous
> internet.** Not "to your players" — to anyone with the workspace GUID and curl.

**So give the publishable credential zero table scopes.** This works, and it is the recommended
setup, because of two things the gateway does:

- Auth routes skip scope checks entirely (`skipTableScopes: true` in
  `GatewayAuthenticationMiddleware`), so `register`, `login` and `refresh` still function on a
  credential with no scopes at all.
- When a player's JWT is present, `authContext.TableScopes` is replaced by the scopes built from
  **that player's roles**. The credential's own scopes are not consulted.

With zero scopes, an extracted key is worth nothing: all it can do is offer an attacker a login
form. Every byte of data requires a signed-in player, and rule 2 below scopes that to their own
rows.

**The tradeoff, stated plainly:** zero scopes also means no anonymous reads. A leaderboard on the
title screen before sign-in will not work. The options are to require sign-in first, to grant the
publishable credential read-only access to *only* that table (accepting that it is then genuinely
world-readable — which a public leaderboard already is), or to serve it through an endpoint.
Decide that deliberately rather than by leaving scopes broad.

### 2. Give every player their own identity

The gateway's per-player isolation needs **two** settings, and they do different jobs. Configuring
only the first is the common mistake, and it fails in a confusing way rather than an obvious one.

**1. Row filter — scopes reads and rewrites.**
On the role's *table* scope, set the row filter to `__SELF__`. The gateway expands it against the
table's `Enduser` column and **injects it into every query made with that role's token**, on the
server, after the client's own conditions. So a player reading `PlayerSaves` gets their save and
nobody else's, and a modified client sending a hand-built query still gets only their own —
the filter is not part of what the client sends, so it is not part of what the client can remove.

**2. Default value template — stamps ownership on insert.**
On the `Enduser` *column's* scope, set the default value template to `{{claim:sub}}`.

This second one is not optional, because **a row filter does nothing for INSERT** — an insert has
no WHERE clause to filter. The default template is what writes the owner, resolved from the
caller's verified token. And a column carrying a default template **cannot be set by the client at
all**: supplying it is rejected with *"Column 'X' has a DefaultValueTemplate and cannot be set
explicitly"*. That rejection is the actual guarantee — it is why a modified build cannot insert a
row owned by somebody else.

**What happens if you configure only the row filter** (measured, not theorised — this is what the
SDK's own integration run produced before the column scope was set): inserts succeed with a NULL
owner, and the row filter then excludes those rows. The player saves their game and cannot read
it back. No error, no denial, just data that vanishes.

> Neither setting is currently reachable through the MCP/agent tools — `set_role_table_access`
> exposes neither `rowFilterTemplate` nor column scopes, though the REST API supports both.
> Configure them in the portal.

```csharp
// No player id anywhere. That is the point.
var save = await Prax.Data.From("PlayerSaves").FirstAsync();
```

**What does not work, and why it looks like it should:** passing a player id in a header, a
where clause, or a request body. The client chooses those values, so a cheater chooses them too.
Only a value the server derives itself — the `sub` claim of a token it issued and verified —
can scope anything. That is why this SDK has no "act as player X" parameter: an impersonation
argument the server does not enforce is worse than none, because it reads like a security
boundary while being decorative.

### 3. Put anything valuable behind an endpoint

Scope the player role **read-only** wherever you can, and route writes that matter through
`Prax.Endpoints`, where an automation you control decides the outcome.

| Operation | Direct table write | Gateway endpoint |
|---|---|---|
| Graphics settings, key bindings | ✅ | |
| Last level played, cosmetic state | ✅ | |
| Currency balance | | ✅ |
| Inventory grants | | ✅ |
| Score submission | | ✅ |
| XP and levelling | | ✅ |
| Anything touching another player | | ✅ |

The test: *if a modified client sending an arbitrary payload could get something it should not,
that operation belongs in an endpoint.*

This is the familiar split between a client API and trusted server-side logic. Here that
server-side half is an automation you build in the portal - there is no separate function app
to deploy.

**Inside the automation, take the player's identity from the verified token claim, never from
the request body.** An automation that trusts `body.playerId` hands every player the ability to
act as anyone.

---

## Sessions on disk

`persistSession` keeps players signed in across restarts. The refresh token is stored
AES-256-CBC encrypted under `Application.persistentDataPath`, with a key derived by PBKDF2
(100k iterations) from the device id and the application identifier. Never `PlayerPrefs` — on
desktop that is a plaintext registry key or file any other process can read.

Be clear about what that buys:

**It stops:** other apps on the device reading the file, a player casually browsing the save
folder, a session file copied to different hardware (the derived key will not match, so it
fails closed and the player signs in again).

**It does not stop:** the owner of the device. The key is derived from values present on that
device, so anyone who can attach a debugger to your process can recover the token. This ceiling
is unavoidable for any client-side credential store on hardware the attacker controls.

The conclusion is therefore not "store tokens better". It is **do not let a stolen session be
worth stealing** — which is rules 2 and 3 again. If a session grants only "read my own rows and
ask the server nicely for things", stealing one is not much of a prize.

Set `persistSession = false` for a shared or kiosk machine, or supply your own `IPraxTokenStore`
backed by the iOS Keychain, the Android Keystore, or a console save API.

---

## Transport

- **HTTPS is enforced.** A plaintext `http://` URL pointed at a remote host throws at
  construction and fails the build. Loopback is allowed for local development.
- **Credentials never appear in a URL.** Keys go in the `x-api-key` header, session tokens in
  `Authorization: Bearer` — query strings end up in proxy logs and browser history.
- **File URLs carry no credential.** Content is proxied by the gateway. When you need a URL
  something else can fetch, `GetSignedUrlAsync` mints a short-lived signed one — treat it as a
  secret for its lifetime and ask for the shortest expiry that works.
- **Logs are scrubbed.** Every message passes through `PraxLog.Scrub`, which strips
  `pk_live_`/`sk_live_` keys, JWTs, and `refreshToken` / `accessToken` / `password` /
  `sessionToken` fields. Verbose logging still writes *player data* to the device log, so leave
  it off in release builds — the build guard warns when it is on.

---

## A note on origin rules

Publishable keys support origin allow-lists, which is how browser clients are pinned to a domain.

**They do nothing for a native client.** A Unity build sends no `Origin` header, so a key with
origin rules configured is rejected outright (`ValidateOrigin` denies an empty origin when rules
exist) — and a client that *did* send one could send any value it liked, since it controls its
own headers.

So: **use a publishable key with no origin rules for game clients**, and get your security from
role scopes and row filters, not from origin pinning. If the same workspace also serves a web
app, give the web app its own pk key with origin rules and the game its own without.

---

## Rate limits and quotas

Both surface as HTTP 429, and the difference matters:

```csharp
catch (PraxException ex) when (ex.IsRateLimited)   // too fast — the SDK already backed off and retried
catch (PraxException ex) when (ex.IsQuotaExceeded) // plan allowance exhausted — retrying cannot fix it
```

The SDK retries rate limits automatically with exponential backoff and jitter, honouring
`Retry-After`. It deliberately does **not** retry quota errors: hammering an exhausted quota
only burns battery. Surface those to the player as "temporarily unavailable" and alert whoever
owns the workspace.

---

## Pre-ship checklist

- [ ] No `sk_live_` anywhere in the project — let the build guard confirm it, do not eyeball it
- [ ] `PRAXSUITE_SERVER` not defined for any player-facing target
- [ ] **The publishable credential has no table scopes** (or only ones you accept as
      world-readable) — whatever it can reach, anyone can reach
- [ ] The client's publishable key has **no origin rules**
- [ ] The player role is read-only on every table with value
- [ ] Tables holding per-player data have an `Enduser` column and a `__SELF__` row filter
- [ ] Currency, inventory and score writes all go through endpoints
- [ ] Every automation reads identity from the token claim, never from the request body
- [ ] `verboseLogging` off
- [ ] Gateway URL is `https://`
- [ ] You know which tier hosts your workspace (a workspace on the wrong host 404s)
