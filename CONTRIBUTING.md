# Contributing

Contributions are welcome — bug reports, fixes, docs, samples, platform support.

Before anything else: **security issues do not go in the issue tracker.** See
[SECURITY.md](SECURITY.md).

## Licence, up front

This SDK is under the [Praxsuite Open SDK Licence](LICENSE) — source-available, not OSI open
source. You can use it free in anything you build, including products you sell, and you can fork
and modify it. You cannot resell the SDK itself or use it to build a competing backend platform.

By contributing, you license your contribution under the same terms and confirm you have the
right to submit it. You keep the copyright in what you wrote.

## Running the tests

Two suites, and you can run the first without Unity at all:

```bash
# Offline: compiles all four assemblies and runs the unit suite against Unity stubs
dotnet test ci~/Praxsuite.CI.csproj
```

That covers the JSON codec, query builder, wire-shape parsers, credential guard and log
scrubber — the parts where a mistake produces wrong data rather than a compile error. It needs
no Unity licence, and it is what CI runs on every push.

The live suite in [`Tests/Integration~/`](Tests/Integration~/README.md) drives a real gateway
from inside Unity. It needs a workspace and a throwaway end user, supplied through environment
variables. **Never commit real credentials** — a workspace GUID alone is enough to fetch that
workspace's publishable key, since `/auth/config` is unauthenticated by design.

## Things worth knowing before you change code

These have all bitten someone already:

- **Never put build output inside the package folder.** Unity imports any DLL it finds in a
  package as a managed plugin. That is why the CI harness lives in `ci~/` — a `~` suffix makes
  a folder invisible to Unity's asset pipeline, the same trick `Samples~` uses.
- **Do not validate arguments inside an `async` method.** The exception goes into the returned
  Task instead of being thrown at the call site, so a caller who does not await gets silence.
  Guardrails validate in a non-`async` wrapper and hand off to a private core method.
- **Nothing may log a credential.** Everything routes through `PraxLog`, which scrubs keys,
  JWTs and password fields. If you add a log line, use `PraxLog`, not `Debug.Log`.
- **The client is untrusted.** Do not add an API that takes a caller-supplied identity — a
  parameter the server ignores reads like a security boundary while being a comment. Identity
  comes from the player's own token.
- **Zero dependencies is a feature.** The bundled JSON codec exists so the package never drags
  Newtonsoft (and its version conflicts) into someone's project. Please do not add a
  `PackageReference` without discussing it first.

## Style

Match the surrounding code. A few conventions the codebase holds to:

- Comments explain *why*, not *what*. If a line needs a comment to say what it does, rename
  something instead.
- Public API gets XML docs, written for someone who has never seen Praxsuite.
- Error messages say what to do next, not just what went wrong.

## Pull requests

1. Fork, branch from `master`
2. Make the change, keeping `dotnet test ci~/Praxsuite.CI.csproj` green
3. Add a test if you fixed a bug — it should fail before your fix
4. Update `CHANGELOG.md` under Unreleased
5. Open the PR describing what changed and why

Small, focused PRs get reviewed faster than large ones. If you are planning something
substantial, open an issue first so we can agree on the shape before you spend the time.

## Reporting a bug

Use the issue template. The two things that make a report actionable are the **exact error**
(SDK errors carry a stable `Code` — include it) and a **minimal repro**. Unity version, SDK
version and platform help too.
