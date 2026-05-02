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
    Items:       (string * string) array  // (label, hint) pairs
    CurrentIdx:  int
    ScrollTop:   int
    VisibleRows: int
}

/// Pure render: PickerState -> DrawOp list.
/// No Console writes, no side effects. Same state → same ops.
/// Each op is DrawOp.Append (one line), in top-to-bottom order:
///   row 0 : title
///   row 1 : above-hint or blank
///   row 2..2+VisibleRows-1 : item rows (up to VisibleRows items)
///   row 2+VisibleRows : below-hint or blank
///   row 3+VisibleRows : footer
let renderState (state: PickerState) : DrawOp list =
    let n = state.Items.Length
    surface {
        // Title row
        yield DrawOp.Append $"  {state.Title}"

        // Above-scroll hint (or blank spacer)
        if state.ScrollTop > 0 then
            yield DrawOp.Append $"  ▲ {state.ScrollTop} more above"
        else
            yield DrawOp.Append ""

        // Item rows
        for offset in 0 .. state.VisibleRows - 1 do
            let i = state.ScrollTop + offset
            if i < n then
                let (label, hint) = state.Items.[i]
                let selected = i = state.CurrentIdx
                let marker = if selected then "› " else "  "
                let labelPart = if selected then $"[bold cyan]{Markup.Escape label}[/]" else Markup.Escape label
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
        yield DrawOp.Append "  ↑/↓ navigate · PgUp/PgDn page · Home/End ends · Enter select · Esc cancel"
    }

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

/// Generic scrollable arrow-key picker.
/// items: (label, hint) pairs. label is the bold left column, hint is the dimmed right column.
/// Returns the chosen index or None on cancel.
let pick (title: string) (items: (string * string) list) (initialIdx: int) : int option =
    if items.IsEmpty then None
    else
        let arr = items |> List.toArray
        let n = arr.Length
        let visibleRows = min n (max 5 (Console.WindowHeight - 6))
        let mutable currentIdx = max 0 (min (n - 1) initialIdx)
        let mutable scrollTop =
            if n <= visibleRows then 0
            elif currentIdx < visibleRows / 2 then 0
            else min (n - visibleRows) (currentIdx - visibleRows / 2)

        let scrollIntoView () =
            if currentIdx < scrollTop then
                scrollTop <- currentIdx
            elif currentIdx >= scrollTop + visibleRows then
                scrollTop <- currentIdx - visibleRows + 1

        let mkState () : PickerState =
            { Title       = title
              Items       = arr
              CurrentIdx  = currentIdx
              ScrollTop   = scrollTop
              VisibleRows = visibleRows }

        // Apply DrawOp list through the Surface actor (Phase 1.3c v2).
        let applyOps (ops: DrawOp list) =
            for op in ops do
                match op with
                | DrawOp.Append text ->
                    Surface.markupLine text
                | _ -> ()

        let totalLines = 1 + 1 + visibleRows + 1 + 1

        let renderAll () =
            applyOps (renderState (mkState ()))

        let redraw () =
            Surface.write($"\x1b[{totalLines}F")
            for _ in 1 .. totalLines do
                Surface.write "\x1b[K"
                Surface.write "\x1b[1B"
            Surface.write($"\x1b[{totalLines}F")
            renderAll ()

        let clearMenu () =
            Surface.write($"\x1b[{totalLines}F")
            for _ in 1 .. totalLines do
                Surface.write "\x1b[K"
                Surface.write "\x1b[1B"
            Surface.write($"\x1b[{totalLines}F")

        Surface.lineBreak ()
        renderAll ()

        let mutable picked: int option = None
        let mutable cancelled = false
        while picked.IsNone && not cancelled do
            let key = Console.ReadKey(intercept = true)
            match key.Key with
            | ConsoleKey.UpArrow ->
                currentIdx <- (currentIdx - 1 + n) % n
                scrollIntoView ()
                redraw ()
            | ConsoleKey.DownArrow ->
                currentIdx <- (currentIdx + 1) % n
                scrollIntoView ()
                redraw ()
            | ConsoleKey.PageUp ->
                currentIdx <- max 0 (currentIdx - visibleRows)
                scrollIntoView ()
                redraw ()
            | ConsoleKey.PageDown ->
                currentIdx <- min (n - 1) (currentIdx + visibleRows)
                scrollIntoView ()
                redraw ()
            | ConsoleKey.Home ->
                currentIdx <- 0
                scrollIntoView ()
                redraw ()
            | ConsoleKey.End ->
                currentIdx <- n - 1
                scrollIntoView ()
                redraw ()
            | ConsoleKey.Enter -> picked <- Some currentIdx
            | ConsoleKey.Escape -> cancelled <- true
            | _ ->
                if key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key = ConsoleKey.C then
                    cancelled <- true

        clearMenu ()
        picked

/// Strip placeholder args (e.g. "/ask <q>" → "/ask "). For commands without args
/// returns the command unchanged. Used to pre-fill the input buffer with the
/// shortest valid template.
let templateOf (commandName: string) : string =
    let p = commandName.IndexOf '<'
    if p < 0 then commandName
    else commandName.Substring(0, p).TrimEnd() + " "
