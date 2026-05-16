module Fugue.Adapters.Console.Tests.Helpers

// ============================================================================
// FACT_DISCOVERY — why every test in this assembly is [<Property(MaxTest=1)>]
// ============================================================================
//
// Plain [<Fact>] tests are NOT discovered in this assembly. Root cause has not
// been isolated despite the fsproj and package setup being structurally
// identical to tests/Fugue.Tests/ (which discovers [<Fact>] correctly).
//
// Workaround (canonical, adopted throughout this project): write Fact-style
// tests as [<Property(MaxTest=1)>] returning bool. FsCheck.Xunit's [<Property>]
// IS discovered, and MaxTest=1 makes it a single-shot deterministic check
// exactly equivalent to [<Fact>].
//
// Every test file that uses this pattern carries the line:
//   // see Helpers.fs FACT_DISCOVERY for why all tests are [<Property>]
// just after the module declaration.
//
// TODO(FACT_DISCOVERY): File an upstream issue when the root cause is found,
// then update every [<Property(MaxTest=1)>] site to plain [<Fact>]. Use
//   rg 'TODO\(FACT_DISCOVERY\)' tests/
// to locate every site that needs updating.
// ============================================================================

/// Convenience alias — using [<Property(MaxTest=1)>] everywhere is noisy.
/// Import this module with `open Helpers` in test files if it helps readability.
[<Literal>]
let MaxTest1 = 1
