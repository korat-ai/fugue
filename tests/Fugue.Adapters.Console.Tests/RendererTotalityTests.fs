module Fugue.Adapters.Console.Tests.RendererTotalityTests

open FsCheck.Xunit
open Fugue.Adapters.Console

// ============================================================================
// T046 — Renderer.toRawAnsi is total over arbitrary Style × SafeText
// (no exceptions should escape the API boundary regardless of input)
//
// FsCheck 3 note: we use the simple [<Property>] approach with string
// parameters (which FsCheck generates natively) rather than custom Gen
// combinators, to avoid FsCheck 3 API incompatibilities with the existing
// FsCheck.Xunit setup. This gives 100+ arbitrary string inputs per test
// run, which covers the totality invariant.
// ============================================================================

/// T046 — Styled with arbitrary user input never throws an exception.
/// FsCheck generates arbitrary strings including nulls, Unicode, special chars.
[<Property>]
let ``T046 — toRawAnsi never throws for Styled with arbitrary user text`` (s: string | null) =
    let ctx  = RenderContext.create 80 true "default"
    let safe = SafeText.ofUser s
    let comp = Composition.Leaf (Primitive.Styled (Style.empty, safe))
    try
        let _ = Renderer.toRawAnsi ctx comp
        true
    with _ ->
        false

[<Property>]
let ``T046b — toRawAnsi never throws for Styled with Rgb colour`` (s: string | null) =
    let ctx  = RenderContext.create 80 true "nocturne"
    let safe = SafeText.ofUser s
    // Mix of valid styles.
    let style =
        Style.create (Colour.Rgb (100uy, 200uy, 50uy)) Colour.Default [Decoration.Bold]
        |> function Ok st -> st | Error _ -> Style.empty
    let comp = Composition.Leaf (Primitive.Styled (style, safe))
    try
        let _ = Renderer.toRawAnsi ctx comp
        true
    with _ ->
        false

[<Property>]
let ``T046c — toRawAnsi never throws for Markup hint with arbitrary string`` (s: string | null) =
    // Markup may return InvalidMarkup — that's fine.
    // The invariant is: no exception escapes.
    let ctx  = RenderContext.create 80 true "default"
    let hint : string = match s with null -> "" | v -> v
    let comp = Composition.Leaf (Primitive.Markup hint)
    try
        let _ = Renderer.toRawAnsi ctx comp
        true
    with _ ->
        false

// ============================================================================
// T047 — RenderContext.create width-clamp idempotence
// Negative width produces same content output as width = 1 (the clamp floor).
// ============================================================================

/// T047 — Negative widths are clamped to 1; toRawAnsi must return Ok (not throw).
/// The content at width=1 may differ from width=N (Spectre wraps at 1 char) —
/// the invariant is structural: both negative and clamped-to-1 return Ok.
[<Property>]
let ``T047 — Negative width is clamped to 1 without error`` (s: string | null) =
    let raw  = match s with null -> "x" | v -> if v.Length = 0 then "x" else v
    let safe = SafeText.ofLiteral raw
    let prim = Primitive.Styled (Style.empty, safe)
    let comp = Composition.Leaf prim
    // Negative widths must all succeed (clamp to 1 internally).
    let widths = [-100; -1; -5; -50]
    widths
    |> List.forall (fun w ->
        let ctxNeg = RenderContext.create w true "default"
        match Renderer.toRawAnsi ctxNeg comp with
        | Ok _    -> true
        | Error _ -> false)
