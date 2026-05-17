module Fugue.Adapters.Console.Tests.RenderableTests

// see Helpers.fs FACT_DISCOVERY for why all tests are [<Property>]
open FsCheck.Xunit
open Fugue.Adapters.Console

let private ctx80 () = RenderContext.create 80 true "default"

// ============================================================================
// T007 — Happy path: Renderable.fromSpectre wraps a real IRenderable and
// can be embedded via Composition.ofRenderable without throwing.
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T007 — Renderable.fromSpectre with Text wraps and embeds successfully`` () =
    // Use Spectre.Console.Text (plain text, no markup parsing) — the
    // simplest non-null IRenderable that always renders without errors.
    let inner = Spectre.Console.Text "Hello" :> Spectre.Console.Rendering.IRenderable
    let r     = Renderable.fromSpectre inner
    let comp  = Composition.ofRenderable r
    // Embedding into a composition tree must not throw and must produce output.
    match Renderer.toRawAnsi (ctx80 ()) comp with
    | Ok output -> output.Length > 0
    | Error _   -> false

// ============================================================================
// T008 — Error path: exception-boundary test.
// A custom IRenderable that throws in its Render method must be caught
// by the adapter and returned as Error (RenderFailed (typeName, _)).
// ============================================================================

/// A minimal IRenderable that always throws when Spectre calls Render.
type private ThrowingRenderable(msg: string) =
    interface Spectre.Console.Rendering.IRenderable with
        member _.Measure(_console, _maxWidth) =
            Spectre.Console.Rendering.Measurement(0, 0)
        member _.Render(_console, _options) =
            raise (System.InvalidOperationException msg)
            Seq.empty

[<Property(MaxTest = 1)>]
let ``T008 — exception in IRenderable.Render is caught as RenderFailed — exception does not propagate`` () =
    let throwing = ThrowingRenderable "deliberate-test-throw"
    let r    = Renderable.fromSpectre (throwing :> Spectre.Console.Rendering.IRenderable)
    let comp = Composition.ofRenderable r
    match Renderer.toRawAnsi (ctx80 ()) comp with
    | Error (RenderError.RenderFailed (typeName, _)) ->
        // typeName must be non-empty (captured at fromSpectre time).
        typeName.Length > 0
    | Ok _  -> false  // exception must NOT be swallowed silently
    | Error _ -> false

// ============================================================================
// T009 — Composition embedding test.
// Wrap a Spectre Text IRenderable; embed inside a Panel with rounded border;
// render; assert output contains both the rounded-border corner character ╭
// and the inner text content.
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T009 — Renderable embedded in Panel renders border and inner text`` () =
    // Use Spectre.Console.Text (plain text, no markup parsing).
    let inner = Spectre.Console.Text "Hello" :> Spectre.Console.Rendering.IRenderable
    let r     = Renderable.fromSpectre inner
    let comp  =
        Composition.panel Border.Rounded None (Composition.ofRenderable r)
    match Renderer.toRawAnsi (ctx80 ()) comp with
    | Ok output ->
        output.Contains "╭"   // rounded border top-left corner
        && output.Contains "Hello"
    | Error _ -> false

// ============================================================================
// T010 — Totality: null IRenderable accepted at fromSpectre; failure surfaces
// at render time as Error (RenderFailed _), never as a thrown exception.
// data-model.md §2.2 null-acceptance rule.
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T010 — null IRenderable accepted at fromSpectre; render time gives RenderFailed not exception`` () =
    let nullIRenderable : Spectre.Console.Rendering.IRenderable | null = null
    let r    = Renderable.fromSpectre nullIRenderable
    let comp = Composition.ofRenderable r
    match Renderer.toRawAnsi (ctx80 ()) comp with
    | Error (RenderError.RenderFailed _) -> true
    | Ok _                               -> false  // null backend must never silently produce output
    | Error _                            -> false  // must be specifically RenderFailed
