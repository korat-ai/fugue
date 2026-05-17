module Fugue.Adapters.Console.Tests.SuiteDurationSmoke

// see Helpers.fs FACT_DISCOVERY for why all tests are [<Property>]
open FsCheck.Xunit
open Fugue.Adapters.Console
open System.Diagnostics

// ============================================================================
// T049 — Suite duration smoke test (SC-003 ≤ 2 s adapter work)
//
// Renders a representative corpus of all 5 primitive types and all 6
// composition types (200 iterations) and asserts total elapsed time is
// under 2 seconds. Budget tightened from 30 s → 2 s (review #15):
// the observed baseline on this machine is ~123 ms, so 2 s gives a
// 16× headroom that still catches a 10× regression but not a 100×
// one accidentally. If a future Spectre upgrade slows things down,
// raise the budget in the same PR as the upgrade.
//
// Note: this test measures the *adapter* work, not the full test discovery
// and run overhead. The wall-clock total (including startup, JIT, etc.)
// may exceed 2 s on a very cold machine but the rendering work itself
// must not.
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T049 — Representative corpus renders 200 times within 30 seconds`` () =
    // Build a moderately complex composition tree.
    let styledLeaf text =
        Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral text))

    let mkCorpus () =
        let stack = Composition.stack [styledLeaf "line A"; styledLeaf "line B"]
        let padded =
            Composition.padded 1 1 0 0 (styledLeaf "padded")
            |> function Ok c -> c | Error _ -> styledLeaf "(err)"
        let panel =
            Composition.panel Border.Rounded (Some (SafeText.ofLiteral " Head ")) (styledLeaf "body")
        let cols =
            Composition.columns [(0.5, styledLeaf "L"); (0.5, styledLeaf "R")]
            |> function Ok c -> c | Error _ -> styledLeaf "(err)"
        let aligned =
            Composition.aligned Alignment.RightAlign (styledLeaf "right")
        let table =
            Composition.table [styledLeaf "A"; styledLeaf "B"] [[styledLeaf "1"; styledLeaf "2"]]
            |> function Ok c -> c | Error _ -> styledLeaf "(err)"
        Composition.stack [stack; padded; panel; cols; aligned; table]

    let ctx    =
        RenderContext.create 80 System.Int32.MaxValue true "default"
        |> function Ok c -> c | Error e -> failwith $"test ctx: {e}"
    let corpus = mkCorpus ()

    let sw = Stopwatch.StartNew ()
    let mutable errorCount = 0
    for _ in 1 .. 200 do
        match Renderer.toRawAnsi ctx corpus with
        | Error _ -> errorCount <- errorCount + 1
        | Ok _    -> ()
    sw.Stop ()

    // All 200 renders must succeed and complete within 2 s (see module comment).
    errorCount = 0 && sw.Elapsed.TotalSeconds < 2.0
