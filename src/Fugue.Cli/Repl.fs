module Fugue.Cli.Repl

open System
open System.Collections.Generic
open System.Diagnostics
open System.Diagnostics.CodeAnalysis
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Agents.AI
open Spectre.Console
open Fugue.Core
open Fugue.Core.Config
open Fugue.Core.Localization
open Fugue.Agent
open Fugue.Cli

/// Try to find the git repository root starting from startDir.
/// Runs `git rev-parse --show-toplevel` as a best-effort child process; returns None on any failure.
/// Stderr is redirected and drained concurrently to suppress git's "not a git repository" message.
let private findGitRoot (startDir: string) : string option =
    let psi = System.Diagnostics.ProcessStartInfo()
    psi.FileName <- "git"
    psi.ArgumentList.Add "rev-parse"
    psi.ArgumentList.Add "--show-toplevel"
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.WorkingDirectory <- startDir
    try
        use proc = new System.Diagnostics.Process()
        proc.StartInfo <- psi
        proc.Start() |> ignore
        // Drain stderr concurrently — must happen before WaitForExit to prevent deadlock.
        let errTask = Task.Run(fun () -> proc.StandardError.ReadToEnd() |> ignore)
        let out = proc.StandardOutput.ReadToEnd().Trim()
        proc.WaitForExit()
        errTask.Wait()
        if proc.ExitCode = 0 && not (String.IsNullOrWhiteSpace out) then Some out
        else None
    with _ -> None



/// Search for a sibling test file in the `tests/` subdirectory of cwd.
/// Returns the relative path from cwd if found, or None.
let tryFindTestFile (cwd: string) (sourceFile: string) : string option =
    let stem =
        System.IO.Path.GetFileNameWithoutExtension sourceFile
        |> Option.ofObj
        |> Option.defaultValue ""
    let testsDir = System.IO.Path.Combine(cwd, "tests")
    if not (System.IO.Directory.Exists testsDir) then None
    else
        let candidates = [| stem + "Tests.fs"; stem + "Test.fs" |]
        candidates |> Array.tryPick (fun name ->
            try
                System.IO.Directory.GetFiles(testsDir, name, SearchOption.AllDirectories)
                |> Array.tryHead
                |> Option.map (fun abs ->
                    try System.IO.Path.GetRelativePath(cwd, abs)
                    with _ -> abs)
            with _ -> None)

/// Find @path tokens in a string.
/// Returns list of (startIdx, tokenLength, path) for each @word token that is preceded
/// by start-of-string or whitespace.
let private findAtTokens (input: string) : (int * int * string) list =
    let mutable i = 0
    let mutable results = []
    while i < input.Length do
        if input.[i] = '@' && (i = 0 || Char.IsWhiteSpace input.[i - 1]) then
            let start = i
            i <- i + 1
            let pathStart = i
            while i < input.Length && not (Char.IsWhiteSpace input.[i]) do
                i <- i + 1
            if i > pathStart then
                results <- (start, i - start, input.[start + 1 .. i - 1]) :: results
        else
            i <- i + 1
    List.rev results

/// Expand @path tokens in user input. Each @word that resolves to an existing file
/// within cwd is replaced with its contents (up to 4000 chars). Skips missing, binary,
/// or out-of-cwd files silently or with a dim notice.
let expandAtFiles (cwd: string) (strings: Fugue.Core.Localization.Strings) (input: string) : string =
    let tokens = findAtTokens input
    if tokens.IsEmpty then input
    else
        // Process tokens in reverse order so indices stay valid during replacement
        let mutable result = input
        for (start, length, pathPart) in List.rev tokens do
            let fullPath = Fugue.Tools.PathSafety.resolve cwd pathPart
            if not (Fugue.Tools.PathSafety.isUnder cwd fullPath) then
                ()  // silently skip paths outside cwd
            elif not (System.IO.File.Exists fullPath) then
                AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape (System.String.Format(strings.AtFileNotFound, pathPart))))
            else
                try
                    let content = System.IO.File.ReadAllText fullPath
                    let truncated = content.Length > 4000
                    let body = if truncated then content.[..3999] else content
                    let header = sprintf "[contents of %s]%s:\n" pathPart (if truncated then " " + strings.AtFileTooBig else "")
                    let hint =
                        match tryFindTestFile cwd fullPath with
                        | Some testRel ->
                            let msg = System.String.Format(strings.TestFileHint, testRel, testRel)
                            sprintf "\n\n%s" msg
                        | None -> ""
                    result <- result.[0 .. start - 1] + header + body + hint + result.[start + length ..]
                with _ ->
                    AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape (System.String.Format(strings.AtFileNotFound, pathPart))))
        result

let private providerInfo (p: ProviderConfig) : string * string =
    match p with
    | Anthropic(_, m) -> "anthropic", m
    | OpenAI(_, m)    -> "openai", m
    | Ollama(ep, m)   -> string ep, m
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

/// Unescape a JSON string value (the content between the outer quotes).
/// Handles the standard escapes: \\ \" \/ \n \r \t and \uXXXX.
/// AOT-safe: no Regex, no reflection.
let private unescapeJson (s: string) : string =
    let sb = StringBuilder(s.Length)
    let mutable i = 0
    while i < s.Length do
        if s.[i] = '\\' && i + 1 < s.Length then
            match s.[i + 1] with
            | '"'  -> sb.Append('"')  |> ignore; i <- i + 2
            | '\\' -> sb.Append('\\') |> ignore; i <- i + 2
            | '/'  -> sb.Append('/')  |> ignore; i <- i + 2
            | 'n'  -> sb.Append('\n') |> ignore; i <- i + 2
            | 'r'  -> sb.Append('\r') |> ignore; i <- i + 2
            | 't'  -> sb.Append('\t') |> ignore; i <- i + 2
            | 'b'  -> sb.Append('\b') |> ignore; i <- i + 2
            | 'f'  -> sb.Append('\f') |> ignore; i <- i + 2
            | 'u' when i + 5 < s.Length ->
                try
                    let hex  = s.Substring(i + 2, 4)
                    let code = Convert.ToInt32(hex, 16)
                    sb.Append(char code) |> ignore
                    i <- i + 6
                with _ ->
                    sb.Append('\\') |> ignore
                    i <- i + 1
            | c ->
                sb.Append('\\') |> ignore
                sb.Append(c)    |> ignore
                i <- i + 2
        else
            sb.Append(s.[i]) |> ignore
            i <- i + 1
    sb.ToString()
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
    let mutable childRunning = false
    member _.QuitRequested = quit
    member _.RequestQuit() = quit <- true
    member _.Token = CancellationToken.None
    member _.EnterChild () = childRunning <- true
    member _.ExitChild ()  = childRunning <- false
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
                if childRunning then
                    // Child gets SIGINT via process group; suppress quit.
                    a.Cancel <- true
                else
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

    StatusBar.startStreaming()
    try
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
                        StatusBar.refresh()
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
                        StatusBar.refresh()
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
    finally
        StatusBar.stopStreaming()
        StatusBar.refresh()
}

[<RequiresUnreferencedCode("Calls Conversation.run which uses STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
[<RequiresDynamicCode("Calls Conversation.run which uses STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
let run (agent: AIAgent) (cfg: AppConfig) (cwd: string) : Task<unit> = task {
    use cancelSrc = new CancelSource()
    let handler = ConsoleCancelEventHandler(fun _ args -> cancelSrc.OnCtrlC args)
    Console.CancelKeyPress.AddHandler handler
    let! initialSession = agent.CreateSessionAsync(CancellationToken.None)
    let mutable session : AgentSession | null = initialSession
    let providerName, modelName = providerInfo cfg.Provider
    DebugLog.sessionStart providerName modelName cwd
    StatusBar.start cwd cfg
    Console.Out.Write "\x1b[?2004h"   // enable bracketed paste
    Console.Out.Flush()

    let strings = pick cfg.Ui.Locale
    let mutable verbosityPrefix : string option = None
    try
        while not cancelSrc.QuitRequested do
            let callbacks : ReadLine.ReadLineCallbacks = { OnClearScreen = StatusBar.refresh }
            let! lineOpt = ReadLine.readAsync (Render.prompt cwd) strings callbacks cancelSrc.Token
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
                                  "/ask <q>",        strings.CmdAskDesc
                                  "/clear",          strings.CmdClearDesc
                                  "/summary",         strings.CmdSummaryDesc
                                  "/squash <N>",      strings.CmdSquashDesc
                                  "/short",           strings.CmdShortDesc
                                  "/long",            strings.CmdLongDesc
                                  "/clear-history",   strings.CmdClearHistoryDesc
                                  "/tools",           strings.CmdToolsDesc
                                  "/todo",            strings.CmdTodoDesc
                                  "/summarize <p>",   strings.CmdSummarizeDesc
                                  "/new",             strings.CmdNewDesc
                                  "/diff",           strings.CmdDiffDesc
                                  "/init",           strings.CmdInitDesc
                                  "/git-log",        strings.CmdGitLogDesc
                                  "/issue <N>",      strings.CmdIssueDesc
                                  "/model suggest",  strings.CmdModelDesc
                                  "/onboard",        strings.CmdOnboardDesc
                                  "/exit",           strings.CmdExitDesc
                                  "/review pr <N>",  strings.CmdReviewPrDesc ]
                for (name, desc) in helpItems do
                    AnsiConsole.Write(Markup("  [cyan]" + Markup.Escape name + "[/]  [dim]" + Markup.Escape desc + "[/]"))
                    AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/git-log" ->
                let psi = System.Diagnostics.ProcessStartInfo()
                psi.FileName <- "git"
                psi.ArgumentList.Add "log"
                psi.ArgumentList.Add "--oneline"
                psi.ArgumentList.Add "--decorate"
                psi.ArgumentList.Add "-20"
                psi.UseShellExecute <- false
                psi.RedirectStandardOutput <- true
                psi.WorkingDirectory <- cwd
                try
                    use proc = new System.Diagnostics.Process()
                    proc.StartInfo <- psi
                    match proc.Start() with
                    | false ->
                        AnsiConsole.Write(Render.errorLine strings strings.GitLogNoRepo)
                        AnsiConsole.WriteLine()
                    | true ->
                        let out = proc.StandardOutput.ReadToEnd()
                        proc.WaitForExit()
                        if proc.ExitCode <> 0 || String.IsNullOrWhiteSpace out then
                            AnsiConsole.Write(Render.errorLine strings strings.GitLogNoRepo)
                            AnsiConsole.WriteLine()
                        else
                            let prompt = strings.GitLogPrompt.Replace("%s", out)
                            do! streamAndRender agent session prompt cfg cancelSrc
                with ex ->
                    AnsiConsole.Write(Render.errorLine strings ex.Message)
                    AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/clear" ->
                AnsiConsole.Clear()
                StatusBar.refresh ()
            | Some s when s = "/squash" || s.StartsWith "/squash " ->
                let rawArg = if s = "/squash" then "" else s.Substring(8).TrimStart()
                let n = if String.IsNullOrWhiteSpace rawArg then 0 else (try int rawArg with _ -> 0)
                if n <= 0 then
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.SquashUsage + "[/]"))
                    AnsiConsole.WriteLine()
                else
                    let psi = System.Diagnostics.ProcessStartInfo()
                    psi.FileName <- "git"
                    psi.ArgumentList.Add "log"
                    psi.ArgumentList.Add "--oneline"
                    psi.ArgumentList.Add (sprintf "-%d" n)
                    psi.UseShellExecute <- false
                    psi.RedirectStandardOutput <- true
                    psi.WorkingDirectory <- cwd
                    try
                        use proc = new System.Diagnostics.Process()
                        proc.StartInfo <- psi
                        match proc.Start() with
                        | false ->
                            AnsiConsole.Write(Render.errorLine strings strings.SquashNoRepo)
                            AnsiConsole.WriteLine()
                        | true ->
                            let out = proc.StandardOutput.ReadToEnd()
                            proc.WaitForExit()
                            if proc.ExitCode <> 0 || String.IsNullOrWhiteSpace out then
                                AnsiConsole.Write(Render.errorLine strings strings.SquashNoRepo)
                                AnsiConsole.WriteLine()
                            else
                                let prompt = sprintf """Here are the last %d commits to be squashed:\n\n%s\n\nPlease generate a single conventional commit message that summarizes all these changes.\nFollow the format: type(scope): description\n\nAlso show the git command to execute the squash:\n  git reset --soft HEAD~%d && git commit -m "<your message>"\n\nDo NOT run the command — just show it for the user to review and run manually.""" n out n
                                do! streamAndRender agent session prompt cfg cancelSrc
                    with ex ->
                        AnsiConsole.Write(Render.errorLine strings ex.Message)
                        AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/short" ->
                verbosityPrefix <- Some "[VERBOSITY: respond briefly, 1-2 sentences per point, no elaboration unless asked]\n\n"
                AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.VerbosityShortSet + "[/]"))
                AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/long" ->
                verbosityPrefix <- Some "[VERBOSITY: respond in detail, include explanations, examples, and context]\n\n"
                AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.VerbosityLongSet + "[/]"))
                AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/clear-history" ->
                match box session with
                | :? IAsyncDisposable as d ->
                    try do! d.DisposeAsync().AsTask() with _ -> ()
                | _ -> ()
                try
                    let! newSession = agent.CreateSessionAsync(CancellationToken.None)
                    session <- newSession
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.ClearHistoryDone + "[/]"))
                    AnsiConsole.WriteLine()
                with ex ->
                    session <- null
                    AnsiConsole.Write(Render.errorLine strings ex.Message)
                    AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/tools" ->
                AnsiConsole.Write(Markup("[bold]" + Markup.Escape strings.ToolsHeader + "[/]"))
                AnsiConsole.WriteLine()
                for (name, desc) in Fugue.Tools.ToolRegistry.descriptions do
                    AnsiConsole.Write(Markup("  [cyan]" + Markup.Escape name + "[/]  [dim]" + Markup.Escape desc + "[/]"))
                    AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/issue" || s.StartsWith "/issue " ->
                let rawArg = if s = "/issue" then "" else s.Substring(7).TrimStart()
                if String.IsNullOrWhiteSpace rawArg then
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.IssueUsage + "[/]"))
                    AnsiConsole.WriteLine()
                else
                    let psi = System.Diagnostics.ProcessStartInfo()
                    psi.FileName <- "gh"
                    psi.ArgumentList.Add "issue"
                    psi.ArgumentList.Add "view"
                    psi.ArgumentList.Add rawArg
                    psi.ArgumentList.Add "--json"
                    psi.ArgumentList.Add "number,title,body"
                    psi.UseShellExecute <- false
                    psi.RedirectStandardOutput <- true
                    psi.RedirectStandardError <- true
                    psi.WorkingDirectory <- cwd
                    try
                        use proc = new System.Diagnostics.Process()
                        proc.StartInfo <- psi
                        match proc.Start() with
                        | false ->
                            AnsiConsole.Write(Render.errorLine strings (String.Format(strings.IssueNotFound, rawArg)))
                            AnsiConsole.WriteLine()
                        | true ->
                            let output = proc.StandardOutput.ReadToEnd()
                            proc.WaitForExit()
                            if proc.ExitCode <> 0 then
                                let err = proc.StandardError.ReadToEnd().Trim()
                                AnsiConsole.Write(Render.errorLine strings (String.Format(strings.IssueNotFound, err)))
                                AnsiConsole.WriteLine()
                            else
                                // Extract a string field from compact gh JSON output (AOT-safe, no JsonSerializer)
                                let extractField (field: string) (json: string) =
                                    let key = sprintf "\"%s\":" field
                                    let idx = json.IndexOf key
                                    if idx < 0 then ""
                                    else
                                        let start = idx + key.Length
                                        let rest = json.Substring(start).TrimStart()
                                        if rest.StartsWith "\"" then
                                            // Collect raw JSON string content (skip escaped closing quotes)
                                            let mutable i = 1
                                            let sb = StringBuilder()
                                            while i < rest.Length && rest.[i] <> '"' do
                                                if rest.[i] = '\\' && i + 1 < rest.Length then
                                                    sb.Append(rest.[i])     |> ignore
                                                    sb.Append(rest.[i + 1]) |> ignore
                                                    i <- i + 2
                                                else
                                                    sb.Append rest.[i] |> ignore
                                                    i <- i + 1
                                            unescapeJson (sb.ToString())
                                        else
                                            let j = rest.IndexOfAny [|','; '}'; '\n'|]
                                            if j < 0 then rest else rest.Substring(0, j)
                                let title = extractField "title" output
                                let body  = extractField "body"  output
                                let issueContext = sprintf "Issue #%s: %s\n\n%s" rawArg title body
                                AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape (String.Format(strings.IssueFetched, rawArg, title))))
                                AnsiConsole.WriteLine()
                                // Best-effort branch creation
                                let slugify (src: string) =
                                    src.ToLowerInvariant()
                                    |> String.map (fun c -> if Char.IsLetterOrDigit c then c else '-')
                                    |> fun s -> s.Trim '-'
                                let words =
                                    title.Split([|' '; '/'; ':'; '('; ')'; '['; ']'|], StringSplitOptions.RemoveEmptyEntries)
                                    |> Array.truncate 5
                                    |> Array.map slugify
                                    |> String.concat "-"
                                let branchName = sprintf "feat/issue-%s-%s" rawArg words
                                let gitPsi = System.Diagnostics.ProcessStartInfo()
                                gitPsi.FileName <- "git"
                                gitPsi.ArgumentList.Add "checkout"
                                gitPsi.ArgumentList.Add "-b"
                                gitPsi.ArgumentList.Add branchName
                                gitPsi.UseShellExecute <- false
                                gitPsi.RedirectStandardError <- true
                                gitPsi.WorkingDirectory <- cwd
                                try
                                    use gitProc = new System.Diagnostics.Process()
                                    gitProc.StartInfo <- gitPsi
                                    if gitProc.Start() then
                                        gitProc.WaitForExit()
                                        if gitProc.ExitCode = 0 then
                                            AnsiConsole.MarkupLine(sprintf "[dim]created branch %s[/]" (Markup.Escape branchName))
                                            AnsiConsole.WriteLine()
                                with _ -> ()   // git unavailable or not a repo — silent
                                // Inject issue context into conversation
                                do! streamAndRender agent session issueContext cfg cancelSrc
                    with ex ->
                        AnsiConsole.Write(Render.errorLine strings ex.Message)
                        AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/model suggest" ->
                AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.AskingModelRecommendation + "[/]"))
                AnsiConsole.WriteLine()
                do! streamAndRender agent session strings.ModelSuggestPrompt cfg cancelSrc
                StatusBar.refresh ()
            | Some s when s = "/onboard" ->
                let tryRead (p: string) =
                    if System.IO.File.Exists p then
                        try Some (System.IO.File.ReadAllText p) with _ -> None
                    else None
                let fugueContent =
                    let cwdFile = System.IO.Path.Combine(cwd, "FUGUE.md")
                    match tryRead cwdFile with
                    | Some c -> Some c
                    | None ->
                        match findGitRoot cwd with
                        | Some root when root <> cwd ->
                            tryRead (System.IO.Path.Combine(root, "FUGUE.md"))
                        | _ -> None
                let treeOutput =
                    try
                        System.IO.Directory.GetFileSystemEntries(cwd)
                        |> Array.choose (fun p ->
                            match System.IO.Path.GetFileName p with
                            | null -> None
                            | n    -> Some n)
                        |> Array.sort
                        |> String.concat "\n"
                    with _ -> "(could not read directory)"
                let contextParts =
                    [ match fugueContent with
                      | Some content ->
                          let trimmed =
                              if content.Length > 4000 then content.[..3999] + "\n...(truncated)"
                              else content
                          yield sprintf "=== FUGUE.md ===\n%s" trimmed
                      | None -> yield "(no FUGUE.md found)"
                      yield sprintf "=== Top-level directory contents of %s ===\n%s" cwd treeOutput ]
                let onboardPrompt =
                    sprintf """Based on the following project context, generate a developer onboarding checklist.
Include: setup steps, key files to read, how to build/test, coding conventions, and first tasks to attempt.

%s

Please generate a clear, actionable onboarding checklist.""" (String.concat "\n\n" contextParts)
                do! streamAndRender agent session onboardPrompt cfg cancelSrc
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
            | Some s when s.StartsWith "/ask " ->
                let question = s.Substring(5).Trim()
                if String.IsNullOrWhiteSpace question then
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.AskUsage + "[/]"))
                    AnsiConsole.WriteLine()
                else
                    let augmented = sprintf "[System: answer from your training knowledge only — do not invoke any tools, do not read files, do not run commands]\n\nUser: %s" question
                    AnsiConsole.Write(Render.userMessage cfg.Ui question)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session augmented cfg cancelSrc
                StatusBar.refresh ()
            | Some s when s = "/ask" ->
                AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.AskUsage + "[/]"))
                AnsiConsole.WriteLine()
            | Some s when s = "/diff" || s = "/diff --staged" ->
                let args = if s = "/diff --staged" then "--staged" else ""
                let psi = System.Diagnostics.ProcessStartInfo()
                psi.FileName <- "git"
                psi.ArgumentList.Add "diff"
                if args <> "" then psi.ArgumentList.Add args
                psi.UseShellExecute <- false
                psi.RedirectStandardOutput <- true
                psi.WorkingDirectory <- cwd
                use proc = new System.Diagnostics.Process()
                proc.StartInfo <- psi
                try
                    proc.Start() |> ignore
                    let output = proc.StandardOutput.ReadToEnd()
                    proc.WaitForExit()
                    if System.String.IsNullOrWhiteSpace output then
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.NoDiff + "[/]"))
                        AnsiConsole.WriteLine()
                    else
                        AnsiConsole.Write(DiffRender.toRenderable output)
                        AnsiConsole.WriteLine()
                with ex ->
                    AnsiConsole.Write(Render.errorLine strings ex.Message)
                    AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/init" ->
                let fugueMdPath = System.IO.Path.Combine(cwd, "FUGUE.md")
                if System.IO.File.Exists fugueMdPath then
                    AnsiConsole.Write(Markup("[yellow]" + Markup.Escape strings.InitExists + "[/]"))
                    AnsiConsole.WriteLine()
                    StatusBar.refresh ()
                else
                    let initPrompt =
                        "Please analyze this project and create a FUGUE.md file in the current directory.\n\n" +
                        "FUGUE.md is a project context file for Fugue (an AI coding assistant). It should include:\n" +
                        "- Project purpose and target audience\n" +
                        "- Architecture overview (key directories, modules, patterns)\n" +
                        "- Tech stack and dependencies\n" +
                        "- Development conventions (naming, style, testing approach)\n" +
                        "- Common commands (build, test, run, deploy)\n" +
                        "- Any gotchas or non-obvious constraints\n\n" +
                        "Keep it concise — aim for 50-150 lines. Use Markdown headers.\n" +
                        "Write the file to ./FUGUE.md using the Write tool."
                    AnsiConsole.Write(Render.userMessage cfg.Ui "/init")
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session initPrompt cfg cancelSrc
                    StatusBar.refresh ()
            | Some s when s = "/new" ->
                AnsiConsole.Clear()
                match box session with
                | :? IAsyncDisposable as d ->
                    try do! d.DisposeAsync().AsTask() with _ -> ()
                | _ -> ()
                try
                    let! newSession = agent.CreateSessionAsync(CancellationToken.None)
                    session <- newSession
                with ex ->
                    session <- null
                    AnsiConsole.Write(Render.errorLine strings ex.Message)
                    AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s.StartsWith "!" ->
                let cmd = s.Substring(1).Trim()
                if not (String.IsNullOrWhiteSpace cmd) then
                    let shell =
                        System.Environment.GetEnvironmentVariable "SHELL"
                        |> Option.ofObj
                        |> Option.filter (fun s -> s <> "")
                        |> Option.defaultValue "/bin/sh"
                    let psi = System.Diagnostics.ProcessStartInfo()
                    psi.FileName <- shell
                    psi.ArgumentList.Add "-c"
                    psi.ArgumentList.Add cmd
                    psi.UseShellExecute <- false
                    psi.WorkingDirectory <- cwd
                    use proc = new System.Diagnostics.Process()
                    proc.StartInfo <- psi
                    cancelSrc.EnterChild ()
                    StatusBar.stop ()
                    try
                        try
                            proc.Start() |> ignore
                            proc.WaitForExit()
                        with ex ->
                            AnsiConsole.Write(Render.errorLine strings ex.Message)
                            AnsiConsole.WriteLine()
                    finally
                        cancelSrc.ExitChild ()
                        StatusBar.start cwd cfg
            | Some s when s = "/summary" ->
                let summaryPrompt =
                    "Please generate a structured summary of our session so far.\n\n" +
                    "Include:\n" +
                    "- What we worked on (tasks, files changed, problems solved)\n" +
                    "- Key decisions made\n" +
                    "- Any open questions or next steps\n\n" +
                    "Be concise but complete."
                AnsiConsole.Write(Render.userMessage cfg.Ui "/summary")
                AnsiConsole.WriteLine()
                do! streamAndRender agent session summaryPrompt cfg cancelSrc
                StatusBar.refresh ()
            | Some s when s = "/todo" ->
                let tryRun (exe: string) (args: string[]) =
                    try
                        let psi = System.Diagnostics.ProcessStartInfo()
                        psi.FileName <- exe
                        for a in args do psi.ArgumentList.Add a
                        psi.UseShellExecute <- false
                        psi.RedirectStandardOutput <- true
                        psi.RedirectStandardError <- true
                        psi.WorkingDirectory <- cwd
                        use proc = new System.Diagnostics.Process()
                        proc.StartInfo <- psi
                        match proc.Start() with
                        | false -> None
                        | true ->
                            let out = proc.StandardOutput.ReadToEnd()
                            proc.WaitForExit()
                            if proc.ExitCode = 0 && not (String.IsNullOrWhiteSpace out) then Some out
                            elif proc.ExitCode = 1 then Some ""  // grep/rg return 1 for no matches
                            else None
                    with _ -> None
                let results =
                    match tryRun "rg" [| "--line-number"; "--with-filename"; "--no-heading";
                                         "-e"; "TODO"; "-e"; "FIXME"; "-e"; "HACK"; "." |] with
                    | Some r -> Some r
                    | None ->
                        tryRun "grep" [| "-rn"; "-E"; "TODO|FIXME|HACK"; "." |]
                match results with
                | None ->
                    AnsiConsole.Write(Render.errorLine strings "rg and grep unavailable")
                    AnsiConsole.WriteLine()
                | Some "" ->
                    AnsiConsole.MarkupLine("[dim]" + Markup.Escape strings.TodoNone + "[/]")
                    AnsiConsole.WriteLine()
                | Some output ->
                    let lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    let truncated, note =
                        if lines.Length > 200 then
                            Array.take 200 lines, sprintf "\n\n[truncated — showing 200 of %d matches]" lines.Length
                        else lines, ""
                    let body = String.concat "\n" truncated
                    let summary =
                        sprintf "Found %d TODO/FIXME/HACK items in workspace:\n\n%s%s\n\nPlease review these and suggest which ones are worth addressing now."
                            lines.Length body note
                    do! streamAndRender agent session summary cfg cancelSrc
                StatusBar.refresh ()
            | Some s when s.StartsWith "/summarize" ->
                let rawArg = s.Substring("/summarize".Length).Trim()
                if System.String.IsNullOrWhiteSpace rawArg then
                    AnsiConsole.Write(Markup("[dim]Usage: /summarize <path>[/]"))
                    AnsiConsole.WriteLine()
                else
                    let targetPath =
                        if System.IO.Path.IsPathRooted rawArg then rawArg
                        else System.IO.Path.GetFullPath(System.IO.Path.Combine(cwd, rawArg))
                    let summarizePrompt =
                        if System.IO.Directory.Exists targetPath then
                            sprintf "Please summarize the directory '%s'.\n\nProvide a structured summary:\n1. Purpose — what does this module/package do?\n2. Public API — key functions, types, or entry points\n3. Key dependencies — what does it depend on?\n4. Notable design decisions — any interesting patterns or constraints\n\nBe concise (2–4 sentences per section). Use the Read and Glob tools to explore the files." rawArg
                        elif System.IO.File.Exists targetPath then
                            sprintf "Please summarize the file '%s'.\n\nProvide a structured summary:\n1. Purpose — what does this file do?\n2. Public API — exported functions or types\n3. Key dependencies — what modules/libraries does it use?\n4. Notable design decisions — any interesting patterns or constraints\n\nBe concise (2–4 sentences per section). Use the Read tool to examine the file." rawArg
                        else
                            sprintf "The path '%s' does not exist. Please let me know." rawArg
                    AnsiConsole.Write(Render.userMessage cfg.Ui ("/summarize " + rawArg))
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session summarizePrompt cfg cancelSrc
                StatusBar.refresh ()
            | Some userInput ->
                if ReadLine.hasZeroWidth userInput then
                    AnsiConsole.Write(Markup("[dim yellow]" + Markup.Escape strings.ZeroWidthWarning + "[/]"))
                    AnsiConsole.WriteLine()
                let expandedInput = expandAtFiles cwd strings userInput
                let expandedInput = expandClipboard expandedInput strings
                AnsiConsole.Write(Render.userMessage cfg.Ui userInput)
                AnsiConsole.WriteLine()
                let effectiveInput =
                    match verbosityPrefix with
                    | Some prefix -> prefix + expandedInput
                    | None -> expandedInput
                let sw = System.Diagnostics.Stopwatch.StartNew()
                do! streamAndRender agent session effectiveInput cfg cancelSrc
                StatusBar.refresh ()
    finally
        Console.CancelKeyPress.RemoveHandler handler
        Console.Out.Write "\x1b[?2004l"   // disable bracketed paste
        Console.Out.Flush()
        StatusBar.stop ()
}
