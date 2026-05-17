module Fugue.Adapters.Console.Tests.CompositionTests

// see Helpers.fs FACT_DISCOVERY for why all tests are [<Property>]
open FsCheck.Xunit
open Fugue.Adapters.Console

let private ctx80 () =
    RenderContext.create 80 System.Int32.MaxValue true "default"
    |> function Ok c -> c | Error e -> failwith $"test ctx: {e}"

// ============================================================================
// T032 — Stack
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T032 — Stack of two Leaf nodes produces non-empty output`` () =
    let ctx = ctx80 ()
    let a = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "first"))
    let b = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "second"))
    let comp = Composition.stack [a; b]
    match Renderer.toRawAnsi ctx comp with
    | Ok s  -> s.Contains "first" && s.Contains "second"
    | Error _ -> false

[<Property(MaxTest = 1)>]
let ``T032b — Stack of empty list renders without error`` () =
    let ctx = ctx80 ()
    let comp = Composition.stack []
    match Renderer.toRawAnsi ctx comp with
    | Ok _  -> true
    | Error _ -> false

// ============================================================================
// T033 — Padded
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T033 — Padded 2 4 0 0 renders inner content without error`` () =
    let ctx = ctx80 ()
    let inner = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "padded"))
    match Composition.padded 2 4 0 0 inner with
    | Error _ -> false
    | Ok comp ->
        match Renderer.toRawAnsi ctx comp with
        | Ok s  -> s.Contains "padded"
        | Error _ -> false

[<Property(MaxTest = 1)>]
let ``T033b — padded with negative left returns InvalidArgument`` () =
    let inner = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "x"))
    match Composition.padded -1 0 0 0 inner with
    | Error (RenderError.InvalidArgument _) -> true
    | _ -> false

[<Property(MaxTest = 1)>]
let ``T033c — padded with negative right returns InvalidArgument`` () =
    let inner = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "x"))
    match Composition.padded 0 -1 0 0 inner with
    | Error (RenderError.InvalidArgument _) -> true
    | _ -> false

[<Property(MaxTest = 1)>]
let ``T033d — padded with negative top returns InvalidArgument`` () =
    let inner = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "x"))
    match Composition.padded 0 0 -3 0 inner with
    | Error (RenderError.InvalidArgument _) -> true
    | _ -> false

// ============================================================================
// T034 — Panel
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T034 — Panel Rounded with header renders content and non-empty output`` () =
    let ctx = ctx80 ()
    let inner = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "panel content"))
    let comp = Composition.panel Border.Rounded (Some (SafeText.ofLiteral " Header ")) inner
    match Renderer.toRawAnsi ctx comp with
    | Ok s  -> s.Contains "panel content"
    | Error _ -> false

[<Property(MaxTest = 1)>]
let ``T034b — Panel with None header renders without error`` () =
    let ctx = ctx80 ()
    let inner = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "no header"))
    let comp = Composition.panel Border.Square None inner
    match Renderer.toRawAnsi ctx comp with
    | Ok s  -> s.Contains "no header"
    | Error _ -> false

[<Property(MaxTest = 1)>]
let ``T034c — Panel Heavy border renders without error`` () =
    let ctx = ctx80 ()
    let inner = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "heavy"))
    let comp = Composition.panel Border.Heavy None inner
    match Renderer.toRawAnsi ctx comp with
    | Ok _ -> true
    | Error _ -> false

// ============================================================================
// T034d — Panel header stress: Unicode, long text, bracket chars, empty-after-escape
// (Fix #12 — escape regression guard)
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T034d — Panel with Unicode header renders without throw`` () =
    // Unicode header text: contains multi-byte chars and emoji-adjacent codepoints.
    let ctx    = ctx80 ()
    let header = SafeText.ofLiteral " Héllo — wörld · 🎵 "
    let inner  = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "body"))
    let comp   = Composition.panel Border.Rounded (Some header) inner
    try
        match Renderer.toRawAnsi ctx comp with
        | Ok s  -> s.Contains "body"
        | Error _ -> false
    with _ ->
        false

[<Property(MaxTest = 1)>]
let ``T034e — Panel with very long header (>200 chars) renders without throw`` () =
    let ctx    = ctx80 ()
    let longHdr = System.String('A', 210)
    let header = SafeText.ofLiteral longHdr
    let inner  = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "body"))
    let comp   = Composition.panel Border.Square (Some header) inner
    try
        match Renderer.toRawAnsi ctx comp with
        | Ok s  -> s.Contains "body"
        | Error _ -> false
    with _ ->
        false

[<Property(MaxTest = 1)>]
let ``T034f — Panel header with '[' chars via ofUser renders without throw (escape regression)`` () =
    // Spectre interprets '[' as markup start; SafeText.ofUser escapes brackets via
    // Markup.Escape, so the panel header is rendered as literal text, not markup.
    let ctx    = ctx80 ()
    let header = SafeText.ofUser " [Status] — [OK] "
    let inner  = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "body"))
    let comp   = Composition.panel Border.Rounded (Some header) inner
    try
        // Must not throw; Spectre sees escaped "[[Status]]" etc., no parse error.
        match Renderer.toRawAnsi ctx comp with
        | Ok s  -> s.Contains "body"
        | Error _ -> false
    with _ ->
        false

// ============================================================================
// T035 — Columns
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T035 — columns 0.3 0.7 renders both children`` () =
    let ctx = ctx80 ()
    let a = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "left"))
    let b = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "right"))
    match Composition.columns [(0.3, a); (0.7, b)] with
    | Error _ -> false
    | Ok comp ->
        match Renderer.toRawAnsi ctx comp with
        | Ok s  -> s.Contains "left" && s.Contains "right"
        | Error _ -> false

[<Property(MaxTest = 1)>]
let ``T035b — columns with ratio sum > 1.0 returns InvalidArgument`` () =
    let a = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "x"))
    let b = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "y"))
    match Composition.columns [(0.6, a); (0.6, b)] with
    | Error (RenderError.InvalidArgument _) -> true
    | _ -> false

[<Property(MaxTest = 1)>]
let ``T035c — columns with negative ratio returns InvalidArgument`` () =
    let a = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "x"))
    match Composition.columns [(-0.1, a)] with
    | Error (RenderError.InvalidArgument _) -> true
    | _ -> false

[<Property(MaxTest = 1)>]
let ``T035d — columns with individual ratio > 1.0 returns InvalidArgument`` () =
    let a = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "x"))
    match Composition.columns [(1.5, a)] with
    | Error (RenderError.InvalidArgument _) -> true
    | _ -> false

// ============================================================================
// T036 — Aligned
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T036 — Aligned RightAlign renders inner content without error`` () =
    let ctx = ctx80 ()
    let inner = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "right-aligned"))
    let comp = Composition.aligned Alignment.RightAlign inner
    match Renderer.toRawAnsi ctx comp with
    | Ok s  -> s.Contains "right-aligned"
    | Error _ -> false

[<Property(MaxTest = 1)>]
let ``T036b — Aligned CentreAlign renders without error`` () =
    let ctx = ctx80 ()
    let inner = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "centred"))
    let comp = Composition.aligned Alignment.CentreAlign inner
    match Renderer.toRawAnsi ctx comp with
    | Ok s  -> s.Contains "centred"
    | Error _ -> false

[<Property(MaxTest = 1)>]
let ``T036c — Aligned LeftAlign renders without error`` () =
    let ctx = ctx80 ()
    let inner = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "left-aligned"))
    let comp = Composition.aligned Alignment.LeftAlign inner
    match Renderer.toRawAnsi ctx comp with
    | Ok s  -> s.Contains "left-aligned"
    | Error _ -> false

// ============================================================================
// T037 — Table
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T037 — table 2 headers 3 rows renders all cell content`` () =
    let ctx = ctx80 ()
    let headers = [
        Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "Name"))
        Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "Value"))
    ]
    let rows : Composition seq seq = seq [
        seq [Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "key1"))
             Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "val1"))]
        seq [Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "key2"))
             Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "val2"))]
        seq [Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "key3"))
             Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "val3"))]
    ]
    match Composition.table headers rows with
    | Error _ -> false
    | Ok comp ->
        match Renderer.toRawAnsi ctx comp with
        | Ok s ->
            s.Contains "Name" && s.Contains "Value"
            && s.Contains "key1" && s.Contains "val3"
        | Error _ -> false

[<Property(MaxTest = 1)>]
let ``T037b — table with ragged rows returns InvalidArgument`` () =
    let headers = [
        Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "A"))
        Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "B"))
    ]
    let rows = [
        // Only 1 cell instead of 2.
        seq [Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "only-one"))]
    ]
    match Composition.table headers rows with
    | Error (RenderError.InvalidArgument _) -> true
    | _ -> false

[<Property(MaxTest = 1)>]
let ``T037c — table with empty headers returns EmptyComposition`` () =
    let rows : Composition seq seq = seq [seq [Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "cell"))]]
    match Composition.table [] rows with
    | Error (RenderError.EmptyComposition _) -> true
    | _ -> false

// ============================================================================
// T039 — stack single-element property
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T039 — stack of single leaf renders same content as the leaf itself`` () =
    let ctx = ctx80 ()
    let leaf = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "singleton"))
    let stacked = Composition.stack [leaf]
    match Renderer.toRawAnsi ctx leaf, Renderer.toRawAnsi ctx stacked with
    | Ok direct, Ok stk ->
        // Both must contain the content. Exact byte equality is not guaranteed
        // because Spectre may add surrounding newlines for Rows vs Text display.
        direct.Contains "singleton" && stk.Contains "singleton"
    | _ -> false
