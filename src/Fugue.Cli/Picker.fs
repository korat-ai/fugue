module Fugue.Cli.Picker

open System
open Spectre.Console

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

        let renderRow (i: int) (selected: bool) : string =
            let (label, hint) = arr.[i]
            let escLabel = Markup.Escape label
            let escHint = Markup.Escape hint
            let marker = if selected then "[bold cyan]›[/] " else "  "
            if selected then $"{marker}[bold cyan]{escLabel}[/]  [dim]{escHint}[/]"
            else             $"{marker}{escLabel}  [dim]{escHint}[/]"

        let renderAll () =
            AnsiConsole.MarkupLine $"[dim]{Markup.Escape title}[/]"
            if scrollTop > 0 then
                AnsiConsole.MarkupLine $"  [dim]▲ {scrollTop} more above[/]"
            else
                Console.Out.WriteLine ""
            for offset in 0 .. visibleRows - 1 do
                let i = scrollTop + offset
                if i < n then AnsiConsole.MarkupLine(renderRow i (i = currentIdx))
                else Console.Out.WriteLine ""
            let remaining = n - (scrollTop + visibleRows)
            if remaining > 0 then
                AnsiConsole.MarkupLine $"  [dim]▼ {remaining} more below[/]"
            else
                Console.Out.WriteLine ""
            AnsiConsole.MarkupLine "[dim]↑/↓ navigate · PgUp/PgDn page · Home/End ends · Enter select · Esc cancel[/]"

        let totalLines = 1 + 1 + visibleRows + 1 + 1

        let redraw () =
            Console.Out.Write($"\x1b[{totalLines}F")
            for _ in 1 .. totalLines do
                Console.Out.Write "\x1b[K"
                Console.Out.Write "\x1b[1B"
            Console.Out.Write($"\x1b[{totalLines}F")
            renderAll ()

        let clearMenu () =
            Console.Out.Write($"\x1b[{totalLines}F")
            for _ in 1 .. totalLines do
                Console.Out.Write "\x1b[K"
                Console.Out.Write "\x1b[1B"
            Console.Out.Write($"\x1b[{totalLines}F")

        AnsiConsole.WriteLine()
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
