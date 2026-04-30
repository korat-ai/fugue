module Fugue.Cli.Render

open System
open Spectre.Console
open Spectre.Console.Rendering
open Fugue.Core.Config
open Fugue.Core.Localization

type ToolState =
    | Running   of name: string * args: string
    | Completed of name: string * args: string * output: string
    | Failed    of name: string * args: string * err: string

/// Path-aware prompt string for ReadLine. Just visible chars; ANSI not added here
/// because Console.WriteLine inside ReadLine doesn't go through Spectre.
let prompt (_cwd: string) : string = "› "

let userMessage (ui: UiConfig) (text: string) : IRenderable =
    let escaped = Markup.Escape text
    match ui.UserAlignment with
    | Left ->
        Markup(sprintf "[grey]›[/] %s" escaped) :> _
    | Right ->
        let bubble = Padder(Markup(escaped)).PadLeft(2).PadRight(2) :> IRenderable
        Align(bubble, HorizontalAlignment.Right) :> _

/// Plain assistant text during streaming (no markdown parse — buffer is partial).
let assistantLive (text: string) : IRenderable =
    Text(text) :> _

/// Final assistant text rendered as markdown.
let assistantFinal (text: string) : IRenderable =
    MarkdownRender.toRenderable text

let cancelled (s: Strings) : IRenderable =
    Markup(sprintf "[yellow]⚠ %s[/]" (Markup.Escape s.Cancelled)) :> _

let errorLine (s: Strings) (msg: string) : IRenderable =
    Markup(sprintf "[red]⚠ %s:[/] %s" (Markup.Escape s.ErrorPrefix) (Markup.Escape msg)) :> _

let private termWidth () =
    let w = Console.WindowWidth
    if w > 0 then w else 120

/// Soft-wrap a single line to `width` columns, appending "↩" at each break.
let private softWrap (width: int) (line: string) : string list =
    if line.Length <= width then [ line ]
    else
        let usable = width - 1  // reserve 1 col for ↩
        let mutable result = []
        let mutable i = 0
        while i < line.Length do
            let chunkEnd = min line.Length (i + usable)
            let chunk = line.[i .. chunkEnd - 1]
            result <- result @ [ if chunkEnd < line.Length then chunk + "↩" else chunk ]
            i <- chunkEnd
        result

let private renderToolBody (output: string) : IRenderable =
    if DiffRender.looksLikeDiff output then DiffRender.toRenderable output
    elif output.Contains "```" || output.Contains "**" || output.StartsWith "#" then
        MarkdownRender.toRenderable output
    else
        // dim plain text, indented
        let lines = output.Split('\n')
        let trimmed =
            if lines.Length > 12 then
                Array.append (Array.truncate 12 lines)
                    [| "+ " + string (lines.Length - 12) + " more lines" |]
            else lines
        let w = termWidth ()
        let rows =
            trimmed
            |> Array.collect (fun l -> softWrap w l |> List.toArray)
            |> Array.map (fun l ->
                Markup(sprintf "[dim]%s[/]" (Markup.Escape l)) :> IRenderable)
        Padder(Rows(rows)).PadLeft(2) :> _

let toolBullet (s: Strings) (state: ToolState) : IRenderable =
    match state with
    | Running(name, args) ->
        let header =
            sprintf "[yellow]●[/] [bold]%s[/]([dim]%s[/])"
                (Markup.Escape name) (Markup.Escape args)
        let body =
            Padder(Markup(sprintf "[dim]%s[/]" (Markup.Escape s.ToolRunning))).PadLeft(2)
        Rows([ Markup(header) :> IRenderable; body :> _ ]) :> _
    | Completed(name, args, output) ->
        let header =
            sprintf "[green]●[/] [bold]%s[/]([dim]%s[/])"
                (Markup.Escape name) (Markup.Escape args)
        Rows([ Markup(header) :> IRenderable; renderToolBody output ]) :> _
    | Failed(name, args, err) ->
        let header =
            sprintf "[red]●[/] [bold]%s[/]([dim]%s[/])"
                (Markup.Escape name) (Markup.Escape args)
        let body =
            Padder(Markup(sprintf "[red]%s[/]" (Markup.Escape err))).PadLeft(2)
        Rows([ Markup(header) :> IRenderable; body :> _ ]) :> _
