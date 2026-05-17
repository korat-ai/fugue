module Fugue.Adapters.Console.Tests.DataDisplaysTests

// see Helpers.fs FACT_DISCOVERY for why all tests are [<Property>]
open FsCheck.Xunit
open Fugue.Adapters.Console
open System.Text.RegularExpressions

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private ctx80colour  () = RenderContext.create 80 true  "default"
let private ctx80nocolour () = RenderContext.create 80 false "default"

/// Strip ANSI CSI escape sequences (\x1b[...m) from a string.
let private stripAnsi (s: string) : string =
    Regex.Replace(s, @"\x1b\[[0-?]*[ -/]*[@-~]", "")

let private renderOk ctx comp =
    match Renderer.toRawAnsi ctx comp with
    | Ok s    -> s
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"

// ============================================================================
// T017 — Tree
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T017a — Tree: single-leaf tree returns Ok and renders non-empty output`` () =
    let leaf = TreeNode.leaf (SafeText.ofLiteral "root")
    match Tree.create leaf with
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"
    | Ok tree ->
        let comp = Tree.toComposition tree
        match Renderer.toRawAnsi (ctx80colour ()) comp with
        | Ok output -> output.Length > 0
        | Error e   -> failwith $"Expected render Ok but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T017b — Tree: depth > 1000 returns Error InvalidArgument`` () =
    // Build a chain of depth 1001 by nesting branches.
    let leaf = TreeNode.leaf (SafeText.ofLiteral "leaf")
    let rec buildDeep n node =
        if n = 0 then node
        else buildDeep (n - 1) (TreeNode.branch (SafeText.ofLiteral $"node-{n}") [ node ])
    let deepRoot = buildDeep 1001 leaf
    match Tree.create deepRoot with
    | Error (RenderError.InvalidArgument ("Tree", _)) -> true
    | Error e -> failwith $"Expected InvalidArgument(Tree) but got: %A{e}"
    | Ok _    -> failwith "Expected Error for depth > 1000 but got Ok"

// ============================================================================
// T018 — Json
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T018a — Json: valid JSON string returns Ok and renders non-empty output`` () =
    match Json.create "{}" with
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"
    | Ok json ->
        let comp = Json.toComposition json
        match Renderer.toRawAnsi (ctx80colour ()) comp with
        | Ok output -> output.Length > 0
        | Error e   -> failwith $"Expected render Ok but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T018b — Json: malformed JSON returns Error InvalidArgument`` () =
    match Json.create "not-valid-json{{{" with
    | Error (RenderError.InvalidArgument ("Json", _)) -> true
    | Error e -> failwith $"Expected InvalidArgument(Json) but got: %A{e}"
    | Ok _    -> failwith "Expected Error for malformed JSON but got Ok"

[<Property(MaxTest = 1)>]
let ``T018c — Json: payload > 10 MiB returns Error InvalidArgument`` () =
    // Build a JSON array string just over 10 MiB.
    let tenMiBPlusOne = 10 * 1024 * 1024 + 1
    // Use a string of spaces inside a JSON string — valid JSON but huge.
    let inner = System.String(' ', tenMiBPlusOne - 4) // account for [""] wrapper
    let bigJson = $"""["{inner}"]"""
    match Json.create bigJson with
    | Error (RenderError.InvalidArgument ("Json", _)) -> true
    | Error e -> failwith $"Expected InvalidArgument(Json) but got: %A{e}"
    | Ok _    -> failwith "Expected Error for > 10 MiB payload but got Ok"

// ============================================================================
// T019 — BarChart
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T019a — BarChart: valid item + chart returns Ok and renders non-empty output`` () =
    match BarChartItem.create (SafeText.ofLiteral "Item A") 42.0 Colour.Default with
    | Error e -> failwith $"BarChartItem.create Error: %A{e}"
    | Ok item ->
        match BarChart.create None [ item ] 80 with
        | Error e -> failwith $"BarChart.create Error: %A{e}"
        | Ok chart ->
            let comp = BarChart.toComposition chart
            match Renderer.toRawAnsi (ctx80colour ()) comp with
            | Ok output -> output.Length > 0
            | Error e   -> failwith $"Expected render Ok but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T019b — BarChartItem: negative value returns Error InvalidArgument`` () =
    match BarChartItem.create (SafeText.ofLiteral "neg") -1.0 Colour.Default with
    | Error (RenderError.InvalidArgument ("BarChartItem", _)) -> true
    | Error e -> failwith $"Expected InvalidArgument(BarChartItem) but got: %A{e}"
    | Ok _    -> failwith "Expected Error for negative value but got Ok"

[<Property(MaxTest = 1)>]
let ``T019c — BarChart: empty items list returns Error EmptyComposition`` () =
    match BarChart.create None [] 80 with
    | Error (RenderError.EmptyComposition "BarChart: at least one item required") -> true
    | Error e -> failwith $"Expected EmptyComposition but got: %A{e}"
    | Ok _    -> failwith "Expected Error for empty items but got Ok"

// ============================================================================
// T020 — BreakdownChart
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T020a — BreakdownChart: valid items + width returns Ok and renders`` () =
    match BreakdownChartItem.create (SafeText.ofLiteral "Slice") 50.0 (Colour.Rgb (0uy, 128uy, 255uy)) with
    | Error e -> failwith $"BreakdownChartItem.create Error: %A{e}"
    | Ok item ->
        match BreakdownChart.create [ item ] 80 with
        | Error e -> failwith $"BreakdownChart.create Error: %A{e}"
        | Ok chart ->
            let comp = BreakdownChart.toComposition chart
            match Renderer.toRawAnsi (ctx80colour ()) comp with
            | Ok output -> output.Length > 0
            | Error e   -> failwith $"Expected render Ok but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T020b — BreakdownChart: empty items list returns Error EmptyComposition`` () =
    match BreakdownChart.create [] 80 with
    | Error (RenderError.EmptyComposition _) -> true
    | Error e -> failwith $"Expected EmptyComposition but got: %A{e}"
    | Ok _    -> failwith "Expected Error for empty items but got Ok"

[<Property(MaxTest = 1)>]
let ``T020c — BreakdownChartItem: negative value returns Error InvalidArgument`` () =
    match BreakdownChartItem.create (SafeText.ofLiteral "neg") -5.0 Colour.Default with
    | Error (RenderError.InvalidArgument ("BreakdownChartItem", _)) -> true
    | Error e -> failwith $"Expected InvalidArgument(BreakdownChartItem) but got: %A{e}"
    | Ok _    -> failwith "Expected Error for negative value but got Ok"

// ============================================================================
// T021 — Calendar
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T021a — Calendar: valid year/month with empty events returns Ok and renders`` () =
    match Calendar.create 2026 5 [] with
    | Error e -> failwith $"Calendar.create Error: %A{e}"
    | Ok cal ->
        let comp = Calendar.toComposition cal
        match Renderer.toRawAnsi (ctx80colour ()) comp with
        | Ok output -> output.Length > 0
        | Error e   -> failwith $"Expected render Ok but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T021b — Calendar: month = 0 returns Error InvalidArgument`` () =
    match Calendar.create 2026 0 [] with
    | Error (RenderError.InvalidArgument ("Calendar", _)) -> true
    | Error e -> failwith $"Expected InvalidArgument(Calendar) but got: %A{e}"
    | Ok _    -> failwith "Expected Error for month=0 but got Ok"

[<Property(MaxTest = 1)>]
let ``T021c — Calendar: month = 13 returns Error InvalidArgument`` () =
    match Calendar.create 2026 13 [] with
    | Error (RenderError.InvalidArgument ("Calendar", _)) -> true
    | Error e -> failwith $"Expected InvalidArgument(Calendar) but got: %A{e}"
    | Ok _    -> failwith "Expected Error for month=13 but got Ok"

// ============================================================================
// T022 — FigletText
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T022a — FigletText: default font + short text returns Ok and renders non-empty`` () =
    let font = FigletFont.default_
    match FigletText.create (SafeText.ofLiteral "Hi") font with
    | Error e -> failwith $"FigletText.create Error: %A{e}"
    | Ok figlet ->
        let comp = FigletText.toComposition figlet
        match Renderer.toRawAnsi (ctx80colour ()) comp with
        | Ok output -> output.Length > 0
        | Error e   -> failwith $"Expected render Ok but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T022b — FigletText: text of length 101 returns Error InvalidArgument`` () =
    let longText = System.String('x', 101)
    match FigletText.create (SafeText.ofLiteral longText) FigletFont.default_ with
    | Error (RenderError.InvalidArgument ("FigletText", _)) -> true
    | Error e -> failwith $"Expected InvalidArgument(FigletText) but got: %A{e}"
    | Ok _    -> failwith "Expected Error for text length 101 but got Ok"

// ============================================================================
// T023 — Grid
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T023a — Grid: 2 columns with 2 rows of exactly 2 cells each returns Ok and renders`` () =
    let col = GridColumn.create (0, 0, 0, 0) Alignment.LeftAlign
    let cell = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "x"))
    match Grid.create [ col; col ] [ [ cell; cell ]; [ cell; cell ] ] with
    | Error e -> failwith $"Grid.create Error: %A{e}"
    | Ok grid ->
        let comp = Grid.toComposition grid
        match Renderer.toRawAnsi (ctx80colour ()) comp with
        | Ok output -> output.Length > 0
        | Error e   -> failwith $"Expected render Ok but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T023b — Grid: 2 columns with a row of 1 cell returns Error InvalidArgument ragged`` () =
    let col  = GridColumn.create (0, 0, 0, 0) Alignment.LeftAlign
    let cell = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "x"))
    match Grid.create [ col; col ] [ [ cell ] ] with
    | Error (RenderError.InvalidArgument ("Grid", msg)) when msg.Contains "ragged" -> true
    | Error e -> failwith $"Expected InvalidArgument(Grid, ragged...) but got: %A{e}"
    | Ok _    -> failwith "Expected Error for ragged row but got Ok"

[<Property(MaxTest = 1)>]
let ``T023c — Grid: empty columns list returns Error EmptyComposition`` () =
    let cell = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "x"))
    match Grid.create [] [ [ cell ] ] with
    | Error (RenderError.EmptyComposition _) -> true
    | Error e -> failwith $"Expected EmptyComposition but got: %A{e}"
    | Ok _    -> failwith "Expected Error for empty columns but got Ok"

// ============================================================================
// T024 — TextPath
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T024a — TextPath: any non-null SafeText path returns Ok and renders`` () =
    let path = SafeText.ofLiteral "/usr/local/bin/foo"
    match TextPath.create path Alignment.LeftAlign with
    | Error e -> failwith $"TextPath.create Error: %A{e}"
    | Ok tp ->
        let comp = TextPath.toComposition tp
        match Renderer.toRawAnsi (ctx80colour ()) comp with
        | Ok output -> output.Length > 0
        | Error e   -> failwith $"Expected render Ok but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T024b — TextPath: SafeText.ofLiteral null returns Error InvalidArgument null content`` () =
    // SafeText.ofLiteral null normalises to empty string internally —
    // the TextPath smart constructor must detect this and return Error.
    // data-model.md §3.2.8: null path → Error InvalidArgument ("TextPath", "null content not permitted")
    let nullPath = SafeText.ofLiteral null
    match TextPath.create nullPath Alignment.LeftAlign with
    | Error (RenderError.InvalidArgument ("TextPath", "null content not permitted")) -> true
    | Error e -> failwith $"Expected InvalidArgument(TextPath, null content...) but got: %A{e}"
    | Ok _    -> failwith "Expected Error for null SafeText but got Ok"

// ============================================================================
// T024a (colour toggle) — all eight widgets at colourEnabled=true vs false
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T024-colour — all data-display widgets: colour-off output equals colour-on with escapes stripped`` () =
    // Build one instance of each widget.
    let leaf     = TreeNode.leaf (SafeText.ofLiteral "root")
    let tree     = Tree.create leaf |> function Ok t -> t | Error e -> failwith $"%A{e}"
    let json     = Json.create """{"key":"val"}""" |> function Ok j -> j | Error e -> failwith $"%A{e}"
    let bci      = BarChartItem.create (SafeText.ofLiteral "bar") 10.0 (Colour.Rgb(255uy,0uy,0uy)) |> function Ok x -> x | Error e -> failwith $"%A{e}"
    let bar      = BarChart.create None [ bci ] 80 |> function Ok x -> x | Error e -> failwith $"%A{e}"
    let brki     = BreakdownChartItem.create (SafeText.ofLiteral "brk") 100.0 (Colour.Rgb(0uy,255uy,0uy)) |> function Ok x -> x | Error e -> failwith $"%A{e}"
    let breakdown= BreakdownChart.create [ brki ] 80 |> function Ok x -> x | Error e -> failwith $"%A{e}"
    let cal      = Calendar.create 2026 6 [] |> function Ok x -> x | Error e -> failwith $"%A{e}"
    let figlet   = FigletText.create (SafeText.ofLiteral "Hi") FigletFont.default_ |> function Ok x -> x | Error e -> failwith $"%A{e}"
    let col      = GridColumn.create (0,0,0,0) Alignment.LeftAlign
    let cell     = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "x"))
    let grid     = Grid.create [ col; col ] [ [ cell; cell ] ] |> function Ok x -> x | Error e -> failwith $"%A{e}"
    let tp       = TextPath.create (SafeText.ofLiteral "/a/b/c") Alignment.LeftAlign |> function Ok x -> x | Error e -> failwith $"%A{e}"

    let compositions =
        [ Tree.toComposition      tree
          Json.toComposition      json
          BarChart.toComposition  bar
          BreakdownChart.toComposition breakdown
          Calendar.toComposition  cal
          FigletText.toComposition figlet
          Grid.toComposition      grid
          TextPath.toComposition  tp ]

    compositions
    |> List.forall (fun comp ->
        let colourOut   = renderOk (ctx80colour  ()) comp
        let nocolourOut = renderOk (ctx80nocolour()) comp
        // Colour-off output must equal colour-on with ANSI escapes stripped.
        stripAnsi colourOut = nocolourOut)

// ============================================================================
// T025 — Determinism: all eight widgets render identically across two passes
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T025 — all data-display primitives render deterministically at fixed width`` () =
    let leaf     = TreeNode.leaf (SafeText.ofLiteral "determinism-root")
    let tree     = Tree.create leaf |> function Ok t -> t | Error e -> failwith $"%A{e}"
    let json     = Json.create """{"det":true}""" |> function Ok j -> j | Error e -> failwith $"%A{e}"
    let bci      = BarChartItem.create (SafeText.ofLiteral "det-bar") 77.0 (Colour.Rgb(0uy,0uy,255uy)) |> function Ok x -> x | Error e -> failwith $"%A{e}"
    let bar      = BarChart.create (Some (SafeText.ofLiteral "DET")) [ bci ] 80 |> function Ok x -> x | Error e -> failwith $"%A{e}"
    let brki     = BreakdownChartItem.create (SafeText.ofLiteral "det-brk") 100.0 (Colour.Rgb(128uy,128uy,0uy)) |> function Ok x -> x | Error e -> failwith $"%A{e}"
    let breakdown= BreakdownChart.create [ brki ] 80 |> function Ok x -> x | Error e -> failwith $"%A{e}"
    let cal      = Calendar.create 2026 1 [] |> function Ok x -> x | Error e -> failwith $"%A{e}"
    let figlet   = FigletText.create (SafeText.ofLiteral "OK") FigletFont.default_ |> function Ok x -> x | Error e -> failwith $"%A{e}"
    let col      = GridColumn.create (1,1,0,0) Alignment.CentreAlign
    let cell     = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "det"))
    let grid     = Grid.create [ col ] [ [ cell ]; [ cell ] ] |> function Ok x -> x | Error e -> failwith $"%A{e}"
    let tp       = TextPath.create (SafeText.ofLiteral "/determinism/path") Alignment.RightAlign |> function Ok x -> x | Error e -> failwith $"%A{e}"

    let compositions =
        [ Tree.toComposition      tree
          Json.toComposition      json
          BarChart.toComposition  bar
          BreakdownChart.toComposition breakdown
          Calendar.toComposition  cal
          FigletText.toComposition figlet
          Grid.toComposition      grid
          TextPath.toComposition  tp ]

    compositions
    |> List.forall (fun comp ->
        // Two separate RenderContext instances at width 80, colour on.
        let ctx1 = RenderContext.create 80 true "default"
        let ctx2 = RenderContext.create 80 true "default"
        let out1 = renderOk ctx1 comp
        let out2 = renderOk ctx2 comp
        // byte-identical output
        out1 = out2)
