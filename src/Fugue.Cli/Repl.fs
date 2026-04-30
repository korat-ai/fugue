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

let private tryReadClipboard () : string option =
    let tryCmd prog (args: string[]) =
        try
            let psi = System.Diagnostics.ProcessStartInfo()
            psi.FileName <- prog
            for a in args do psi.ArgumentList.Add a
            psi.RedirectStandardOutput <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            use proc = new System.Diagnostics.Process()
            proc.StartInfo <- psi
            proc.Start() |> ignore
            let out = proc.StandardOutput.ReadToEnd()
            proc.WaitForExit()
            if proc.ExitCode = 0 && not (System.String.IsNullOrEmpty out) then Some out
            else None
        with _ -> None
    // macOS
    match tryCmd "pbpaste" [||] with
    | Some s -> Some s
    | None ->
    // Linux (X11)
    match tryCmd "xclip" [| "-o"; "-sel"; "clipboard" |] with
    | Some s -> Some s
    | None ->
    // Linux (Wayland)
    match tryCmd "wl-paste" [||] with
    | Some s -> Some s
    | None -> None

let private expandClipboard (input: string) (strings: Strings) : string =
    if not (input.Contains "@clipboard") then input
    else
        match tryReadClipboard() with
        | None ->
            input.Replace("@clipboard", "[" + strings.ClipboardUnavailable + "]")
        | Some content ->
            let truncated =
                if content.Length > 4000 then content.[..3999] + " " + strings.AtFileTooBig
                else content
            input.Replace("@clipboard", truncated)

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
    let mutable assistantStreaming = false   // are we mid-line in the plain-text stream

    try
        let stream = Conversation.run agent session input streamCts.Token
        let enumerator = stream.GetAsyncEnumerator(streamCts.Token)
        let mutable hasNext = true
        while hasNext do
            let! step = enumerator.MoveNextAsync().AsTask()
            if step then
                match enumerator.Current with
                | Conversation.TextChunk t ->
                    Console.Out.Write t
                    Console.Out.Flush()
                    assistantStreaming <- true
                | Conversation.ToolStarted(id, name, args) ->
                    toolMeta.[id] <- (name, args)
                    if assistantStreaming then
                        Console.Out.WriteLine ()
                        assistantStreaming <- false
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
        if assistantStreaming then Console.Out.WriteLine ()
    with
    | :? OperationCanceledException ->
        if assistantStreaming then Console.Out.WriteLine ()
        AnsiConsole.Write(Render.cancelled strings)
        AnsiConsole.WriteLine()
    | ex ->
        if assistantStreaming then Console.Out.WriteLine ()
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
                let expanded = expandClipboard userInput strings
                AnsiConsole.Write(Render.userMessage cfg.Ui userInput)
                AnsiConsole.WriteLine()
                do! streamAndRender agent session expanded cfg cancelSrc
                StatusBar.refresh ()
    finally
        Console.CancelKeyPress.RemoveHandler handler
        StatusBar.stop ()
}
