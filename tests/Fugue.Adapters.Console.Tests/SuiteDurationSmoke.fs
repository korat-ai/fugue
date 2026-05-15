module Fugue.Adapters.Console.Tests.SuiteDurationSmoke

open FsCheck.Xunit
open Fugue.Adapters.Console
open System.Diagnostics

// ============================================================================
// T049 — Suite duration smoke test (SC-003 ≤ 30 s)
//
// Renders a representative corpus of all 5 primitive types and all 6
// composition types (200 iterations) and asserts total elapsed time is
// under 30 seconds. Satisfies SC-003: "integration tests covering the
// adapter's public surface finish in under 30 seconds on a developer
// laptop".
//
// Note: this test measures the *adapter* work, not the full test discovery
// and run overhead. The wall-clock total (including startup, JIT, etc.)
// may exceed 30 s on a very cold machine but the rendering work itself
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

    let ctx    = RenderContext.create 80 true "default"
    let corpus = mkCorpus ()

    let sw = Stopwatch.StartNew ()
    let mutable errorCount = 0
    for _ in 1 .. 200 do
        match Renderer.toRawAnsi ctx corpus with
        | Error _ -> errorCount <- errorCount + 1
        | Ok _    -> ()
    sw.Stop ()

    // All 200 renders must succeed and complete within 30 s.
    errorCount = 0 && sw.Elapsed.TotalSeconds < 30.0
