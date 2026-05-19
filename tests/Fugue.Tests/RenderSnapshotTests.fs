module Fugue.Tests.RenderSnapshotTests

// Per-render-path snapshot tests. Fixtures live under tests/Fugue.Tests/Snapshots/*.txt
// and assert byte-for-byte equality against Renderer.toRawAnsi output for a deterministic
// Composition input. See specs/005-cli-layout-migration/contracts/snapshot-fixture-shape.md
// for the format contract.
//
// NOTE — deterministic render context vs. production probe:
// Tests render via fixed 80×24 RenderContext.create; production Surface.fs uses
// RenderContext.probe(). These tests assert renderer correctness for a deterministic
// input; visual parity vs the running binary is verified manually via canonical
// scripted session (data-model.md §4).

open System.IO
open Xunit
open Fugue.Adapters.Console
open Fugue.Surface

/// Load a snapshot fixture by name (e.g. "surface_status_bar"). Returns the raw bytes
/// as a string with LF line endings preserved.
let private readSnapshot (name: string) : string =
    let path = Path.Combine("Snapshots", $"{name}.txt")
    File.ReadAllText path

/// Render a Composition to raw ANSI at 80×24 with colour enabled.
///
/// Snapshot fixtures are stored as canonical LF (per
/// specs/005-cli-layout-migration/contracts/snapshot-fixture-shape.md).
/// Spectre's renderer emits platform-specific line endings (CRLF on Windows
/// via Environment.NewLine). Normalize CRLF → LF in actual output so byte-
/// equality assertions are cross-platform stable — line-ending is a
/// presentation detail, not a renderer semantic worth asserting separately.
let private render (comp: Composition) : string =
    let ctx =
        match RenderContext.create 80 24 true "default" with
        | Ok c -> c
        | Error e -> failwithf "test context failed: %A" e
    match Renderer.toRawAnsi ctx comp with
    | Ok ansi -> ansi.Replace("\r\n", "\n")
    | Error e -> failwithf "render failed: %A" e

[<Fact>]
let ``readSnapshot loads _smoke.txt fixture correctly`` () =
    // _smoke.txt is a permanent self-test of the snapshot loader.
    // Content: "smoke\n" (UTF-8, LF, no BOM). Kept as a canary so that
    // the readSnapshot helper is never unused (compiler warning hazard on
    // TreatWarningsAsErrors) and future fixture PRs can verify the loader works.
    let actual = readSnapshot "_smoke"
    Assert.Equal("smoke\n", actual)

[<Fact>]
let ``surface_status_bar snapshot matches renderer output for status-bar markup`` () =
    // Fixture: dim-styled model name, context percentage, elapsed timer.
    // Regenerate with: dotnet fsi tools/gen_snapshots.fsx
    let comp =
        Composition.Leaf (Primitive.Markup "[dim]claude-3-5-sonnet[/] [dim]42%[/] [dim]00:01:23[/]")
    let actual   = render comp
    let expected = readSnapshot "surface_status_bar"
    Assert.Equal(expected, actual)

[<Fact>]
let ``surface_markdown_basic snapshot matches renderer output for heading and paragraphs`` () =
    // Fixture: bold heading + two paragraphs, one with italic emphasis.
    let heading = Composition.Leaf (Primitive.Markup "[bold]Hello World[/]")
    let para1   = Composition.Leaf (Primitive.Markup "This is a basic paragraph of text.")
    let para2   = Composition.Leaf (Primitive.Markup "Second paragraph with [italic]emphasis[/].")
    let comp    = Composition.Stack [heading; para1; para2]
    let actual   = render comp
    let expected = readSnapshot "surface_markdown_basic"
    Assert.Equal(expected, actual)

[<Fact>]
let ``surface_diff_basic snapshot matches renderer output for diff-styled lines`` () =
    // Fixture: green added line, red removed line, plain context line.
    let added   = Composition.Leaf (Primitive.Markup "[green]+ added line[/]")
    let removed = Composition.Leaf (Primitive.Markup "[red]- removed line[/]")
    let ctx     = Composition.Leaf (Primitive.Markup "  context line")
    let comp    = Composition.Stack [added; removed; ctx]
    let actual   = render comp
    let expected = readSnapshot "surface_diff_basic"
    Assert.Equal(expected, actual)
