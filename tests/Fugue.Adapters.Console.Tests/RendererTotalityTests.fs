module Fugue.Adapters.Console.Tests.RendererTotalityTests

// see Helpers.fs FACT_DISCOVERY for why all tests are [<Property>]
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
// T046d-T046g — totality over every remaining Primitive case
// (data-model invariant 5: Renderer.toRawAnsi is total over all Primitive DU cases)
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T046d — toRawAnsi never throws for LineBreak`` () =
    let ctx  = RenderContext.create 80 true "default"
    let comp = Composition.Leaf Primitive.LineBreak
    try let _ = Renderer.toRawAnsi ctx comp in true
    with _ -> false

[<Property(MaxTest = 1)>]
let ``T046e — toRawAnsi never throws for RawAnsi`` () =
    let ctx  = RenderContext.create 80 true "default"
    let comp = Composition.Leaf (Primitive.RawAnsi "\x1b[31mfoo\x1b[0m")
    try let _ = Renderer.toRawAnsi ctx comp in true
    with _ -> false

[<Property(MaxTest = 1)>]
let ``T046f — toRawAnsi never throws for RawAnsi empty string`` () =
    let ctx  = RenderContext.create 80 true "default"
    let comp = Composition.Leaf (Primitive.RawAnsi "")
    try let _ = Renderer.toRawAnsi ctx comp in true
    with _ -> false

[<Property(MaxTest = 1)>]
let ``T046g — toRawAnsi never throws for Rule`` () =
    let ctx  = RenderContext.create 80 true "default"
    let comp = Composition.Leaf (Primitive.Rule Style.empty)
    try let _ = Renderer.toRawAnsi ctx comp in true
    with _ -> false

// ============================================================================
// T046h-T046n — totality over every Composition shape
// Invariant: Renderer.toRawAnsi returns Result, never throws, for every
// Composition DU case including degenerate inputs.
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T046h — toRawAnsi never throws for Stack of empty list`` () =
    let ctx  = RenderContext.create 80 true "default"
    let comp = Composition.stack []
    try let _ = Renderer.toRawAnsi ctx comp in true
    with _ -> false

[<Property(MaxTest = 1)>]
let ``T046i — toRawAnsi never throws for Stack of multiple mixed primitives`` () =
    let ctx  = RenderContext.create 80 true "default"
    let leaf s = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral s))
    let comp =
        Composition.stack [
            leaf "line A"
            Composition.Leaf Primitive.LineBreak
            leaf "line B"
            Composition.Leaf (Primitive.RawAnsi "\x1b[0m")
            Composition.Leaf (Primitive.Rule Style.empty)
        ]
    try let _ = Renderer.toRawAnsi ctx comp in true
    with _ -> false

[<Property(MaxTest = 1)>]
let ``T046j — toRawAnsi never throws for Padded (zero padding)`` () =
    let ctx  = RenderContext.create 80 true "default"
    let inner = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "x"))
    match Composition.padded 0 0 0 0 inner with
    | Error _ -> false
    | Ok comp ->
        try let _ = Renderer.toRawAnsi ctx comp in true
        with _ -> false

[<Property(MaxTest = 1)>]
let ``T046k — toRawAnsi never throws for Panel with no header`` () =
    let ctx  = RenderContext.create 80 true "default"
    let inner = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "body"))
    let comp  = Composition.panel Border.Rounded None inner
    try let _ = Renderer.toRawAnsi ctx comp in true
    with _ -> false

[<Property(MaxTest = 1)>]
let ``T046l — toRawAnsi never throws for Panel with header`` () =
    let ctx  = RenderContext.create 80 true "default"
    let inner = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "body"))
    let comp  = Composition.panel Border.Square (Some (SafeText.ofLiteral " Title ")) inner
    try let _ = Renderer.toRawAnsi ctx comp in true
    with _ -> false

[<Property(MaxTest = 1)>]
let ``T046m — toRawAnsi never throws for Columns`` () =
    let ctx  = RenderContext.create 80 true "default"
    let leaf s = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral s))
    match Composition.columns [(0.5, leaf "L"); (0.5, leaf "R")] with
    | Error _ -> false
    | Ok comp ->
        try let _ = Renderer.toRawAnsi ctx comp in true
        with _ -> false

[<Property(MaxTest = 1)>]
let ``T046n — toRawAnsi never throws for Aligned`` () =
    let ctx  = RenderContext.create 80 true "default"
    let inner = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "right"))
    let comp  = Composition.aligned Alignment.RightAlign inner
    try let _ = Renderer.toRawAnsi ctx comp in true
    with _ -> false

[<Property(MaxTest = 1)>]
let ``T046o — toRawAnsi never throws for Table (empty rows)`` () =
    let ctx  = RenderContext.create 80 true "default"
    let hdr  = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "Col"))
    match Composition.table [hdr] [] with
    | Error _ -> false
    | Ok comp ->
        try let _ = Renderer.toRawAnsi ctx comp in true
        with _ -> false

[<Property(MaxTest = 1)>]
let ``T046p — toRawAnsi never throws for Table (single row)`` () =
    let ctx  = RenderContext.create 80 true "default"
    let leaf s = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral s))
    match Composition.table [leaf "H"] [[leaf "cell"]] with
    | Error _ -> false
    | Ok comp ->
        try let _ = Renderer.toRawAnsi ctx comp in true
        with _ -> false

[<Property(MaxTest = 1)>]
let ``T046q — toRawAnsi never throws for Table (multi-row)`` () =
    let ctx  = RenderContext.create 80 true "default"
    let leaf s = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral s))
    match Composition.table [leaf "A"; leaf "B"]
                            [[leaf "1"; leaf "2"]; [leaf "3"; leaf "4"]] with
    | Error _ -> false
    | Ok comp ->
        try let _ = Renderer.toRawAnsi ctx comp in true
        with _ -> false

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
