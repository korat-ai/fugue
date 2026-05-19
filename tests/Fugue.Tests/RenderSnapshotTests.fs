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
