module Fugue.Cli.Render

open Spectre.Console
open Spectre.Console.Rendering
open Fugue.Core.Config
open Fugue.Core.Localization

type ToolState =
    | Running   of name: string * args: string
    | Completed of name: string * args: string * output: string
    | Failed    of name: string * args: string * err: string

let mutable private colorEnabled = true

let initColor (enabled: bool) =
    colorEnabled <- enabled

let isColorEnabled () = colorEnabled

/// Path-aware prompt string for ReadLine. Just visible chars; ANSI not added here
/// because Console.WriteLine inside ReadLine doesn't go through Spectre.
let prompt (_cwd: string) : string = "› "

let userMessage (ui: UiConfig) (text: string) : IRenderable =
    if colorEnabled then
        let escaped = Markup.Escape text
        match ui.UserAlignment with
        | Left ->
            Markup(sprintf "[grey]›[/] %s" escaped) :> _
        | Right ->
            let bubble = Padder(Markup(escaped)).PadLeft(2).PadRight(2) :> IRenderable
            Align(bubble, HorizontalAlignment.Right) :> _
    else
        Text(sprintf "> %s" text) :> _

/// Plain assistant text during streaming (no markdown parse — buffer is partial).
let assistantLive (text: string) : IRenderable =
    Text(text) :> _

/// Final assistant text rendered as markdown.
let assistantFinal (text: string) : IRenderable =
    if colorEnabled then MarkdownRender.toRenderable text
    else Text(text) :> _

let cancelled (s: Strings) : IRenderable =
    if colorEnabled then
        Markup(sprintf "[yellow]⚠ %s[/]" (Markup.Escape s.Cancelled)) :> _
    else
        Text(s.Cancelled) :> _

let errorLine (s: Strings) (msg: string) : IRenderable =
    if colorEnabled then
        Markup(sprintf "[red]⚠ %s:[/] %s" (Markup.Escape s.ErrorPrefix) (Markup.Escape msg)) :> _
    else
        Text(sprintf "%s: %s" s.ErrorPrefix msg) :> _

let private renderToolBody (output: string) : IRenderable =
    if colorEnabled then
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
            let rows =
                trimmed
                |> Array.map (fun l ->
                    Markup(sprintf "[dim]%s[/]" (Markup.Escape l)) :> IRenderable)
            Padder(Rows(rows)).PadLeft(2) :> _
    else
        let lines = output.Split('\n')
        let trimmed =
            if lines.Length > 12 then
                Array.append (Array.truncate 12 lines)
                    [| "+ " + string (lines.Length - 12) + " more lines" |]
            else lines
        Padder(Rows(trimmed |> Array.map (fun l -> Text(l) :> IRenderable))).PadLeft(2) :> _

let toolBullet (s: Strings) (state: ToolState) : IRenderable =
    if colorEnabled then
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
    else
        match state with
        | Running(name, args) ->
            Rows([ Text(sprintf "* %s(%s)" name args) :> IRenderable
                   Padder(Text(s.ToolRunning)).PadLeft(2) :> _ ]) :> _
        | Completed(name, args, output) ->
            Rows([ Text(sprintf "* %s(%s)" name args) :> IRenderable
                   renderToolBody output ]) :> _
        | Failed(name, args, err) ->
            Rows([ Text(sprintf "* %s(%s)" name args) :> IRenderable
                   Padder(Text(sprintf "Error: %s" err)).PadLeft(2) :> _ ]) :> _
