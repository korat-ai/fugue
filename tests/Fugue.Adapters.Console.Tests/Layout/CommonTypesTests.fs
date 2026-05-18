module Fugue.Adapters.Console.Tests.Layout.CommonTypesTests

// see Helpers.fs FACT_DISCOVERY for why all tests are [<Property>]
open FsCheck.Xunit
open Fugue.Adapters.Console
open Fugue.Adapters.Console.Layout

// ============================================================================
// T008 — Exhaustive-match smoke tests for common Layout DUs.
//
// Purpose: ensure every case of each DU is reachable. A bare `_ ->` wildcard
// that masked a forgotten case would cause this test to pass trivially — the
// exhaustive multi-arm match forces every case to appear at least once in
// compiled code, so adding a new case without updating this test will produce
// a compiler warning (incomplete-match) that fails the build under
// TreatWarningsAsErrors=true.
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T008a — Orientation: all cases are reachable`` () =
    let allCases = [Vertical; Horizontal]
    allCases
    |> List.forall (fun o ->
        match o with
        | Vertical   -> true
        | Horizontal -> true)

[<Property(MaxTest = 1)>]
let ``T008b — CrossAxisAlignment: all cases are reachable`` () =
    let allCases = [Start; Center; End_; Stretch]
    allCases
    |> List.forall (fun a ->
        match a with
        | Start   -> true
        | Center  -> true
        | End_    -> true
        | Stretch -> true)

[<Property(MaxTest = 1)>]
let ``T008c — Overflow: all cases are reachable`` () =
    let allCases = [Clip; Strict; WrapNext]
    allCases
    |> List.forall (fun ov ->
        match ov with
        | Clip     -> true
        | Strict   -> true
        | WrapNext -> true)

// ============================================================================
// T008d — LayoutDepth.layoutDepth: Foreign with embeddedDepth (CEO b.1).
//
// Verifies that Composition.Foreign (_, Some n) returns n from layoutDepth
// (honoring the embedded depth), and Foreign (_, None) returns 1 (existing
// behaviour preserved). This is the key invariant of the CEO option (b.1)
// redesign that enables ofDock / ofLayoutGrid / ofFlex to use the
// lazy-IRenderable pattern while still tracking depth correctly.
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T008d — LayoutDepth.layoutDepth honors Foreign embeddedDepth = Some n`` () =
    // Build a Foreign node with embeddedDepth = Some 42 via the internal factory.
    // Renderable.fromSpectre with a real IRenderable (Spectre.Console.Text).
    let inner : Spectre.Console.Rendering.IRenderable =
        Spectre.Console.Text("test") :> _
    let r   = Renderable.fromSpectre inner
    let comp = Composition.ofRenderableWithDepth r 42
    // layoutDepth must return 42 (the embedded depth), not 1.
    LayoutDepth.layoutDepth comp = 42

[<Property(MaxTest = 1)>]
let ``T008e — LayoutDepth.layoutDepth treats Foreign embeddedDepth = None as depth 1`` () =
    // ofRenderable uses embeddedDepth = None — depth must be 1.
    let inner : Spectre.Console.Rendering.IRenderable =
        Spectre.Console.Text("test") :> _
    let r    = Renderable.fromSpectre inner
    let comp = Composition.ofRenderable r
    LayoutDepth.layoutDepth comp = 1
