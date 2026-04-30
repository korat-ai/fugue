module Fugue.Cli.Repl

open System
open System.Collections.Generic
open System.Diagnostics.CodeAnalysis
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Agents.AI
open Spectre.Console
open Fugue.Core.Config
open Fugue.Core.Localization
open Fugue.Agent
open Fugue.Cli

/// Search for a sibling test file in the `tests/` subdirectory of cwd (up to 3 levels deep).
/// Returns the relative path from cwd if found, or None.
let tryFindTestFile (cwd: string) (sourceFile: string) : string option =
    let stem =
        match Path.GetFileNameWithoutExtension sourceFile |> Option.ofObj with
        | Some s -> s
        | None   -> ""
    let testsDir = Path.Combine(cwd, "tests")
    if not (Directory.Exists testsDir) then None
    else
        let candidates = [| stem + "Tests.fs"; stem + "Test.fs" |]
        let found =
            candidates |> Array.tryPick (fun name ->
                try
                    Directory.GetFiles(testsDir, name, SearchOption.AllDirectories)
                    |> Array.tryHead
                with _ -> None)
        found |> Option.map (fun abs ->
            try Path.GetRelativePath(cwd, abs)
            with _ -> abs)

/// Expand `@path` tokens in user input. For each token, inlines the file contents.
/// If a sibling test file is found, appends a hint. Returns the expanded string.
let expandAtFiles (cwd: string) (strings: Strings) (input: string) : string =
    let words = input.Split(' ')
    let anyAt = words |> Array.exists (fun w -> w.StartsWith "@" && w.Length > 1)
    if not anyAt then input
    else
        let sb = StringBuilder()
        for i in 0 .. words.Length - 1 do
            let w = words.[i]
            if w.StartsWith "@" && w.Length > 1 then
                let relPath = w.Substring 1
                let absPath =
                    if Path.IsPathRooted relPath then relPath
                    else Path.Combine(cwd, relPath)
                if File.Exists absPath then
                    let contents =
                        try File.ReadAllText absPath
                        with ex -> sprintf "[error reading %s: %s]" relPath ex.Message
                    if sb.Length > 0 then sb.Append ' ' |> ignore
                    sb.Append(sprintf "[contents of %s]:\n%s" relPath contents) |> ignore
                    match tryFindTestFile cwd absPath with
                    | Some testRel ->
                        let hint = String.Format(strings.TestFileHint, testRel, testRel)
                        sb.Append(sprintf "\n\n%s" hint) |> ignore
                    | None -> ()
                else
                    if sb.Length > 0 then sb.Append ' ' |> ignore
                    sb.Append w |> ignore
            else
                if sb.Length > 0 then sb.Append ' ' |> ignore
                sb.Append w |> ignore
        sb.ToString()

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
                let expandedInput = expandAtFiles cwd strings userInput
                AnsiConsole.Write(Render.userMessage cfg.Ui userInput)
                AnsiConsole.WriteLine()
                do! streamAndRender agent session expandedInput cfg cancelSrc
                StatusBar.refresh ()
    finally
        Console.CancelKeyPress.RemoveHandler handler
        StatusBar.stop ()
}
