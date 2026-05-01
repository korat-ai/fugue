module Fugue.Tests.PickerRenderTests

open Xunit
open FsUnit.Xunit
open Fugue.Surface
open Fugue.Cli.Picker

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// Build a terminal tall enough and run renderState ops on it.
/// Returns the mock terminal with all ops applied (CursorRow starts at 0).
let private run (state: PickerState) : MockTerminal =
    let lineCount = 1 + 1 + state.VisibleRows + 1 + 1
    let term = MockExecutor.create 120 (lineCount + 5) // extra rows as margin
    term.CursorRow <- 0
    MockExecutor.execute (renderState state) term |> ignore
    term

/// Row offsets within the rendered output (relative to CursorRow=0):
///   0 : title
///   1 : above-hint or blank
///   2..2+VisibleRows-1 : item rows
///   2+VisibleRows : below-hint or blank
///   3+VisibleRows : footer
let private titleRow   = 0
let private hintAbove  = 1
let private itemRow (offset: int) = 2 + offset
let private hintBelow (visibleRows: int) = 2 + visibleRows
let private footerRow (visibleRows: int) = 3 + visibleRows

// ---------------------------------------------------------------------------
// 1. Empty items → only title + footer (no item rows crash)
// ---------------------------------------------------------------------------
[<Fact>]
let ``empty items: renderState produces ops without crashing`` () =
    let state =
        { Title = "Choose"; Items = [||]; CurrentIdx = 0; ScrollTop = 0; VisibleRows = 3 }
    let ops = renderState state
    // 1 (title) + 1 (above-hint blank) + 3 (item rows, all empty) + 1 (below-hint blank) + 1 (footer) = 7
    ops |> List.length |> should equal 7

[<Fact>]
let ``empty items: title row contains title text`` () =
    let state =
        { Title = "My Title"; Items = [||]; CurrentIdx = 0; ScrollTop = 0; VisibleRows = 3 }
    let term = run state
    MockExecutor.lineAt titleRow term |> should haveSubstring "My Title"

// ---------------------------------------------------------------------------
// 2. Single item, currentIdx=0 → item is highlighted (contains › marker)
// ---------------------------------------------------------------------------
[<Fact>]
let ``single item selected: row contains selection marker`` () =
    let state =
        { Title = "Pick"; Items = [| "alpha", "hint-a" |]; CurrentIdx = 0; ScrollTop = 0; VisibleRows = 5 }
    let term = run state
    MockExecutor.lineAt (itemRow 0) term |> should haveSubstring "›"

[<Fact>]
let ``single item selected: item label appears in row`` () =
    let state =
        { Title = "Pick"; Items = [| "alpha", "hint-a" |]; CurrentIdx = 0; ScrollTop = 0; VisibleRows = 5 }
    let term = run state
    MockExecutor.lineAt (itemRow 0) term |> should haveSubstring "alpha"

// ---------------------------------------------------------------------------
// 3. Three items, currentIdx=1 → middle item highlighted, others not
// ---------------------------------------------------------------------------
[<Fact>]
let ``three items currentIdx=1: middle row contains marker, first row does not`` () =
    let items = [| "first","h1"; "second","h2"; "third","h3" |]
    let state = { Title = "T"; Items = items; CurrentIdx = 1; ScrollTop = 0; VisibleRows = 5 }
    let term = run state
    let row0 = MockExecutor.lineAt (itemRow 0) term
    let row1 = MockExecutor.lineAt (itemRow 1) term
    let row2 = MockExecutor.lineAt (itemRow 2) term
    row1 |> should haveSubstring "›"
    row0 |> should not' (haveSubstring "›")
    row2 |> should not' (haveSubstring "›")

// ---------------------------------------------------------------------------
// 4. ScrollTop > 0 → "above" hint visible in above-hint row
// ---------------------------------------------------------------------------
[<Fact>]
let ``scrollTop=2: above-hint row contains 'more above'`` () =
    let items = Array.init 10 (fun i -> $"item{i}", $"h{i}")
    let state = { Title = "T"; Items = items; CurrentIdx = 3; ScrollTop = 2; VisibleRows = 5 }
    let term = run state
    MockExecutor.lineAt hintAbove term |> should haveSubstring "more above"

[<Fact>]
let ``scrollTop=0: above-hint row is blank`` () =
    let items = Array.init 5 (fun i -> $"item{i}", $"h{i}")
    let state = { Title = "T"; Items = items; CurrentIdx = 0; ScrollTop = 0; VisibleRows = 5 }
    let term = run state
    MockExecutor.lineAt hintAbove term |> should equal ""

// ---------------------------------------------------------------------------
// 5. Items extend below window → "below" hint visible
// ---------------------------------------------------------------------------
[<Fact>]
let ``items below window: below-hint row contains 'more below'`` () =
    let items = Array.init 10 (fun i -> $"item{i}", $"h{i}")
    let state = { Title = "T"; Items = items; CurrentIdx = 0; ScrollTop = 0; VisibleRows = 5 }
    let term = run state
    MockExecutor.lineAt (hintBelow 5) term |> should haveSubstring "more below"

[<Fact>]
let ``items fit window: below-hint row is blank`` () =
    let items = Array.init 3 (fun i -> $"item{i}", $"h{i}")
    let state = { Title = "T"; Items = items; CurrentIdx = 0; ScrollTop = 0; VisibleRows = 5 }
    let term = run state
    MockExecutor.lineAt (hintBelow 5) term |> should equal ""

// ---------------------------------------------------------------------------
// 6. CurrentIdx outside scroll window: render is still valid (no crash)
// ---------------------------------------------------------------------------
[<Fact>]
let ``currentIdx outside scroll window: does not throw`` () =
    // currentIdx = 9 but scrollTop = 0, visibleRows = 3 → idx 9 is outside [0,2]
    let items = Array.init 15 (fun i -> $"item{i}", $"h{i}")
    let state = { Title = "T"; Items = items; CurrentIdx = 9; ScrollTop = 0; VisibleRows = 3 }
    // If this throws, the test fails naturally — that is the assertion.
    let ops = renderState state
    ops |> List.length |> should equal (state.VisibleRows + 4)

// ---------------------------------------------------------------------------
// 7. Hint appears in every visible item row (dim markup)
// ---------------------------------------------------------------------------
[<Fact>]
let ``item rows include dim markup for hints`` () =
    let items = [| "label", "my-hint" |]
    let state = { Title = "T"; Items = items; CurrentIdx = 0; ScrollTop = 0; VisibleRows = 3 }
    let ops = renderState state
    // Find all Append ops with "my-hint" — should appear in item row
    let found =
        ops |> List.exists (fun op ->
            match op with
            | DrawOp.Append t -> t.Contains "my-hint"
            | _ -> false)
    found |> should equal true

// ---------------------------------------------------------------------------
// 8. Rendering is deterministic — same state → equal DrawOp lists
// ---------------------------------------------------------------------------
[<Fact>]
let ``renderState is deterministic`` () =
    let items = Array.init 5 (fun i -> $"item{i}", $"h{i}")
    let state = { Title = "Determinism"; Items = items; CurrentIdx = 2; ScrollTop = 0; VisibleRows = 5 }
    renderState state |> should equal (renderState state)

// ---------------------------------------------------------------------------
// 9. Title appears as the first Append op in the DrawOp list
// ---------------------------------------------------------------------------
[<Fact>]
let ``title is the first Append op`` () =
    let state =
        { Title = "First Op Title"; Items = [| "x","y" |]; CurrentIdx = 0; ScrollTop = 0; VisibleRows = 3 }
    let ops = renderState state
    match ops with
    | DrawOp.Append text :: _ -> text |> should haveSubstring "First Op Title"
    | _ -> failwith "Expected first op to be DrawOp.Append"

// ---------------------------------------------------------------------------
// 10. Footer row contains navigation hints
// ---------------------------------------------------------------------------
[<Fact>]
let ``footer row contains navigation hint text`` () =
    let state =
        { Title = "T"; Items = [| "a","b" |]; CurrentIdx = 0; ScrollTop = 0; VisibleRows = 3 }
    let term = run state
    let footer = MockExecutor.lineAt (footerRow 3) term
    footer |> should haveSubstring "↑/↓"
    footer |> should haveSubstring "Enter"
    footer |> should haveSubstring "Esc"
