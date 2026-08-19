# Changelog

All notable changes to the Praxsuite SDK for Unity.
This project follows [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-08-19

First release.

### Added

**Setup**
- Configuration through a single `PraxsuiteSettings` asset; Workspace ID is the only required
  field. The publishable key is fetched from the workspace's public `/auth/config` endpoint at
  startup, so there is no second value to copy and rotating the key in the portal does not
  require touching the Unity project.
- Self-initialising: the SDK loads its settings on first use, so there is no boot script and no
  ordering dependency between scripts.
- Project Settings page under **Project / Praxsuite**, with live validation.
- Zero package dependencies. Bundled JSON codec rather than a Newtonsoft reference.

**Player accounts** (`Prax.Auth`)
- Register, sign in, sign out; sessions with rotating refresh tokens.
- Automatic token refresh, both proactively before expiry and once in response to a 401.
  Concurrent refreshes share one request, since the gateway invalidates the old refresh token as
  it issues the new one and two racing refreshes would leave the loser holding a retired token.
- Password reset by emailed code, change password, resend confirmation.
- OIDC sign-in (authorization URL plus callback exchange).
- Email-confirmation-required responses surfaced explicitly rather than as a generic failure.
- `GetWorkspaceConfigAsync` returns workspace branding and enabled auth features, for a sign-in
  screen that matches the workspace without hardcoding anything.

**Data** (`Prax.Data`)
- Fluent queries: filters, OR/AND groups, ordering, paging, total count, relations, aggregates
  with `groupBy` and `having`.
- Insert, insert-many, update, delete, upsert. Update and delete require a filter - the gateway
  refuses an unscoped mutation and so does the SDK.
- `PraxRow` with forgiving typed accessors, and reflection-based projection onto your own classes.
- Table addressing by name via a fetched schema, or by GUID with no schema request at all.

**Endpoints** (`Prax.Endpoints`)
- `CallAsync` for sync endpoints, `FireAsync` for fire-and-forget telemetry that never throws.
- The player's session token is attached automatically, so automations can identify the caller
  from a verified claim rather than trusting a player id in the payload.

**Files** (`Prax.Files`)
- Upload and download bytes or textures; short-lived signed URLs; list and delete.

**Players** (`Prax.Players`)
- Platform identity links for analytics and account linking, with `IsValidated` exposed so
  callers can tell a verified id from an unverified label.

**Dedicated servers** (`Praxsuite.Server.PraxServer`)
- Secret-key access for a headless server build. The key is read from the environment or a
  secret file mounted outside the project - never from an asset.

**Transport**
- Retry with exponential backoff and jitter for network errors, 5xx and rate limits, honouring
  `Retry-After`. Quota exhaustion is deliberately not retried.
- `PraxException` with a stable `Code` and predicates (`IsRateLimited`, `IsQuotaExceeded`,
  `IsForbidden`, `IsNetworkError`, `IsTransient`) so callers never match on message text.
- Main-thread marshalling, so `UnityWebRequest` is always created where Unity requires it even
  when a call resumes on a worker thread after an `await`.
- `Task`-based throughout, with `AsCoroutine()` for projects not using async/await.

### Security

- **Four independent controls against shipping a secret key**: a runtime guard with no opt-out
  flag; an assembly excluded from Android, iOS, WebGL and console; a `PRAXSUITE_SERVER` define
  constraint; and a build guard that **fails the build** on any `sk_live_` found under `Assets/`
  or `ProjectSettings/`, or when `PRAXSUITE_SERVER` is defined for a player-facing target.
- Sessions stored AES-256-CBC encrypted under `persistentDataPath` with a PBKDF2 device-derived
  key. Never `PlayerPrefs`. `IPraxTokenStore` allows platform keychains instead.
- Plaintext `http://` to a remote host throws at construction and fails the build; loopback is
  allowed for local development.
- All SDK logging passes through a scrubber that strips keys, JWTs and password/token fields.
- No client-side impersonation parameter, deliberately. Per-player isolation comes from the
  player's own token plus two server-side settings: a `__SELF__` row filter on the table scope,
  and a `{{claim:sub}}` default value template on the `Enduser` column's scope. Both are needed -
  see the note below and `docs/security.md`.

### Verified against a live gateway

Run in Unity 6000.5.9f1 against a real workspace on 2026-08-19: 41/41 PlayMode tests passing
(auth, schema, insert, query, update, bulk insert, paging, count, OR/IN filters, aggregates,
typed projection, scope denial, token refresh, concurrent requests, delete, sign-out), plus
25/25 offline tests. The live suite lives in `Tests/Integration~/`.

Four defects were found by that run and fixed before release:

- **The CI harness broke Unity.** `dotnet test` wrote its assembly into `ci/bin/`, and Unity
  imports any DLL found inside a package - so the harness assembly shadowed every SDK type and
  its `UnityEngine` stubs collided with the real `HeaderAttribute`, `TooltipAttribute` and
  `RangeAttribute`. Anyone who ran the tests and then opened Unity got a project that would not
  compile. The folder is now `ci~/`, which the asset pipeline ignores.
- **Mutation guardrails could fail silently.** `InsertAsync`, `UpdateAsync`, `DeleteAsync` and
  `UpsertAsync` validated their arguments inside `async` methods, so a refusal became a faulted
  Task rather than a thrown exception. A caller who launched one fire-and-forget got no write
  and no error. Validation now happens synchronously, at the call site.
- **`PraxSchema.Has()` was missing.**
- **The `Enduser` column is not auto-filled on insert.** The docs and the QuickStart sample said
  the gateway stamps ownership from the caller's token. It does not: that requires a
  `{{claim:sub}}` default value template on the column's scope. With only a `__SELF__` row
  filter configured, inserts land with a NULL owner which the filter then hides - the player
  saves their game and cannot read it back. Both required settings are now documented.

### Behaviour worth knowing

Verified against the gateway rather than assumed, because each of these is a place where a
plausible-looking guess produces silently wrong results:

- **Only the operators the gateway implements are exposed.** `PraxFilter` offers `eq`, `neq`,
  `gt`, `gte`, `lt`, `lte`, `like`, `ilike`, `in`, `is`, `between`, `contains` and `textsearch`.
  Familiar names such as `startsWith`, `endsWith` and `notIn` are deliberately absent - the
  server rejects them, so offering them would only produce runtime 400s. `IsNull` and
  `IsNotNull` compile down to the supported forms.
- **Counts read the gateway's `meta.total`.** Reading a differently-named field returns zero
  forever rather than failing, so this one is pinned by a test.
- **The host is explicit.** Praxsuite runs several independent tiers and a workspace lives on
  exactly one; the wrong host returns 404 rather than a useful error. Set `Host` in the
  settings asset to match your workspace's API Gateway page.
- **There is no client-side "act as player X" parameter, on purpose.** Per-player isolation
  comes from the player's own token plus server-side scoping. A parameter the server does not
  enforce would read like a security boundary while being decorative.
