module Fugue.Cli.Repl

open System
open System.Collections.Generic
open System.Diagnostics.CodeAnalysis
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Agents.AI
open Spectre.Console
open Fugue.Core.Config
open Fugue.Core.Localization
open Fugue.Agent
open Fugue.Cli

/// Cheap heuristic — does the text contain markdown markers worth re-rendering?
/// Avoids re-printing pure plain text twice. False positives (e.g. raw `**` in code)
/// are acceptable; the markdown render still produces readable output.
let private hasMarkdown (s: string) : bool =
    s.Contains "```" || s.Contains "**"
    || s.Split('\n') |> Array.exists (fun l ->
        let t = l.TrimStart()
        t.StartsWith "# " || t.StartsWith "## " || t.StartsWith "### "
        || t.StartsWith "- " || t.StartsWith "* " || t.StartsWith "1. ")

/// Holder for the currently active stream's CancellationTokenSource.
/// Console.CancelKeyPress handler uses this to cancel mid-stream Ctrl+C.
type CancelSource() =
    let mutable streamCts : CancellationTokenSource option = None
    let mutable quit = false
    member _.QuitRequested = quit
    member _.RequestQuit() = quit <- true
    member _.Token = CancellationToken.None
    member _.NewStreamCts() =
        let cts = new CancellationTokenSource()
        streamCts <- Some cts
        cts
    member _.OnCtrlC (args: ConsoleCancelEventArgs | null) =
        match args with
        | null -> quit <- true
        | a ->
            match streamCts with
            | Some cts ->
                try cts.Cancel() with _ -> ()
                a.Cancel <- true
            | None ->
                quit <- true
                a.Cancel <- true
    interface IDisposable with
        member _.Dispose() =
            streamCts |> Option.iter (fun c -> try c.Dispose() with _ -> ())

let private streamAndRender
        (agent: AIAgent) (session: AgentSession | null) (input: string)
        (cfg: AppConfig) (cancelSrc: CancelSource) : Task<unit> = task {
    use streamCts = cancelSrc.NewStreamCts()
    let strings = pick cfg.Ui.Locale
    let toolMeta = Dictionary<string, string * string>()
    let textBuf = StringBuilder()
    // Progressive render state for the current text segment.
    // We use relative cursor moves (ESC[NA) instead of save/restore because save/restore
    // preserves absolute screen coords that become stale after scroll-region scrolling.
    let mutable segmentRows = 0   // rows the previous render of this segment occupied (cursor sits N rows below segment start)

    let renderSegment () =
        if textBuf.Length = 0 then () else
        let text = textBuf.ToString()
        let renderable : Spectre.Console.Rendering.IRenderable =
            if hasMarkdown text then Render.assistantFinal text
            else Spectre.Console.Text(text) :> _
        // Erase prior render: move cursor up to start of segment, walk down clearing each row.
        if segmentRows > 0 then
            Console.Out.Write ("\x1b[" + string segmentRows + "A")
            Console.Out.Write "\r"
            for i in 0 .. segmentRows - 1 do
                Console.Out.Write "\x1b[K"
                if i < segmentRows - 1 then Console.Out.Write "\x1b[B"
            // walk back up to start
            if segmentRows > 1 then
                Console.Out.Write ("\x1b[" + string (segmentRows - 1) + "A")
            Console.Out.Write "\r"
            Console.Out.Flush()
        // Measure rows by rendering into a StringWriter-backed console.
        let width = max 40 Console.WindowWidth
        let sw = new System.IO.StringWriter()
        let settings = new Spectre.Console.AnsiConsoleSettings(Out = new Spectre.Console.AnsiConsoleOutput(sw))
        let measureCon = Spectre.Console.AnsiConsole.Create(settings)
        measureCon.Profile.Width <- width
        measureCon.Write(renderable)
        let output : string = sw.ToString()
        let nlCount = output |> Seq.filter (fun c -> c = '\n') |> Seq.length
        let endsWithNl = output.Length > 0 && output.EndsWith "\n"
        let rows = if output.Length > 0 && not endsWithNl then nlCount + 1 else nlCount
        AnsiConsole.Write(renderable)
        if output.Length > 0 && not endsWithNl then AnsiConsole.WriteLine ()
        segmentRows <- max 1 rows

    let endSegment () =
        if textBuf.Length > 0 then renderSegment ()
        textBuf.Clear() |> ignore
        segmentRows <- 0

    try
        let stream = Conversation.run agent session input streamCts.Token
        let enumerator = stream.GetAsyncEnumerator(streamCts.Token)
        let mutable hasNext = true
        while hasNext do
            let! step = enumerator.MoveNextAsync().AsTask()
            if step then
                match enumerator.Current with
                | Conversation.TextChunk t ->
                    textBuf.Append t |> ignore
                    renderSegment ()
                | Conversation.ToolStarted(id, name, args) ->
                    endSegment ()
                    toolMeta.[id] <- (name, args)
                | Conversation.ToolCompleted(id, output, isErr) ->
                    let name, args =
                        match toolMeta.TryGetValue id with
                        | true, v -> v
                        | false, _ -> "?", "?"
                    let state =
                        if isErr then Render.Failed(name, args, output)
                        else Render.Completed(name, args, output)
                    AnsiConsole.Write(Render.toolBullet strings state)
                    AnsiConsole.WriteLine ()
                | Conversation.Finished -> ()
                | Conversation.Failed ex -> raise ex
            else hasNext <- false
        endSegment ()
    with
    | :? OperationCanceledException ->
        endSegment ()
        AnsiConsole.Write(Render.cancelled strings)
        AnsiConsole.WriteLine()
    | ex ->
        endSegment ()
        AnsiConsole.Write(Render.errorLine strings ex.Message)
        AnsiConsole.WriteLine()
}

[<RequiresUnreferencedCode("Calls Conversation.run which uses STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
[<RequiresDynamicCode("Calls Conversation.run which uses STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
let run (agent: AIAgent) (cfg: AppConfig) (cwd: string) : Task<unit> = task {
    use cancelSrc = new CancelSource()
    let handler = ConsoleCancelEventHandler(fun _ args -> cancelSrc.OnCtrlC args)
    Console.CancelKeyPress.AddHandler handler
    let! session = agent.CreateSessionAsync(CancellationToken.None)
    StatusBar.start cwd cfg

    let strings = pick cfg.Ui.Locale
    try
        while not cancelSrc.QuitRequested do
            let! lineOpt = ReadLine.readAsync (Render.prompt cwd) strings cancelSrc.Token
            match lineOpt with
            | None ->
                cancelSrc.RequestQuit()
            | Some s when System.String.IsNullOrWhiteSpace s -> ()
            | Some s when s = "/exit" || s = "/quit" ->
                cancelSrc.RequestQuit()
            | Some s when s = "/help" ->
                AnsiConsole.Write(Markup("[bold]" + Markup.Escape strings.HelpHeader + "[/]"))
                AnsiConsole.WriteLine()
                let helpItems = [ "/help",  strings.CmdHelpDesc
                                  "/clear", strings.CmdClearDesc
                                  "/exit",  strings.CmdExitDesc ]
                for (name, desc) in helpItems do
                    AnsiConsole.Write(Markup("  [cyan]" + Markup.Escape name + "[/]  [dim]" + Markup.Escape desc + "[/]"))
                    AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/clear" ->
                AnsiConsole.Clear()
                StatusBar.refresh ()
            | Some userInput ->
                AnsiConsole.Write(Render.userMessage cfg.Ui userInput)
                AnsiConsole.WriteLine()
                do! streamAndRender agent session userInput cfg cancelSrc
                StatusBar.refresh ()
    finally
        Console.CancelKeyPress.RemoveHandler handler
        StatusBar.stop ()
}
