module Fugue.Tests.RenderSnapshotTests

// Per-render-path snapshot tests. Fixtures live under tests/Fugue.Tests/Snapshots/*.txt
// and assert byte-for-byte equality against Renderer.toRawAnsi output for a deterministic
// Composition input. See specs/005-cli-layout-migration/contracts/snapshot-fixture-shape.md
// for the format contract.

open System.IO
open Xunit
open Fugue.Adapters.Console

/// Load a snapshot fixture by name (e.g. "surface_status_bar"). Returns the raw bytes
/// as a string with LF line endings preserved.
let private readSnapshot (name: string) : string =
    let path = Path.Combine("Snapshots", $"{name}.txt")
    File.ReadAllText path

[<Fact>]
let ``readSnapshot loads _smoke.txt fixture correctly`` () =
    // _smoke.txt is a permanent self-test of the snapshot loader.
    // Content: "smoke\n" (UTF-8, LF, no BOM). Kept as a canary so that
    // the readSnapshot helper is never unused (compiler warning hazard on
    // TreatWarningsAsErrors) and future fixture PRs can verify the loader works.
    let actual = readSnapshot "_smoke"
    Assert.Equal("smoke\n", actual)
