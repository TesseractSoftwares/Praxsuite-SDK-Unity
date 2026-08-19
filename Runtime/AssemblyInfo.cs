using System.Runtime.CompilerServices;

// The test assembly exercises wire-shape helpers that are internal on purpose: PraxQuery's
// request builder, PraxData's response parsers, PraxHttp's error mapping. Those are contracts
// against the gateway rather than public API, so they should not be part of the SDK's surface -
// but they are exactly the parts worth testing, because a silent mismatch there produces wrong
// data rather than a compile error.
[assembly: InternalsVisibleTo("Praxsuite.Tests")]

// The editor assembly reads the same internals to validate configuration before a build.
[assembly: InternalsVisibleTo("Praxsuite.Editor")]
