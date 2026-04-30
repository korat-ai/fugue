module Fugue.Cli.Render

open System
open Spectre.Console
open Spectre.Console.Rendering
open Fugue.Core.Config
open Fugue.Core.Localization

type ToolState =
    | Running   of name: string * args: string
    | Completed of name: string * args: string * output: string * elapsed: TimeSpan
    | Failed    of name: string * args: string * err: string   * elapsed: TimeSpan

let mutable private colorEnabled = true
let mutable private bubblesMode  = false
let mutable private activeTheme  = ""

let initColor (enabled: bool) =
    colorEnabled <- enabled

let isColorEnabled () = colorEnabled

let initBubbles (enabled: bool) = bubblesMode <- enabled
let toggleBubbles () = bubblesMode <- not bubblesMode
let isBubblesMode () = bubblesMode

let mutable private typewriterMode = false

let initTheme (theme: string) = activeTheme <- theme
let initTypewriter (enabled: bool) = typewriterMode <- enabled
let isTypewriterMode () = typewriterMode
let toggleTypewriter () = typewriterMode <- not typewriterMode

let private themeColor (normal: string) (nocturne: string) =
    if activeTheme = "nocturne" then nocturne else normal

/// Render an IRenderable to ANSI string and split into lines for typewriter output.
let captureLines (r: IRenderable) : string[] =
    let sw = new System.IO.StringWriter()
    let settings = AnsiConsoleSettings()
    settings.Out <- AnsiConsoleOutput(sw)
    let console = AnsiConsole.Create settings
    console.Profile.Width <- max 20 Console.WindowWidth
    console.Write r
    sw.ToString().TrimEnd([| '\n' |]).Split('\n')

/// Build the input prompt from the UiConfig template.
/// Interpolate each character of `text` between two RGB colours and return Spectre markup.
let private gradientMarkup (r0: int, g0: int, b0: int) (r1: int, g1: int, b1: int) (text: string) : string =
    let n = max 1 (text.Length - 1)
    text
    |> Seq.mapi (fun i c ->
        let t = float i / float n
        let r = r0 + int (t * float (r1 - r0))
        let g = g0 + int (t * float (g1 - g0))
        let b = b0 + int (t * float (b1 - b0))
        sprintf "[#%02x%02x%02x]%s[/]" r g b (Markup.Escape (string c)))
    |> String.concat ""

/// One-line startup banner: "♩ Fugue" with blue-to-purple gradient. No-op when colour is off.
let showBanner () : unit =
    if not colorEnabled then () else
    let title = gradientMarkup (59, 130, 246) (168, 85, 247) "Fugue"
    AnsiConsole.MarkupLine(sprintf "[dim]♩[/] %s" title)
    AnsiConsole.WriteLine()

/// Supports {model} interpolation. Falls back to "› " if template is empty.
let prompt (ui: Fugue.Core.Config.UiConfig) (modelShort: string) : string =
    let tmpl = if System.String.IsNullOrWhiteSpace ui.PromptTemplate then "♩ " else ui.PromptTemplate
    tmpl.Replace("{model}", modelShort)

let userMessage (ui: UiConfig) (text: string) (turn: int) : IRenderable =
    if colorEnabled then
        let escaped = Markup.Escape text
        let prefix = sprintf "[dim][[%d]][/] " turn
        match ui.UserAlignment with
        | Left ->
            Markup(sprintf "[grey]›[/] %s%s" prefix escaped) :> _
        | Right ->
            let bubble = Padder(Markup(sprintf "%s%s" prefix escaped)).PadLeft(2).PadRight(2) :> IRenderable
            Align(bubble, HorizontalAlignment.Right) :> _
    else
        let prefix = sprintf "[%d] " turn
        Text(sprintf "> %s%s" prefix text) :> _

/// Plain assistant text during streaming (no markdown parse — buffer is partial).
let assistantLive (text: string) : IRenderable =
    Text(text) :> _

/// Final assistant text rendered as markdown.
let assistantFinal (text: string) : IRenderable =
    if colorEnabled then
        let inner = MarkdownRender.toRenderable text
        if bubblesMode then
            let panel = Panel(inner)
            panel.Border <- BoxBorder.Rounded
            panel.BorderStyle <- Style.Parse (themeColor "dim" "dim #1e3a5a")
            panel.Header <- PanelHeader(" Claude ")
            panel :> IRenderable
        else inner
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

let private termWidth () =
    let w = Console.WindowWidth
    if w >= 20 then w else 120

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

let private formatElapsed (e: TimeSpan) =
    if e.TotalMilliseconds >= 1000.0 then
        sprintf "%.1fs" e.TotalSeconds
    else
        sprintf "%dms" (int e.TotalMilliseconds)

let private renderToolBody (output: string) : IRenderable =
    let w = max 20 (termWidth () - 4)  // 2 col PadLeft + 2 col margin
    let maxLines = if Console.WindowHeight < 24 then 10 else 20
    if colorEnabled then
        if DiffRender.looksLikeDiff output then DiffRender.toRenderable output
        elif output.Contains "```" || output.Contains "**" || output.StartsWith "#" then
            MarkdownRender.toRenderable output
        else
            let wrapped = output.Split('\n') |> Array.collect (softWrap w >> List.toArray)
            let trimmed =
                if wrapped.Length > maxLines then
                    Array.append (Array.truncate maxLines wrapped)
                        [| sprintf "+ %d more lines" (wrapped.Length - maxLines) |]
                else wrapped
            let rows = trimmed |> Array.map (fun l -> Markup(sprintf "[dim]%s[/]" (Markup.Escape l)) :> IRenderable)
            Padder(Rows(rows)).PadLeft(2) :> _
    else
        let wrapped = output.Split('\n') |> Array.collect (softWrap w >> List.toArray)
        let trimmed =
            if wrapped.Length > maxLines then
                Array.append (Array.truncate maxLines wrapped)
                    [| sprintf "+ %d more lines" (wrapped.Length - maxLines) |]
            else wrapped
        Padder(Rows(trimmed |> Array.map (fun l -> Text(l) :> IRenderable))).PadLeft(2) :> _

/// Extract the first readable line from a raw error string, suppressing .NET stack frames.
let private summarizeToolError (raw: string) : string =
    let lines = raw.Split('\n')
    let headline =
        lines
        |> Array.tryFind (fun l ->
            let t = l.TrimStart()
            t.Length > 0
            && not (t.StartsWith("at "))
            && not (t.StartsWith("--- End of"))
            && not (t.StartsWith("System."))
            && not (t.StartsWith("Microsoft.")))
        |> Option.defaultWith (fun () ->
            lines |> Array.tryFind (fun l -> l.TrimStart().Length > 0) |> Option.defaultValue raw)
    if headline.Length > 120 then headline.[..116] + " …" else headline

let toolBullet (s: Strings) (state: ToolState) : IRenderable =
    if colorEnabled then
        let cYellow = themeColor "yellow"  "#c0a060"
        let cGreen  = themeColor "green"   "#5a9060"
        let cRed    = themeColor "red"     "#a04040"
        match state with
        | Running(name, args) ->
            let header =
                sprintf "[%s]●[/] [bold]%s[/]([dim]%s[/])"
                    cYellow (Markup.Escape name) (Markup.Escape args)
            let body =
                Padder(Markup(sprintf "[dim]%s[/]" (Markup.Escape s.ToolRunning))).PadLeft(2)
            Rows([ Markup(header) :> IRenderable; body :> _ ]) :> _
        | Completed(name, args, output, elapsed) ->
            let header =
                sprintf "[%s]●[/] [bold]%s[/]([dim]%s[/]) [dim]%s[/]"
                    cGreen (Markup.Escape name) (Markup.Escape args) (formatElapsed elapsed)
            Rows([ Markup(header) :> IRenderable; renderToolBody output ]) :> _
        | Failed(name, args, err, elapsed) ->
            let header =
                sprintf "[%s]●[/] [bold]%s[/]([dim]%s[/]) [dim]%s[/]"
                    cRed (Markup.Escape name) (Markup.Escape args) (formatElapsed elapsed)
            let body =
                Padder(Markup(sprintf "[%s]%s[/]" cRed (Markup.Escape (summarizeToolError err)))).PadLeft(2)
            Rows([ Markup(header) :> IRenderable; body :> _ ]) :> _
    else
        match state with
        | Running(name, args) ->
            Rows([ Text(sprintf "* %s(%s)" name args) :> IRenderable
                   Padder(Text(s.ToolRunning)).PadLeft(2) :> _ ]) :> _
        | Completed(name, args, output, elapsed) ->
            Rows([ Text(sprintf "* %s(%s) %s" name args (formatElapsed elapsed)) :> IRenderable
                   renderToolBody output ]) :> _
        | Failed(name, args, err, elapsed) ->
            Rows([ Text(sprintf "* %s(%s) %s" name args (formatElapsed elapsed)) :> IRenderable
                   Padder(Text(sprintf "Error: %s" (summarizeToolError err))).PadLeft(2) :> _ ]) :> _
