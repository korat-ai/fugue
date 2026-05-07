module Fugue.Cli.Picker

open System
open Spectre.Console
open Fugue.Surface

// ---------------------------------------------------------------------------
// Pure render types
// ---------------------------------------------------------------------------

/// Immutable state snapshot for the picker. All fields needed to render
/// a complete frame — no Console reads inside renderState.
type PickerState = {
    Title:       string
    Query:       string
    Items:       (string * string) array  // (label, hint) already-filtered view
    CurrentIdx:  int
    ScrollTop:   int
    VisibleRows: int
}

/// Highlight the first occurrence of query inside label using yellow markup.
/// Falls back to escaped plain text when query is empty or not found.
let private highlightMatch (label: string) (query: string) : string =
    if query = "" then Markup.Escape label
    else
        let idx = label.IndexOf(query, StringComparison.OrdinalIgnoreCase)
        if idx < 0 then Markup.Escape label
        else
            let before  = Markup.Escape (label[.. idx - 1])
            let matched = Markup.Escape (label[idx .. idx + query.Length - 1])
            let after   = Markup.Escape (label[idx + query.Length ..])
            $"{before}[bold yellow]{matched}[/]{after}"

/// Pure render: PickerState -> DrawOp list.
/// No Console writes, no side effects. Same state → same ops.
/// Row layout (fixed height = VisibleRows + 5):
///   row 0              : title
///   row 1              : search / filter box
///   row 2              : above-scroll hint or blank
///   row 3..3+VR-1      : item rows (VisibleRows slots)
///   row 3+VR           : below-scroll hint or blank
///   row 4+VR           : footer
let renderState (state: PickerState) : DrawOp list =
    let n = state.Items.Length
    surface {
        // Title row
        yield DrawOp.Append $"  {state.Title}"

        // Search / filter row
        let qDisplay =
            if state.Query = "" then "[dim](type to filter)[/]"
            else Markup.Escape state.Query
        yield DrawOp.Append $"  / {qDisplay}"

        // Above-scroll hint (or blank spacer)
        if state.ScrollTop > 0 then
            yield DrawOp.Append $"  ▲ {state.ScrollTop} more above"
        else
            yield DrawOp.Append ""

        // Item rows — always emit exactly VisibleRows lines to keep totalLines constant
        if n = 0 then
            yield DrawOp.Append "  [dim]no matches[/]"
            for _ in 2 .. state.VisibleRows do
                yield DrawOp.Append ""
        else
            for offset in 0 .. state.VisibleRows - 1 do
                let i = state.ScrollTop + offset
                if i < n then
                    let (label, hint) = state.Items.[i]
                    let selected  = i = state.CurrentIdx
                    let marker    = if selected then "› " else "  "
                    // Selected rows: plain bold cyan (avoid Spectre nested-markup issues).
                    // Unselected rows: highlight the typed query in yellow.
                    let labelPart =
                        if selected then $"[bold cyan]{Markup.Escape label}[/]"
                        else highlightMatch label state.Query
                    yield DrawOp.Append $"{marker}{labelPart}  [dim]{Markup.Escape hint}[/]"
                else
                    yield DrawOp.Append ""

        // Below-scroll hint (or blank spacer)
        let remaining = n - (state.ScrollTop + state.VisibleRows)
        if remaining > 0 then
            yield DrawOp.Append $"  ▼ {remaining} more below"
        else
            yield DrawOp.Append ""

        // Footer help row
        yield DrawOp.Append "  ↑/↓ navigate · type to filter · Backspace · Enter select · Esc cancel"
    }

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

/// Generic scrollable picker with type-to-filter and arrow-key navigation.
/// items: (label, hint) pairs. label is the bold left column, hint is the dimmed right column.
/// Returns the original (pre-filter) index of the chosen item, or None on cancel.
let pick (title: string) (items: (string * string) list) (initialIdx: int) : int option =
    if items.IsEmpty then None
    else
        let arr = items |> List.toArray
        let n   = arr.Length
        // One extra row for the search box vs. the old layout (+5 instead of +4).
        let visibleRows = min n (max 5 (Console.WindowHeight - 7))

        let mutable query      = ""
        let mutable currentIdx = max 0 (min (n - 1) initialIdx)
        let mutable scrollTop  =
            if n <= visibleRows then 0
            elif currentIdx < visibleRows / 2 then 0
            else min (n - visibleRows) (currentIdx - visibleRows / 2)

        // filteredItems: (label, hint, originalIdx)
        let mutable filteredItems : (string * string * int) array =
            arr |> Array.mapi (fun i (l, h) -> l, h, i)

        let refilter () =
            filteredItems <-
                if query = "" then
                    arr |> Array.mapi (fun i (l, h) -> l, h, i)
                else
                    arr
                    |> Array.mapi (fun i (l, h) -> l, h, i)
                    |> Array.filter (fun (l, _, _) ->
                        l.Contains(query, StringComparison.OrdinalIgnoreCase))
            currentIdx <- 0
            scrollTop  <- 0

        let scrollIntoView () =
            let fn = filteredItems.Length
            if fn = 0 then
                scrollTop  <- 0
                currentIdx <- 0
            else
                currentIdx <- max 0 (min (fn - 1) currentIdx)
                if currentIdx < scrollTop then
                    scrollTop <- currentIdx
                elif currentIdx >= scrollTop + visibleRows then
                    scrollTop <- currentIdx - visibleRows + 1

        let mkState () : PickerState =
            { Title       = title
              Query       = query
              Items       = filteredItems |> Array.map (fun (l, h, _) -> l, h)
              CurrentIdx  = currentIdx
              ScrollTop   = scrollTop
              VisibleRows = visibleRows }

        let applyOps (ops: DrawOp list) =
            for op in ops do
                match op with
                | DrawOp.Append text -> Surface.markupLine text
                | _ -> ()

        let totalLines = visibleRows + 5

        let renderAll () =
            applyOps (renderState (mkState ()))

        let redraw () =
            Surface.write $"\x1b[{totalLines}F"
            for _ in 1 .. totalLines do
                Surface.write "\x1b[K"
                Surface.write "\x1b[1B"
            Surface.write $"\x1b[{totalLines}F"
            renderAll ()

        let clearMenu () =
            Surface.write $"\x1b[{totalLines}F"
            for _ in 1 .. totalLines do
                Surface.write "\x1b[K"
                Surface.write "\x1b[1B"
            Surface.write $"\x1b[{totalLines}F"

        Surface.lineBreak ()
        renderAll ()

        let mutable picked    : int option = None
        let mutable cancelled = false
        while picked.IsNone && not cancelled do
            let key = Console.ReadKey(intercept = true)
            match key.Key with
            | ConsoleKey.UpArrow ->
                let fn = filteredItems.Length
                if fn > 0 then
                    currentIdx <- (currentIdx - 1 + fn) % fn
                    scrollIntoView ()
                    redraw ()
            | ConsoleKey.DownArrow ->
                let fn = filteredItems.Length
                if fn > 0 then
                    currentIdx <- (currentIdx + 1) % fn
                    scrollIntoView ()
                    redraw ()
            | ConsoleKey.PageUp ->
                currentIdx <- max 0 (currentIdx - visibleRows)
                scrollIntoView ()
                redraw ()
            | ConsoleKey.PageDown ->
                let fn = filteredItems.Length
                if fn > 0 then
                    currentIdx <- min (fn - 1) (currentIdx + visibleRows)
                    scrollIntoView ()
                    redraw ()
            | ConsoleKey.Home ->
                currentIdx <- 0
                scrollIntoView ()
                redraw ()
            | ConsoleKey.End ->
                let fn = filteredItems.Length
                if fn > 0 then
                    currentIdx <- fn - 1
                    scrollIntoView ()
                    redraw ()
            | ConsoleKey.Enter ->
                if filteredItems.Length > 0 then
                    let (_, _, origIdx) = filteredItems.[currentIdx]
                    picked <- Some origIdx
            | ConsoleKey.Escape -> cancelled <- true
            | ConsoleKey.Backspace ->
                if query.Length > 0 then
                    query <- query[.. query.Length - 2]
                    refilter ()
                    redraw ()
            | _ ->
                if key.Modifiers.HasFlag ConsoleModifiers.Control && key.Key = ConsoleKey.C then
                    cancelled <- true
                elif not (Char.IsControl key.KeyChar) then
                    query <- query + string key.KeyChar
                    refilter ()
                    redraw ()

        clearMenu ()
        picked

/// Strip placeholder args (e.g. "/ask <q>" → "/ask "). For commands without args
/// returns the command unchanged. Used to pre-fill the input buffer with the
/// shortest valid template.
let templateOf (commandName: string) : string =
    let p = commandName.IndexOf '<'
    if p < 0 then commandName
    else commandName.Substring(0, p).TrimEnd() + " "
