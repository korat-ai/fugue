module Fugue.Tests.RenderableFromSpectreCountTest

// Standing invariant test for the Renderable.fromSpectre retirement path.
//
// Phase 4 introduces uses of Renderable.fromSpectre in src/Fugue.Cli/ as a
// consequence of PR P1's Surface.fs signature change (IRenderable -> Renderable).
// These wraps retire as PR P2 / P3 / P4 migrate the producer Render*.fs files
// to return Composition instead of IRenderable.
//
// This test asserts the count is at or below the expected ceiling for the
// current phase. The ceiling tightens with each merged PR. If a future commit
// adds wraps beyond the ceiling, the build fails — making the wraps a
// constrained budget, not an unbounded growth surface.
//
// See specs/005-cli-layout-migration/spec.md FR-002, FR-014 + reviewer
// finding "Concern A" + CEO decision A1 dated 2026-05-19.

open System.IO
open System.Text.RegularExpressions
open Xunit

/// Maximum allowed Renderable.fromSpectre call sites in src/Fugue.Cli/*.fs
/// (excluding the two designated bridge files LayoutHost.fs and RenderBridge.fs).
///
/// PR-P1 ceiling: 40 (current state after Surface.fs migration).
/// After PR-P2 (Render+Doctor producers -> Composition): <= 30 expected.
/// After PR-P3 (Markdown+Stream producers -> Composition): <= 10 expected.
/// After PR-P4 (Diff+StackTrace+Picker producers -> Composition): <= 1 expected
///   (just the one legitimate Phase 3 baseline use).
///
/// Tighten this ceiling AS PART OF the PR that does the retirement. Do not
/// raise it back — that's a regression of the migration's purpose.
let private maxFromSpectreUses = 40

/// Find all *.fs files under src/Fugue.Cli/, excluding the two bridge files.
let private scanFiles (cliRoot: string) : string list =
    let bridgeFiles = Set.ofList ["LayoutHost.fs"; "RenderBridge.fs"; "LayoutHost.fsi"; "RenderBridge.fsi"]
    Directory.EnumerateFiles(cliRoot, "*.fs")
    |> Seq.filter (fun path -> Set.contains (Path.GetFileName path) bridgeFiles |> not)
    |> Seq.toList

/// Count Renderable.fromSpectre matches in a single file.
let private countMatches (path: string) : int =
    let content = File.ReadAllText path
    Regex.Matches(content, @"Renderable\.fromSpectre").Count

[<Fact>]
let ``Renderable.fromSpectre uses in src/Fugue.Cli stay within the retirement-path ceiling`` () =
    let repoRoot =
        // Walk up from the test DLL's bin directory until we find Fugue.slnx.
        let rec walk (dir: string) =
            if File.Exists (Path.Combine(dir, "Fugue.slnx")) then dir
            else
                match Directory.GetParent dir |> Option.ofObj with
                | None        -> failwith "Could not find repo root (Fugue.slnx)"
                | Some parent -> walk parent.FullName
        walk System.AppContext.BaseDirectory
    let cliRoot = Path.Combine(repoRoot, "src", "Fugue.Cli")
    let files = scanFiles cliRoot
    let total =
        files
        |> List.sumBy countMatches
    let perFile =
        files
        |> List.map (fun p -> Path.GetFileName p, countMatches p)
        |> List.filter (fun (_, n) -> n > 0)
        |> List.sortByDescending snd
    let breakdown =
        perFile
        |> List.map (fun (f, n) -> $"  {f}: {n}")
        |> String.concat "\n"
    let msg =
        $"Renderable.fromSpectre count = {total} exceeds the current retirement-path ceiling of {maxFromSpectreUses}.\n" +
        $"Per-file breakdown (non-zero):\n{breakdown}\n" +
        "If you've migrated a producer to return Composition, REDUCE maxFromSpectreUses\n" +
        "in this test as part of the same commit. Do not raise the ceiling."
    Assert.True(total <= maxFromSpectreUses, msg)
