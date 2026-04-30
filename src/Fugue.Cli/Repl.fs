module Fugue.Cli.Repl

open System
open System.Collections.Generic
open System.Diagnostics
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

/// Run an external command and return Some stdout on exit 0, None on failure.
/// stderr is not redirected to avoid deadlock risk with unbuffered tools like gh.
let private runCmd (exe: string) (args: string[]) (workingDir: string) : string option =
    try
        let psi = ProcessStartInfo(exe, args)
        psi.WorkingDirectory <- workingDir
        psi.RedirectStandardOutput <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        match Process.Start psi with
        | null -> None
        | p ->
            use _ = p
            let stdout = p.StandardOutput.ReadToEnd()
            p.WaitForExit()
            if p.ExitCode = 0 then Some stdout else None
    with _ -> None

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
                let helpItems = [ "/help",           strings.CmdHelpDesc
                                  "/clear",          strings.CmdClearDesc
                                  "/exit",           strings.CmdExitDesc
                                  "/review pr <N>",  strings.CmdReviewPrDesc ]
                for (name, desc) in helpItems do
                    AnsiConsole.Write(Markup("  [cyan]" + Markup.Escape name + "[/]  [dim]" + Markup.Escape desc + "[/]"))
                    AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/clear" ->
                AnsiConsole.Clear()
                StatusBar.refresh ()
            | Some s when s = "/review pr" || s = "/review pr " ->
                AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.ReviewPrUsage + "[/]"))
                AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/review pr " ->
                let arg = s.Substring(11).Trim()
                match System.Int32.TryParse arg with
                | false, _ ->
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.ReviewPrUsage + "[/]"))
                    AnsiConsole.WriteLine()
                | true, prNum when prNum < 1 ->
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.ReviewPrUsage + "[/]"))
                    AnsiConsole.WriteLine()
                | true, prNum ->
                    let meta = runCmd "gh" [| "pr"; "view"; string prNum; "--json"; "title,body" |] cwd
                    let diff = runCmd "gh" [| "pr"; "diff"; string prNum |] cwd
                    match diff with
                    | None ->
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.ReviewPrNotFound + "[/]"))
                        AnsiConsole.WriteLine()
                    | Some diffText ->
                        let truncatedDiff =
                            if diffText.Length > 8000 then diffText.[..7999] + "\n[diff truncated]"
                            else diffText
                        let titleBody = meta |> Option.defaultValue ""
                        let prompt =
                            strings.ReviewPrPrompt
                                .Replace("{0}", string prNum)
                                .Replace("{1}", titleBody)
                                .Replace("{2}", truncatedDiff)
                        AnsiConsole.Write(Render.userMessage cfg.Ui (sprintf "Reviewing PR #%d…" prNum))
                        AnsiConsole.WriteLine()
                        do! streamAndRender agent session prompt cfg cancelSrc
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
