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
open Fugue.Tools.PathSafety

// Session-scoped state: most recently accessed file path for @last expansion.
let mutable private lastFile : string option = None

// Zen mode: suppress status bar and tool output bullets.
let mutable private zenMode = false

// Snippet store: name → (language, code, source)
let mutable private snippetStore : Map<string, string * string * string> = Map.empty

// Bookmark store: ordered list of (name, filePath, startLine, endLine)
let mutable private bookmarks : (string * string * int * int) list = []

// Activity store: file path → edit count
let mutable private activityStore : Map<string, int> = Map.empty

// Alias store: short name → expansion
let mutable private aliasStore : Map<string, string> = Map.empty

// Scratch buffer: accumulated lines
let mutable private scratchLines : string list = []

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


/// Search for a sibling test file in the `tests/` subdirectory of cwd (recursively).
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
            // @last expands to the most recently accessed file path
            let pathPart =
                if pathPart = "last" then
                    match lastFile with
                    | Some p -> p
                    | None   -> pathPart
                else pathPart
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

/// Expand @clip (most recent tool output), @clip1..@clip10 in user input.
let private expandClipRing (input: string) : string =
    if not (input.Contains "@clip") then input
    else
        let mutable result = input
        // Numbered slots first to avoid partial replacement of @clip in @clip1..@clip10
        for n in ClipRing.Capacity .. -1 .. 1 do
            let token = sprintf "@clip%d" n
            if result.Contains token then
                let replacement = ClipRing.get n |> Option.defaultValue (sprintf "[clip%d empty]" n)
                let truncated = if replacement.Length > 4000 then replacement.[..3999] + " …" else replacement
                result <- result.Replace(token, truncated)
        // Plain @clip = @clip1
        if result.Contains "@clip" then
            let replacement = ClipRing.get 1 |> Option.defaultValue "[clip empty]"
            let truncated = if replacement.Length > 4000 then replacement.[..3999] + " …" else replacement
            result <- result.Replace("@clip", truncated)
        result

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
        (cfg: AppConfig) (cancelSrc: CancelSource) (zen: bool) : Task<unit> = task {
    use streamCts = cancelSrc.NewStreamCts()
    let strings = pick cfg.Ui.Locale
    let toolMeta = Dictionary<string, string * string * int64>()
    let mutable assistantStreaming = false   // are we mid-line in the plain-text stream
    let mutable toolCallsThisTurn = 0
    let responseText = StringBuilder()

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
                        responseText.Append t |> ignore
                        StatusBar.recordToken ()
                        assistantStreaming <- true
                    | Conversation.ToolStarted(id, name, args) ->
                        toolMeta.[id] <- (name, args, Stopwatch.GetTimestamp())
                        toolCallsThisTurn <- toolCallsThisTurn + 1
                        if assistantStreaming then
                            Console.Out.WriteLine ()
                            assistantStreaming <- false
                        // Track last accessed file for @last expansion
                        if name = "Read" || name = "Write" || name = "Edit" then
                            let key = "\"path\":"
                            let idx = args.IndexOf key
                            if idx >= 0 then
                                let rest = args.Substring(idx + key.Length).TrimStart()
                                if rest.StartsWith '"' then
                                    let endIdx = rest.IndexOf('"', 1)
                                    if endIdx > 1 then lastFile <- Some rest.[1..endIdx-1]
                        StatusBar.refresh()
                    | Conversation.ToolCompleted(id, output, isErr) ->
                        let output =
                            if cfg.LowBandwidth && not isErr then
                                let lines = output.Split('\n')
                                if lines.Length > 500 then
                                    String.concat "\n" lines.[..499] + sprintf "\n… [low-bandwidth: %d lines truncated]" (lines.Length - 500)
                                else output
                            else output
                        let name, args, elapsed =
                            match toolMeta.TryGetValue id with
                            | true, (n, a, tick) -> n, a, Stopwatch.GetElapsedTime(tick)
                            | false, _ -> "?", "?", TimeSpan.Zero
                        // Track file edits for /activity heatmap
                        if not isErr && (name = "Write" || name = "Edit") then
                            let key = "\"path\":"
                            let idx = args.IndexOf key
                            if idx >= 0 then
                                let rest = args.Substring(idx + key.Length).TrimStart()
                                if rest.StartsWith '"' then
                                    let endIdx = rest.IndexOf('"', 1)
                                    if endIdx > 1 then
                                        let p = rest.[1..endIdx-1]
                                        let count = activityStore |> Map.tryFind p |> Option.defaultValue 0
                                        activityStore <- activityStore |> Map.add p (count + 1)
                        if not isErr && output.Length > 0 then
                            ClipRing.push output
                        let state =
                            if isErr then Render.Failed(name, args, output, elapsed)
                            else Render.Completed(name, args, output, elapsed)
                        if not zen then
                            AnsiConsole.Write(Render.toolBullet strings state)
                            AnsiConsole.WriteLine ()
                        StatusBar.refresh()
                    | Conversation.Finished -> ()
                    | Conversation.Failed ex -> raise ex
                else hasNext <- false
            if assistantStreaming then
                Console.Out.WriteLine ()
                let text = responseText.ToString()
                if Render.isColorEnabled () then
                    // Replace raw streamed text with formatted markdown.
                    // Count how many terminal rows the raw output occupied, move cursor
                    // back to the start of the response, clear to end, then re-render.
                    let termW = max 20 Console.WindowWidth
                    let rawLines =
                        text.TrimEnd([|'\n'; '\r'|]).Split('\n')
                        |> Array.sumBy (fun line ->
                            let l = line.Length
                            if l = 0 then 1 else (l + termW - 1) / termW)
                    if rawLines > 0 then
                        Console.Out.Write(sprintf "\x1b[%dA\x1b[J" rawLines)
                        Console.Out.Flush()
                    if Render.isTypewriterMode () then
                        // Render to ANSI string, then write line-by-line with async delay.
                        let lines = Render.captureLines (Render.assistantFinal text)
                        for line in lines do
                            Console.Out.WriteLine line
                            Console.Out.Flush()
                            do! Task.Delay 12
                    else
                        AnsiConsole.Write(Render.assistantFinal text)
                        AnsiConsole.WriteLine()
                let wordCount = text.Split([|' '; '\n'; '\r'; '\t'|], StringSplitOptions.RemoveEmptyEntries).Length
                StatusBar.recordWords wordCount
                if wordCount >= 20 then
                    let mins = max 1 (int (Math.Round(float wordCount / 220.0)))
                    let timeStr = if mins = 1 then "~1 min read" else sprintf "~%d min read" mins
                    let footer = sprintf "[~%d words · %s]" wordCount timeStr
                    if Render.isColorEnabled () then
                        AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape footer))
                    else
                        Console.Out.WriteLine footer
                if toolCallsThisTurn >= 10 then
                    let msg = strings.ToolCallsWarning.Replace("{0}", string toolCallsThisTurn)
                    if Render.isColorEnabled () then
                        AnsiConsole.MarkupLine(sprintf "[dim yellow]⚠️ %s[/]" (Markup.Escape msg))
                    else
                        Console.Out.WriteLine(sprintf "⚠️ %s" msg)
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

let private bellChar () = Console.Out.Write '\a'

let private prelude (cfg: AppConfig) =
    if cfg.Ui.Bell then
        bellChar ()
        System.Threading.Thread.Sleep 80
        bellChar ()

let private coda (cfg: AppConfig) =
    if cfg.Ui.Bell then bellChar ()

[<RequiresUnreferencedCode("Calls Conversation.run which uses STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
[<RequiresDynamicCode("Calls Conversation.run which uses STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
let run (agent: AIAgent) (cfg: AppConfig) (cwd: string) (lastSummary: string option) : Task<unit> = task {
    let sessionStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    use cancelSrc = new CancelSource()
    let handler = ConsoleCancelEventHandler(fun _ args -> cancelSrc.OnCtrlC args)
    Console.CancelKeyPress.AddHandler handler
    let! initialSession = agent.CreateSessionAsync(CancellationToken.None)
    let mutable session : AgentSession | null = initialSession
    let providerName, modelName = providerInfo cfg.Provider
    DebugLog.sessionStart providerName modelName cwd
    StatusBar.start cwd cfg
    Render.showBanner ()
    // Show last-session resuming hint if available
    let strings0 = pick cfg.Ui.Locale
    lastSummary |> Option.iter (fun s ->
        let first = let i = s.IndexOf('.') in if i > 0 && i < 120 then s.[..i - 1] else s.[..min 119 (s.Length - 1)]
        let msg = strings0.SessionResuming.Replace("{0}", first)
        if Render.isColorEnabled () then
            AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape msg))
        else
            Console.Out.WriteLine msg
        AnsiConsole.WriteLine())
    // Show template badge on startup
    cfg.TemplateName |> Option.iter (fun name ->
        if Render.isColorEnabled () then
            AnsiConsole.MarkupLine(sprintf "[dim cyan]▸ template: %s[/]" (Markup.Escape name))
        else
            Console.Out.WriteLine(sprintf "template: %s" name)
        AnsiConsole.WriteLine())
    prelude cfg
    Console.Out.Write "\x1b[?2004h"   // enable bracketed paste
    Console.Out.Flush()

    // Load .fugue/ignore patterns (lines starting with # are comments)
    let ignorePatterns =
        let p = System.IO.Path.Combine(cwd, ".fugue", "ignore")
        if System.IO.File.Exists p then
            System.IO.File.ReadAllLines p
            |> Array.filter (fun l -> not (l.TrimStart().StartsWith '#') && l.Trim() <> "")
            |> Array.toList
        else []
    let isIgnored (relativePath: string) =
        let rel = relativePath.Replace('\\', '/')
        ignorePatterns |> List.exists (fun pat ->
            let pat = pat.TrimStart('!')
            // Simple matching: pattern ending in / matches any path containing that segment
            let pat = pat.Replace('\\', '/')
            if pat.EndsWith '/' then rel.Contains(pat.TrimEnd('/') + "/")
            elif pat.Contains '*' then
                // Minimal glob: replace ** with .* and * with [^/]* for basic matching
                // Since we can't use Regex (AOT), fall back to simple prefix/suffix
                let norm = pat.Replace("**/", "").Replace("**", "")
                if norm = "" then true
                elif norm.StartsWith '*' then rel.EndsWith(norm.TrimStart '*')
                elif norm.EndsWith '*' then rel.StartsWith(norm.TrimEnd '*')
                else rel.Contains norm
            else rel = pat || rel.StartsWith(pat + "/"))

    let strings = pick cfg.Ui.Locale
    let mutable savedUi = cfg.Ui   // mutable copy for persisting runtime UI toggles
    let mutable verbosityPrefix : string option = None
    let mutable turnNumber = 0
    let mutable sessionNotes : (int * string) list = []  // (turnNumber, text)
    try
        while not cancelSrc.QuitRequested do
            let doAnnotate (rating: Fugue.Core.Annotation.Rating) =
                let idx = max 1 turnNumber
                let ann : Fugue.Core.Annotation.TurnAnnotation =
                    { TurnIndex   = idx
                      Rating      = Some rating
                      Note        = None
                      AnnotatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
                Fugue.Core.Annotation.save cwd ann
                StatusBar.recordAnnotation rating
                StatusBar.refresh ()
                let sym = match rating with Fugue.Core.Annotation.Up -> "↑" | Fugue.Core.Annotation.Down -> "↓"
                AnsiConsole.MarkupLine(sprintf "[dim]%s turn %d[/]" sym idx)
            let callbacks : ReadLine.ReadLineCallbacks =
                { OnClearScreen  = StatusBar.refresh
                  OnAnnotateUp   = fun () -> doAnnotate Fugue.Core.Annotation.Up
                  OnAnnotateDown = fun () -> doAnnotate Fugue.Core.Annotation.Down }
            // Drain file-watch triggers from background FileSystemWatcher
            let watchTriggers = FileWatcher.drain ()
            for wcmd in watchTriggers do
                AnsiConsole.Write(Markup(sprintf "[dim]⟳ watch → %s[/]" (Markup.Escape wcmd)))
                AnsiConsole.WriteLine()
                turnNumber <- turnNumber + 1
                AnsiConsole.Write(Render.userMessage cfg.Ui (sprintf "[watch] %s" wcmd) turnNumber)
                AnsiConsole.WriteLine()
                prelude cfg
                do! streamAndRender agent session wcmd cfg cancelSrc zenMode
                coda cfg
                StatusBar.refresh ()
            let modelShort =
                let m = match cfg.Provider with
                        | Fugue.Core.Config.Anthropic(_, m) | Fugue.Core.Config.OpenAI(_, m) | Fugue.Core.Config.Ollama(_, m) -> m
                let i = m.LastIndexOf '/'
                if i >= 0 && i + 1 < m.Length then m.Substring(i + 1) else m
            let! lineOpt =
                match Macros.dequeueStep() with
                | Some step ->
                    AnsiConsole.Write(Markup(sprintf "[dim]▶ %s[/]" (Markup.Escape step)))
                    AnsiConsole.WriteLine()
                    System.Threading.Tasks.Task.FromResult(Some step)
                | None ->
                    ReadLine.readAsync (Render.prompt cfg.Ui modelShort) strings callbacks (Render.isColorEnabled ()) cancelSrc.Token
            // Colon command expansion: :q → /exit, :n → /new, :h → /help, :s → /scratch send
            let colonExpand (s: string) =
                if not (s.StartsWith ':') || s.Length < 2 then s
                else
                    match s.Substring(1).Trim() with
                    | "q" | "Q" -> "/exit"
                    | "n"       -> "/new"
                    | "h" | "?" -> "/help"
                    | "s"       -> "/scratch send"
                    | rest when rest.StartsWith "e " -> rest.Substring(2).Trim() |> sprintf "@%s"
                    | _ -> s
            // Alias expansion: /aliasname → stored expansion
            let aliasExpand (s: string) =
                if not (s.StartsWith '/') then s
                else
                    let name = s.TrimStart('/').Split(' ').[0]
                    match aliasStore |> Map.tryFind name with
                    | Some expansion -> expansion
                    | None -> s
            let processedLine = lineOpt |> Option.map (colonExpand >> aliasExpand)
            // Record step if macro recording is active (but not /macro commands themselves)
            match processedLine with
            | Some s when Macros.isRecording() && not (s.StartsWith "/macro") ->
                Macros.recordStep s
            | _ -> ()
            match processedLine with
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
                                  "/document [path]", strings.CmdDocumentDesc
                                  "/activity",        strings.CmdActivityDesc
                                  "/alias <n>=<exp>", strings.CmdAliasDesc
                                  "/scratch <text>",  strings.CmdScratchDesc
                                  "/squash <N>",      strings.CmdSquashDesc
                                  "/short",           strings.CmdShortDesc
                                  "/long",            strings.CmdLongDesc
                                  "/zen",             strings.CmdZenDesc
                                  "/snippet …",       strings.CmdSnippetDesc
                                  "/bookmark <name>", strings.CmdBookmarkDesc
                                  "/bookmarks",       strings.CmdBookmarksDesc
                                  "/clear-history",   strings.CmdClearHistoryDesc
                                  "/tools",           strings.CmdToolsDesc
                                  "/todo",            strings.CmdTodoDesc
                                  "/note <text>",     strings.CmdNoteDesc
                                  "/notes",           strings.CmdNotesDesc
                                  "/undo",            strings.CmdUndoDesc
                                  "/find <query>",    strings.CmdFindDesc
                                  "/summarize <p>",   strings.CmdSummarizeDesc
                                  "/new",             strings.CmdNewDesc
                                  "/diff",           strings.CmdDiffDesc
                                  "/init",           strings.CmdInitDesc
                                  "/git-log",        strings.CmdGitLogDesc
                                  "/issue <N>",      strings.CmdIssueDesc
                                  "/model suggest",  strings.CmdModelDesc
                                  "/onboard",        strings.CmdOnboardDesc
                                  "/watch <p> <cmd>", strings.CmdWatchDesc
                                  "/macro …",        strings.CmdMacroDesc
                                  "/bench [n]",      strings.CmdBenchDesc
                                  "/compat",         strings.CmdCompatDesc
                                  "/history [n]",    strings.CmdHistoryDesc
                                  "/templates",      "list available session templates"
                                  "/rate up|down [n]", strings.CmdRateDesc
                                  "/annotate [n] [up|down] <note>", strings.CmdAnnotateDesc
                                  "/theme …",        strings.CmdThemeDesc
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
                            do! streamAndRender agent session prompt cfg cancelSrc zenMode
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
                                do! streamAndRender agent session prompt cfg cancelSrc zenMode
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
            | Some s when s = "/zen" ->
                zenMode <- not zenMode
                if zenMode then
                    StatusBar.stop ()
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.ZenOn + "[/]"))
                else
                    StatusBar.start cwd cfg
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.ZenOff + "[/]"))
                AnsiConsole.WriteLine()
                if not zenMode then StatusBar.refresh ()
            | Some s when s.StartsWith "/snippet" ->
                let parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                let sub = if parts.Length > 1 then parts.[1] else ""
                match sub with
                | "list" ->
                    if snippetStore.IsEmpty then
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.SnippetNone + "[/]"))
                    else
                        for KeyValue(name, (lang, code, src)) in snippetStore do
                            let preview = code.Split('\n').[0].[..60].Trim()
                            AnsiConsole.Write(Markup(sprintf "  [cyan]%s[/] [dim](%s · %s)[/]  %s" (Markup.Escape name) (Markup.Escape lang) (Markup.Escape src) (Markup.Escape preview)))
                            AnsiConsole.WriteLine()
                    AnsiConsole.WriteLine()
                    StatusBar.refresh ()
                | "save" when parts.Length >= 4 ->
                    let name = parts.[2]
                    let fileRef = parts.[3]
                    // parse file:start-end
                    let colonIdx = fileRef.LastIndexOf ':'
                    if colonIdx < 1 then
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.SnippetUsage + "[/]"))
                        AnsiConsole.WriteLine()
                    else
                        let filePart = fileRef.[..colonIdx-1]
                        let rangePart = fileRef.[colonIdx+1..]
                        let dashIdx = rangePart.IndexOf '-'
                        match (if dashIdx > 0 then System.Int32.TryParse(rangePart.[..dashIdx-1]), System.Int32.TryParse(rangePart.[dashIdx+1..]) else (false, 0), (false, 0)) with
                        | (true, startL), (true, endL) ->
                            let fullPath = Fugue.Tools.PathSafety.resolve cwd filePart
                            if not (System.IO.File.Exists fullPath) then
                                AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(strings.AtFileNotFound, filePart)) + "[/]"))
                            else
                                let lines = System.IO.File.ReadAllLines fullPath
                                let s1 = max 1 startL
                                let e1 = min lines.Length endL
                                let code = lines.[s1-1..e1-1] |> String.concat "\n"
                                let ext = (System.IO.Path.GetExtension filePart |> Option.ofObj |> Option.defaultValue "").TrimStart('.')
                                let lang = if ext = "" then "text" else ext
                                snippetStore <- snippetStore |> Map.add name (lang, code, fileRef)
                                AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(strings.SnippetSaved, name)) + "[/]"))
                            AnsiConsole.WriteLine()
                        | _ ->
                            AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.SnippetUsage + "[/]"))
                            AnsiConsole.WriteLine()
                    StatusBar.refresh ()
                | "inject" when parts.Length >= 3 ->
                    let query = parts.[2..] |> String.concat " "
                    let found =
                        snippetStore |> Map.tryFind query
                        |> Option.orElseWith (fun () ->
                            snippetStore |> Map.tryFindKey (fun k _ -> k.Contains(query, StringComparison.OrdinalIgnoreCase))
                            |> Option.map (fun k -> snippetStore.[k]))
                    match found with
                    | None ->
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(strings.SnippetNotFound, query)) + "[/]"))
                        AnsiConsole.WriteLine()
                    | Some(lang, code, _) ->
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(strings.SnippetInjected, query)) + "[/]"))
                        AnsiConsole.WriteLine()
                        let block = sprintf "```%s\n%s\n```" lang code
                        do! streamAndRender agent session block cfg cancelSrc zenMode
                    StatusBar.refresh ()
                | "remove" when parts.Length >= 3 ->
                    let name = parts.[2..] |> String.concat " "
                    if snippetStore.ContainsKey name then
                        snippetStore <- snippetStore |> Map.remove name
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(strings.SnippetRemoved, name)) + "[/]"))
                    else
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(strings.SnippetNotFound, name)) + "[/]"))
                    AnsiConsole.WriteLine()
                    StatusBar.refresh ()
                | _ ->
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.SnippetUsage + "[/]"))
                    AnsiConsole.WriteLine()
                    StatusBar.refresh ()
            | Some s when s = "/bookmarks" || (s.StartsWith "/bookmark " && s.Contains " remove ") || s.StartsWith "/bookmark" ->
                let parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                let isBookmarks = s = "/bookmarks"
                let isRemove = parts.Length >= 3 && parts.[1] = "remove"
                if isBookmarks then
                    if bookmarks.IsEmpty then
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.BookmarkNone + "[/]"))
                        AnsiConsole.WriteLine()
                    else
                        for (i, (name, file, s1, e1)) in bookmarks |> List.indexed do
                            AnsiConsole.Write(Markup(sprintf "  [cyan]%d.[/] [bold]%s[/] [dim]%s:%d-%d[/]" (i+1) (Markup.Escape name) (Markup.Escape file) s1 e1))
                            AnsiConsole.WriteLine()
                    StatusBar.refresh ()
                elif isRemove then
                    let name = parts.[2..] |> String.concat " "
                    let before = bookmarks.Length
                    bookmarks <- bookmarks |> List.filter (fun (n, _, _, _) -> n <> name)
                    if bookmarks.Length < before then
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(strings.BookmarkRemoved, name)) + "[/]"))
                    else
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(strings.BookmarkNotFound, name)) + "[/]"))
                    AnsiConsole.WriteLine()
                    StatusBar.refresh ()
                elif parts.Length >= 2 then
                    let name = parts.[1]
                    // Check if a file:range is provided as second arg
                    let fileRef = if parts.Length >= 3 then Some parts.[2] else None
                    let resolved =
                        match fileRef with
                        | Some ref ->
                            let colonIdx = ref.LastIndexOf ':'
                            if colonIdx < 1 then None
                            else
                                let filePart = ref.[..colonIdx-1]
                                let rangePart = ref.[colonIdx+1..]
                                let dashIdx = rangePart.IndexOf '-'
                                if dashIdx < 1 then None
                                else
                                    match System.Int32.TryParse(rangePart.[..dashIdx-1]), System.Int32.TryParse(rangePart.[dashIdx+1..]) with
                                    | (true, s1), (true, e1) ->
                                        let full = Fugue.Tools.PathSafety.resolve cwd filePart
                                        if System.IO.File.Exists full then Some(full, s1, e1)
                                        else None
                                    | _ -> None
                        | None ->
                            // Grep for the symbol in the workspace
                            let grepArgs = [| "--include=*.fs"; "--include=*.cs"; "--include=*.ts"; "-rn"; sprintf "\\b%s\\b" name; cwd |]
                            match runCmd "grep" grepArgs cwd with
                            | Some output ->
                                let firstLine = output.Split('\n') |> Array.tryHead |> Option.defaultValue ""
                                let colonIdx = firstLine.IndexOf ':'
                                if colonIdx > 0 then
                                    let filePart = firstLine.[..colonIdx-1]
                                    match System.Int32.TryParse(firstLine.[colonIdx+1..firstLine.IndexOf(':', colonIdx+1) - 1]) with
                                    | true, lineN -> if System.IO.File.Exists filePart then Some(filePart, max 1 (lineN-2), lineN+5) else None
                                    | _ -> None
                                else None
                            | None -> None
                    match resolved with
                    | None ->
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.BookmarkUsage + "[/]"))
                    | Some(fullPath, s1, e1) ->
                        bookmarks <- bookmarks @ [(name, fullPath, s1, e1)]
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(strings.BookmarkSaved, name)) + "[/]"))
                    AnsiConsole.WriteLine()
                    StatusBar.refresh ()
                else
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.BookmarkUsage + "[/]"))
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
            | Some s when s = "/compat" ->
                let env (k: string) = System.Environment.GetEnvironmentVariable k |> Option.ofObj |> Option.defaultValue ""
                let termProg   = env "TERM_PROGRAM"
                let colorTerm  = env "COLORTERM"
                let term       = env "TERM"
                let lang       = env "LANG"
                let lcAll      = env "LC_ALL"
                let trueColor  = colorTerm = "truecolor" || colorTerm = "24bit"
                let colors256  = term.Contains "256color"
                let utf8       = lang.Contains "UTF-8" || lcAll.Contains "UTF-8"
                let emoji      = Fugue.Core.EmojiMap.terminalSupportsEmoji ()
                let kitty      = termProg.ToLowerInvariant().Contains "kitty" || term = "xterm-kitty"
                let iterm2     = termProg.Contains "iTerm"
                let wezterm    = termProg.ToLowerInvariant().Contains "wezterm"
                let osc8       = kitty || iterm2 || wezterm
                let ansiColor  = Render.isColorEnabled ()
                let termW      = Console.WindowWidth
                let inDocker   =
                    System.IO.File.Exists "/.dockerenv"
                    || (try
                            let cg = System.IO.File.ReadAllText "/proc/1/cgroup"
                            cg.Contains "docker" || cg.Contains "kubepods" || cg.Contains "containerd"
                        with _ -> false)
                let check ok   = if ok then "[green]✓[/]" else "[red]✗[/]"
                if Render.isColorEnabled () then
                    let tbl = Table()
                    tbl.Border <- TableBorder.Rounded
                    tbl.AddColumn(TableColumn("[bold]Feature[/]").LeftAligned()) |> ignore
                    tbl.AddColumn(TableColumn("[bold]Status[/]").Centered())    |> ignore
                    tbl.AddColumn(TableColumn("[dim]Source[/]").LeftAligned())  |> ignore
                    let row feat ok src =
                        tbl.AddRow([| Markup.Escape feat; check ok; sprintf "[dim]%s[/]" (Markup.Escape src) |]) |> ignore
                    row "ANSI color"        ansiColor  "--color flag"
                    row "True color (24-bit)" trueColor (sprintf "COLORTERM=%s" colorTerm)
                    row "256 colors"        colors256  (sprintf "TERM=%s" term)
                    row "UTF-8"            utf8       (sprintf "LANG=%s" (if lang <> "" then lang else lcAll))
                    row "Emoji"            emoji      (sprintf "TERM_PROGRAM=%s" termProg)
                    row "Kitty protocol"   kitty      (sprintf "TERM_PROGRAM=%s" termProg)
                    row "OSC 8 hyperlinks" osc8       (if kitty then "kitty" elif iterm2 then "iTerm2" elif wezterm then "WezTerm" else "—")
                    row (sprintf "Terminal width = %d" termW) true "Console.WindowWidth"
                    row "Docker / container" inDocker   (if inDocker then "/.dockerenv or cgroup" else "not detected")
                    AnsiConsole.Write tbl
                else
                    let yn ok = if ok then "yes" else "no"
                    Console.Out.WriteLine(sprintf "ANSI color:      %s" (yn ansiColor))
                    Console.Out.WriteLine(sprintf "True color:      %s  COLORTERM=%s" (yn trueColor) colorTerm)
                    Console.Out.WriteLine(sprintf "256 colors:      %s  TERM=%s" (yn colors256) term)
                    Console.Out.WriteLine(sprintf "UTF-8:           %s  LANG=%s" (yn utf8) lang)
                    Console.Out.WriteLine(sprintf "Emoji:           %s" (yn emoji))
                    Console.Out.WriteLine(sprintf "Kitty protocol:  %s" (yn kitty))
                    Console.Out.WriteLine(sprintf "OSC 8:           %s" (yn osc8))
                    Console.Out.WriteLine(sprintf "Terminal width:  %d" termW)
                    Console.Out.WriteLine(sprintf "Docker:          %s" (yn inDocker))
                StatusBar.refresh ()
            | Some s when s = "/bench" || s.StartsWith "/bench " ->
                let nArg = if s = "/bench" then 3 else
                               let rest = s.Substring(6).Trim()
                               match System.Int32.TryParse rest with
                               | true, n when n >= 1 -> min n 10
                               | _ -> 3
                let probePrompt = "Reply with exactly one word: ping"
                let ttfts = System.Collections.Generic.List<int64>()
                let totals = System.Collections.Generic.List<int64>()
                let mutable benchAborted = false
                for attempt in 1..nArg do
                    if not benchAborted then
                        if Render.isColorEnabled () then
                            AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape (String.Format(strings.BenchRunning, string attempt, string nArg))))
                        else
                            Console.Out.WriteLine(String.Format(strings.BenchRunning, string attempt, string nArg))
                        try
                            use benchCts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds 30.0)
                            let! probeSession = agent.CreateSessionAsync(benchCts.Token)
                            let sw = System.Diagnostics.Stopwatch.StartNew()
                            let mutable ttft = -1L
                            let stream = Conversation.run agent probeSession probePrompt benchCts.Token
                            let en = stream.GetAsyncEnumerator(benchCts.Token)
                            let mutable running = true
                            while running do
                                let! stepped = en.MoveNextAsync().AsTask()
                                if stepped then
                                    match en.Current with
                                    | Conversation.TextChunk _ ->
                                        if ttft < 0L then ttft <- sw.ElapsedMilliseconds
                                    | Conversation.Finished -> running <- false
                                    | Conversation.Failed _ -> running <- false
                                    | _ -> ()
                                else running <- false
                            let total = sw.ElapsedMilliseconds
                            if ttft >= 0L then
                                ttfts.Add ttft
                                totals.Add total
                        with _ ->
                            benchAborted <- true
                if benchAborted then
                    AnsiConsole.Write(Render.errorLine strings strings.BenchAborted)
                    AnsiConsole.WriteLine()
                elif ttfts.Count > 0 then
                    let pct (data: System.Collections.Generic.List<int64>) (p: int) =
                        let sorted = data |> Seq.sort |> Seq.toArray
                        sorted.[int (float (sorted.Length - 1) * float p / 100.0)]
                    if Render.isColorEnabled () then
                        let tbl = Table()
                        tbl.Border <- TableBorder.Rounded
                        tbl.AddColumn(TableColumn("[bold]Metric[/]").LeftAligned()) |> ignore
                        tbl.AddColumn(TableColumn("[bold]TTFT[/]").RightAligned())  |> ignore
                        tbl.AddColumn(TableColumn("[bold]Total[/]").RightAligned()) |> ignore
                        let addRow label ttftVal totalVal =
                            tbl.AddRow([| label; sprintf "%dms" ttftVal; sprintf "%dms" totalVal |]) |> ignore
                        addRow "min"  (pct ttfts 0)   (pct totals 0)
                        addRow "p50"  (pct ttfts 50)  (pct totals 50)
                        addRow "p90"  (pct ttfts 90)  (pct totals 90)
                        addRow "p99"  (pct ttfts 99)  (pct totals 99)
                        addRow "max"  (pct ttfts 100) (pct totals 100)
                        AnsiConsole.Write tbl
                    else
                        Console.Out.WriteLine(sprintf "%-6s  %-8s  %-8s" "metric" "TTFT" "Total")
                        let pr label p = Console.Out.WriteLine(sprintf "%-6s  %-6dms  %-6dms" label (pct ttfts p) (pct totals p))
                        pr "min" 0; pr "p50" 50; pr "p90" 90; pr "p99" 99; pr "max" 100
                StatusBar.refresh ()
            | Some s when s = "/history" || s.StartsWith "/history " ->
                let nArg = if s = "/history" then 5 else
                               let rest = s.Substring(8).Trim()
                               match System.Int32.TryParse rest with
                               | true, n when n > 0 -> min n 20
                               | _ -> 5
                let records = Fugue.Core.SessionSummary.loadHistory cwd nArg
                if records.IsEmpty then
                    AnsiConsole.Write(Markup("[dim]no session history yet[/]"))
                    AnsiConsole.WriteLine()
                else
                    for (idx, (ts, summary)) in records |> List.mapi (fun i r -> i, r) do
                        let ago =
                            let diff = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds ts
                            if diff.TotalDays >= 1.0 then sprintf "%dd ago" (int diff.TotalDays)
                            elif diff.TotalHours >= 1.0 then sprintf "%dh ago" (int diff.TotalHours)
                            else sprintf "%dm ago" (max 1 (int diff.TotalMinutes))
                        if Render.isColorEnabled () then
                            AnsiConsole.MarkupLine(sprintf "[dim]#%d (%s)[/]" (idx + 1) ago)
                            AnsiConsole.Write(MarkdownRender.toRenderable summary)
                            AnsiConsole.WriteLine()
                        else
                            Console.Out.WriteLine(sprintf "#%d (%s)" (idx + 1) ago)
                            Console.Out.WriteLine summary
                            Console.Out.WriteLine()
                StatusBar.refresh ()
            | Some "/templates" ->
                let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                let dir = System.IO.Path.Combine(home, ".fugue", "templates")
                if not (System.IO.Directory.Exists dir) then
                    AnsiConsole.MarkupLine("[dim]No templates found. Create ~/.fugue/templates/<name>.md to add one.[/]")
                else
                    let files = System.IO.Directory.GetFiles(dir, "*.md")
                    if files.Length = 0 then
                        AnsiConsole.MarkupLine("[dim]No templates found. Create ~/.fugue/templates/<name>.md to add one.[/]")
                    else
                        AnsiConsole.MarkupLine("[dim]Available templates (use fugue --template <name>):[/]")
                        for f in files |> Array.sort do
                            let name = System.IO.Path.GetFileNameWithoutExtension f |> Option.ofObj |> Option.defaultValue ""
                            if name <> "" then
                                AnsiConsole.MarkupLine(sprintf "  [cyan]%s[/]" (Markup.Escape name))
                StatusBar.refresh ()
            | Some s when s = "/rate" || s.StartsWith "/rate " ->
                let parts = (if s = "/rate" then "" else s.Substring(6)).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                let ratingArg = if parts.Length > 0 then parts.[0].ToLowerInvariant() else ""
                let turnArg   = if parts.Length > 1 then parts.[1] else ""
                match ratingArg with
                | "up" | "down" ->
                    let rating = if ratingArg = "up" then Fugue.Core.Annotation.Up else Fugue.Core.Annotation.Down
                    let targetTurn =
                        match System.Int32.TryParse turnArg with
                        | true, n when n > 0 -> n
                        | _ -> turnNumber
                    let ann : Fugue.Core.Annotation.TurnAnnotation =
                        { TurnIndex   = targetTurn
                          Rating      = Some rating
                          Note        = None
                          AnnotatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
                    Fugue.Core.Annotation.save cwd ann
                    StatusBar.recordAnnotation rating
                    let msg = String.Format(strings.RateSaved, string targetTurn, ratingArg)
                    if Render.isColorEnabled () then
                        let color = if ratingArg = "up" then "green" else "red"
                        AnsiConsole.MarkupLine(sprintf "[%s]%s[/]" color (Markup.Escape msg))
                    else Console.Out.WriteLine msg
                | _ ->
                    if Render.isColorEnabled () then
                        AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape strings.RateUsage))
                    else Console.Out.WriteLine strings.RateUsage
                StatusBar.refresh ()
            | Some s when s = "/annotate" || s.StartsWith "/annotate " ->
                // /annotate [N] [up|down] <note text>
                let arg = (if s = "/annotate" then "" else s.Substring(10)).Trim()
                let tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                let mutable idx = 0
                let mutable targetTurn = turnNumber
                let mutable rating : Fugue.Core.Annotation.Rating option = None
                // Try to parse optional leading turn number
                if idx < tokens.Length then
                    match System.Int32.TryParse tokens.[idx] with
                    | true, n when n > 0 -> targetTurn <- n; idx <- idx + 1
                    | _ -> ()
                // Try to parse optional up|down
                if idx < tokens.Length then
                    match tokens.[idx].ToLowerInvariant() with
                    | "up"   -> rating <- Some Fugue.Core.Annotation.Up;   idx <- idx + 1
                    | "down" -> rating <- Some Fugue.Core.Annotation.Down; idx <- idx + 1
                    | _ -> ()
                // Rest is the note
                let note =
                    if idx < tokens.Length then Some (String.concat " " tokens.[idx..])
                    else None
                if rating = None && note = None then
                    AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape strings.AnnotateUsage))
                else
                    let ann : Fugue.Core.Annotation.TurnAnnotation =
                        { TurnIndex   = targetTurn
                          Rating      = rating
                          Note        = note
                          AnnotatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
                    Fugue.Core.Annotation.save cwd ann
                    match rating with
                    | Some r -> StatusBar.recordAnnotation r
                    | None   -> ()
                    let ratingStr = rating |> Option.map (fun r -> match r with Fugue.Core.Annotation.Up -> " ↑" | Fugue.Core.Annotation.Down -> " ↓") |> Option.defaultValue ""
                    let noteStr   = note |> Option.map (fun n -> sprintf " \"%s\"" n) |> Option.defaultValue ""
                    let msg = sprintf "turn %d%s%s" targetTurn ratingStr noteStr
                    AnsiConsole.MarkupLine(sprintf "[dim]annotated: %s[/]" (Markup.Escape msg))
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
                                do! streamAndRender agent session issueContext cfg cancelSrc zenMode
                    with ex ->
                        AnsiConsole.Write(Render.errorLine strings ex.Message)
                        AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/model suggest" ->
                AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.AskingModelRecommendation + "[/]"))
                AnsiConsole.WriteLine()
                do! streamAndRender agent session strings.ModelSuggestPrompt cfg cancelSrc zenMode
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
                do! streamAndRender agent session onboardPrompt cfg cancelSrc zenMode
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
                        AnsiConsole.Write(Render.userMessage cfg.Ui (sprintf "Reviewing PR #%d…" prNum) 0)
                        AnsiConsole.WriteLine()
                        do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/ask " ->
                let question = s.Substring(5).Trim()
                if String.IsNullOrWhiteSpace question then
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.AskUsage + "[/]"))
                    AnsiConsole.WriteLine()
                else
                    let augmented = sprintf "[System: answer from your training knowledge only — do not invoke any tools, do not read files, do not run commands]\n\nUser: %s" question
                    AnsiConsole.Write(Render.userMessage cfg.Ui question 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session augmented cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s = "/ask" ->
                AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.AskUsage + "[/]"))
                AnsiConsole.WriteLine()
            | Some s when s = "/diff" || s = "/diff --staged" ||
                          (s.StartsWith("/diff ") && s <> "/diff --staged") ->
                let rest = if s.Length > 6 then s.Substring(6).Trim() else ""
                // Determine mode: git diff, git diff --staged, or git diff --no-index <a> <b>
                let parts =
                    if rest = "--staged" || rest = "" then [||]
                    else rest.Split(' ') |> Array.filter (fun p -> p <> "")
                let psi = System.Diagnostics.ProcessStartInfo()
                psi.FileName <- "git"
                psi.ArgumentList.Add "diff"
                match parts with
                | [| a; b |] ->
                    let resolve (p: string) = if System.IO.Path.IsPathRooted p then p else System.IO.Path.Combine(cwd, p)
                    psi.ArgumentList.Add "--no-index"
                    psi.ArgumentList.Add (resolve a)
                    psi.ArgumentList.Add (resolve b)
                | _ ->
                    if rest = "--staged" then psi.ArgumentList.Add "--staged"
                psi.UseShellExecute <- false
                psi.RedirectStandardOutput <- true
                psi.RedirectStandardError <- true
                psi.WorkingDirectory <- cwd
                use proc = new System.Diagnostics.Process()
                proc.StartInfo <- psi
                try
                    proc.Start() |> ignore
                    let output = proc.StandardOutput.ReadToEnd()
                    let errOut = proc.StandardError.ReadToEnd().Trim()
                    proc.WaitForExit()
                    // git diff --no-index exits 1 when files differ — that is a success case
                    let isTwoFile = match parts with [| _; _ |] -> true | _ -> false
                    if proc.ExitCode <> 0 && not (isTwoFile && proc.ExitCode = 1) then
                        let msg = if errOut <> "" then errOut else sprintf "git diff exited %d" proc.ExitCode
                        AnsiConsole.Write(Render.errorLine strings msg)
                        AnsiConsole.WriteLine()
                    elif System.String.IsNullOrWhiteSpace output then
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
                    AnsiConsole.Write(Render.userMessage cfg.Ui "/init" 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session initPrompt cfg cancelSrc zenMode
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
                turnNumber <- 0
                sessionNotes <- []
                lastFile <- None
                zenMode <- false
                StatusBar.resetWords ()
                StatusBar.resetAnnotations ()
                ClipRing.clear ()
                snippetStore <- Map.empty
                bookmarks <- []
                activityStore <- Map.empty
                aliasStore <- Map.empty
                scratchLines <- []
                FileWatcher.clear ()
                Macros.clear ()
                Fugue.Core.Checkpoint.clear ()
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
                AnsiConsole.Write(Render.userMessage cfg.Ui "/summary" 0)
                AnsiConsole.WriteLine()
                do! streamAndRender agent session summaryPrompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/document" ->
                let arg = s.Substring("/document".Length).Trim()
                let docPath =
                    if String.IsNullOrWhiteSpace arg then "docs/session-notes.md"
                    else
                        let clean = arg.TrimStart('/')
                        if clean.StartsWith "docs/" then clean else "docs/" + clean
                let docPrompt = System.String.Format(strings.DocumentPrompt, docPath)
                AnsiConsole.Write(Render.userMessage cfg.Ui (sprintf "/document %s" docPath) 0)
                AnsiConsole.WriteLine()
                do! streamAndRender agent session docPrompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s = "/activity" ->
                if activityStore.IsEmpty then
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.ActivityNone + "[/]"))
                    AnsiConsole.WriteLine()
                else
                    let maxCount = activityStore |> Map.toSeq |> Seq.map snd |> Seq.max
                    let sorted = activityStore |> Map.toSeq |> Seq.sortByDescending snd |> Seq.truncate 20
                    for (path, count) in sorted do
                        let heat =
                            if count >= 10 then "████"
                            elif count >= 6  then "███░"
                            elif count >= 3  then "██░░"
                            else             "█░░░"
                        let barLen = max 1 (count * 20 / maxCount)
                        let bar = String.replicate barLen "█"
                        let label = System.IO.Path.GetFileName(path) |> Option.ofObj |> Option.defaultValue path
                        if Render.isColorEnabled () then
                            AnsiConsole.MarkupLine(sprintf "  [cyan]%s[/] [dim]%s[/] [grey]%dx[/]" heat (Markup.Escape label) count)
                        else
                            printfn "  %s %s %dx" bar label count
                    AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/macro" ->
                let rest = s.Substring("/macro".Length).Trim()
                let sub  = if rest.Contains " " then rest.Substring(0, rest.IndexOf ' ') else rest
                let arg  = if rest.Contains " " then rest.Substring(rest.IndexOf ' ' + 1).Trim() else ""
                match sub with
                | "record" when not (String.IsNullOrWhiteSpace arg) ->
                    if Macros.isRecording() then Macros.stopRecording() |> ignore
                    Macros.startRecording arg
                    AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape (String.Format(strings.MacroRecording, arg))))
                | "stop" ->
                    match Macros.stopRecording() with
                    | Some name ->
                        let cnt = Macros.list() |> List.tryFind (fun (n, _) -> n = name) |> Option.map snd |> Option.defaultValue 0
                        AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape (String.Format(strings.MacroSaved, name, cnt))))
                    | None ->
                        AnsiConsole.MarkupLine("[dim]no recording in progress[/]")
                | "play" when not (String.IsNullOrWhiteSpace arg) ->
                    match Macros.list() |> List.tryFind (fun (n, _) -> n = arg) with
                    | Some (_, cnt) ->
                        if Macros.enqueuePlay arg then
                            AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape (String.Format(strings.MacroPlaying, arg, cnt))))
                        else
                            AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape (String.Format(strings.MacroNotFound, arg))))
                    | None ->
                        AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape (String.Format(strings.MacroNotFound, arg))))
                | "list" | "" ->
                    let macros = Macros.list()
                    if macros.IsEmpty then
                        AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape strings.MacroNone))
                    else
                        for (name, cnt) in macros do
                            AnsiConsole.MarkupLine(sprintf "  [cyan]%s[/] [dim](%d steps)[/]" (Markup.Escape name) cnt)
                | "remove" when not (String.IsNullOrWhiteSpace arg) ->
                    if Macros.remove arg then
                        AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape (String.Format(strings.MacroRemoved, arg))))
                    else
                        AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape (String.Format(strings.MacroNotFound, arg))))
                | _ ->
                    AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape strings.MacroUsage))
                AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/theme" ->
                let sub = s.Substring("/theme".Length).Trim()
                match sub with
                | "bubbles" ->
                    Render.toggleBubbles ()
                    let on = Render.isBubblesMode ()
                    if on then
                        AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape strings.ThemeBubblesOn))
                    else
                        AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape strings.ThemeBubblesOff))
                    savedUi <- { savedUi with BubblesMode = on }
                    try Config.saveToFile { cfg with Ui = savedUi } with _ -> ()
                | "typewriter" ->
                    Render.toggleTypewriter ()
                    let on = Render.isTypewriterMode ()
                    if on then
                        AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape strings.ThemeTypewriterOn))
                    else
                        AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape strings.ThemeTypewriterOff))
                    savedUi <- { savedUi with TypewriterMode = on }
                    try Config.saveToFile { cfg with Ui = savedUi } with _ -> ()
                | _ ->
                    AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape strings.ThemeUsage))
                AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/alias" ->
                let parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                let sub = if parts.Length > 1 then parts.[1] else ""
                match sub with
                | "list" | "" when s = "/alias" || s = "/alias list" ->
                    if aliasStore.IsEmpty then
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.AliasNone + "[/]"))
                    else
                        for KeyValue(name, expansion) in aliasStore do
                            AnsiConsole.Write(Markup(sprintf "  [cyan]/%s[/] [dim]→ %s[/]" (Markup.Escape name) (Markup.Escape expansion)))
                            AnsiConsole.WriteLine()
                    AnsiConsole.WriteLine()
                | "remove" when parts.Length >= 3 ->
                    let name = parts.[2]
                    aliasStore <- aliasStore |> Map.remove name
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(strings.AliasRemoved, name)) + "[/]"))
                    AnsiConsole.WriteLine()
                | _ ->
                    // Parse: /alias <name> = <expansion>
                    let rest = s.Substring("/alias ".Length)
                    let eqIdx = rest.IndexOf '='
                    if eqIdx < 1 then
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.AliasUsage + "[/]"))
                    else
                        let name = rest.[..eqIdx-1].Trim().TrimStart('/')
                        let expansion = rest.[eqIdx+1..].Trim()
                        if not (String.IsNullOrWhiteSpace name) && not (String.IsNullOrWhiteSpace expansion) then
                            aliasStore <- aliasStore |> Map.add name expansion
                            AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(strings.AliasSet, name, expansion)) + "[/]"))
                        else
                            AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.AliasUsage + "[/]"))
                    AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/scratch" ->
                let sub = s.Substring("/scratch".Length).Trim()
                match sub with
                | "send" when not scratchLines.IsEmpty ->
                    let text = scratchLines |> String.concat "\n"
                    scratchLines <- []
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.ScratchSent + "[/]"))
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session text cfg cancelSrc zenMode
                | "send" ->
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.ScratchEmpty + "[/]"))
                    AnsiConsole.WriteLine()
                | "clear" ->
                    scratchLines <- []
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.ScratchCleared + "[/]"))
                    AnsiConsole.WriteLine()
                | "show" ->
                    if scratchLines.IsEmpty then
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.ScratchEmpty + "[/]"))
                    else
                        AnsiConsole.Write(Markup("[bold]" + Markup.Escape strings.ScratchShowHeader + "[/]"))
                        AnsiConsole.WriteLine()
                        for line in scratchLines do
                            AnsiConsole.Write(Markup("[dim]  " + Markup.Escape line + "[/]"))
                            AnsiConsole.WriteLine()
                    AnsiConsole.WriteLine()
                | text when not (String.IsNullOrWhiteSpace text) ->
                    scratchLines <- scratchLines @ [text]
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(strings.ScratchAppended, scratchLines.Length)) + "[/]"))
                    AnsiConsole.WriteLine()
                | _ ->
                    // /scratch alone with no subcommand: show usage
                    AnsiConsole.Write(Markup("[dim]usage: /scratch <text> | /scratch send | /scratch show | /scratch clear[/]"))
                    AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/watches" ->
                let ws = FileWatcher.list ()
                if ws.IsEmpty then
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.WatchNone + "[/]"))
                    AnsiConsole.WriteLine()
                else
                    for (path, cmd) in ws do
                        AnsiConsole.Write(Markup(sprintf "  [cyan]%s[/] → [dim]%s[/]" (Markup.Escape path) (Markup.Escape cmd)))
                        AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/unwatch " ->
                let path = s.Substring("/unwatch ".Length).Trim()
                let absPath = System.IO.Path.GetFullPath(path)
                FileWatcher.remove absPath
                AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(strings.WatchRemoved, path)) + "[/]"))
                AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/watch " ->
                let rest = s.Substring("/watch ".Length).Trim()
                let spaceIdx = rest.IndexOf ' '
                if spaceIdx <= 0 then
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape strings.WatchUsage + "[/]"))
                    AnsiConsole.WriteLine()
                else
                    let path = rest.Substring(0, spaceIdx).Trim()
                    let cmd  = rest.Substring(spaceIdx + 1).Trim()
                    let absPath = System.IO.Path.GetFullPath(path)
                    if not (System.IO.File.Exists absPath) then
                        AnsiConsole.Write(Markup(sprintf "[dim]%s[/]" (Markup.Escape (System.String.Format(strings.AtFileNotFound, path)))))
                        AnsiConsole.WriteLine()
                    else
                        FileWatcher.add absPath cmd
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(strings.WatchAdded, path, cmd)) + "[/]"))
                        AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/note " ->
                let text = s.Substring("/note ".Length).Trim()
                if not (String.IsNullOrWhiteSpace text) then
                    sessionNotes <- sessionNotes @ [(turnNumber, text)]
                    AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape strings.NoteAdded))
                    AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/notes" ->
                if sessionNotes.IsEmpty then
                    AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape strings.NoteNone))
                else
                    for (turn, text) in sessionNotes do
                        if Render.isColorEnabled () then
                            AnsiConsole.MarkupLine(sprintf "[dim][[turn %d]][/] %s" turn (Markup.Escape text))
                        else
                            Console.Out.WriteLine(sprintf "[turn %d] %s" turn text)
                AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/undo" ->
                match Fugue.Core.Checkpoint.undo () with
                | Fugue.Core.Checkpoint.NothingToUndo ->
                    AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape strings.UndoNothingToUndo))
                | Fugue.Core.Checkpoint.Restored path ->
                    AnsiConsole.MarkupLine(sprintf "[green]✓[/] %s: [dim]%s[/]" (Markup.Escape strings.UndoRestored) (Markup.Escape path))
                | Fugue.Core.Checkpoint.Deleted path ->
                    AnsiConsole.MarkupLine(sprintf "[yellow]✓[/] %s: [dim]%s[/]" (Markup.Escape strings.UndoDeleted) (Markup.Escape path))
                AnsiConsole.WriteLine()
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
                    do! streamAndRender agent session summary cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/find " ->
                let query = s.Substring("/find ".Length).Trim()
                if String.IsNullOrWhiteSpace query then
                    AnsiConsole.Write(Markup("[dim]Usage: /find <query>[/]"))
                    AnsiConsole.WriteLine()
                else
                    // Two-pass: filename fuzzy match + content grep for query terms
                    let fuzzyScore (q: string) (candidate: string) =
                        let q = q.ToLowerInvariant()
                        let c = candidate.ToLowerInvariant()
                        let mutable qi = 0
                        let mutable score = 0
                        for ci in 0 .. c.Length - 1 do
                            if qi < q.Length && c.[ci] = q.[qi] then
                                score <- score + 1
                                qi <- qi + 1
                        if qi = q.Length then score * 3 else 0
                    let allFiles =
                        try
                            System.IO.Directory.EnumerateFiles(cwd, "*", SearchOption.AllDirectories)
                            |> Seq.filter (fun f ->
                                let rel = try System.IO.Path.GetRelativePath(cwd, f) with _ -> f
                                not (rel.Contains(string System.IO.Path.DirectorySeparatorChar + ".git"))
                                && not (rel.StartsWith ".git")
                                && not (isIgnored rel))
                            |> Seq.toList
                        with _ -> []
                    let results =
                        allFiles
                        |> List.choose (fun abs ->
                            let rel = try System.IO.Path.GetRelativePath(cwd, abs) with _ -> abs
                            let name = System.IO.Path.GetFileName abs |> Option.ofObj |> Option.defaultValue ""
                            let ns = fuzzyScore query name
                            let cs, snippet =
                                try
                                    let info = System.IO.FileInfo abs
                                    if info.Length < 512L * 1024L then
                                        let lines = System.IO.File.ReadAllLines abs
                                        let hit = lines |> Array.tryFind (fun l -> l.Contains(query, StringComparison.OrdinalIgnoreCase))
                                        let cnt = lines |> Array.sumBy (fun l -> if l.Contains(query, StringComparison.OrdinalIgnoreCase) then 1 else 0)
                                        cnt, hit
                                    else 0, None
                                with _ -> 0, None
                            let total = ns + cs
                            if total > 0 then Some (total, rel, snippet) else None)
                        |> List.sortByDescending (fun (score, _, _) -> score)
                        |> List.truncate 10
                    if results.IsEmpty then
                        AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape strings.FindNoResults))
                    else
                        for (_score, rel, snippet) in results do
                            let snipStr =
                                match snippet with
                                | Some s -> sprintf "  → %s" (s.Trim())
                                | None   -> ""
                            if Render.isColorEnabled () then
                                AnsiConsole.MarkupLine(sprintf "[cyan]%s[/]%s" (Markup.Escape rel) (Markup.Escape snipStr))
                            else
                                Console.Out.WriteLine(sprintf "%s%s" rel snipStr)
                        AnsiConsole.MarkupLine(sprintf "[dim]%s[/]" (Markup.Escape strings.FindInjectHint))
                    AnsiConsole.WriteLine()
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
                    AnsiConsole.Write(Render.userMessage cfg.Ui ("/summarize " + rawArg) 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session summarizePrompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some userInput ->
                if ReadLine.hasZeroWidth userInput then
                    AnsiConsole.Write(Markup("[dim yellow]" + Markup.Escape strings.ZeroWidthWarning + "[/]"))
                    AnsiConsole.WriteLine()
                let expandedInput = expandAtFiles cwd strings userInput
                let expandedInput = expandClipboard expandedInput strings
                let expandedInput = expandClipRing expandedInput
                StatusBar.recordWords (expandedInput.Split([|' '; '\n'; '\r'; '\t'|], StringSplitOptions.RemoveEmptyEntries).Length)
                turnNumber <- turnNumber + 1
                AnsiConsole.Write(Render.userMessage cfg.Ui userInput turnNumber)
                AnsiConsole.WriteLine()
                let effectiveInput =
                    match verbosityPrefix with
                    | Some prefix -> prefix + expandedInput
                    | None -> expandedInput
                let sw = System.Diagnostics.Stopwatch.StartNew()
                do! streamAndRender agent session effectiveInput cfg cancelSrc zenMode
                StatusBar.refresh ()
        // Generate and persist a 3-sentence session summary when the session had at least one turn.
        if turnNumber > 0 then
            try
                use summaryCts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds 20.0)
                let summaryPrompt =
                    "Summarise what was accomplished in this coding session in exactly 3 sentences. " +
                    "Be specific: mention file names, feature names, and any incomplete work. " +
                    "Output plain text only — no lists, no markdown, no headings."
                let summaryBuf = System.Text.StringBuilder()
                let summaryStream = Conversation.run agent session summaryPrompt summaryCts.Token
                let enum = summaryStream.GetAsyncEnumerator(summaryCts.Token)
                let mutable summaryRunning = true
                while summaryRunning do
                    let! stepped = enum.MoveNextAsync().AsTask()
                    if stepped then
                        match enum.Current with
                        | Conversation.TextChunk t -> summaryBuf.Append t |> ignore
                        | Conversation.Finished    -> summaryRunning <- false
                        | Conversation.Failed _    -> summaryRunning <- false
                        | _ -> ()
                    else summaryRunning <- false
                let summary = summaryBuf.ToString().Trim()
                if summary.Length > 0 then
                    Fugue.Core.SessionSummary.save cwd sessionStartedAt summary
            with _ -> ()
    finally
        Console.CancelKeyPress.RemoveHandler handler
        Console.Out.Write "\x1b[?2004l"   // disable bracketed paste
        Console.Out.Flush()
        coda cfg
        StatusBar.stop ()
}

[<RequiresUnreferencedCode("Calls Conversation.run which uses STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
[<RequiresDynamicCode("Calls Conversation.run which uses STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
let runHeadless (agent: AIAgent) (cfg: AppConfig) (_cwd: string) : Task<unit> = task {
    let input = Console.In.ReadToEnd().Trim()
    if not (String.IsNullOrWhiteSpace input) then
        let! session = agent.CreateSessionAsync(CancellationToken.None)
        let stream = Conversation.run agent session input CancellationToken.None
        let enumerator = stream.GetAsyncEnumerator(CancellationToken.None)
        let mutable hasNext = true
        while hasNext do
            let! step = enumerator.MoveNextAsync().AsTask()
            if step then
                match enumerator.Current with
                | Conversation.TextChunk t ->
                    Console.Out.Write t
                    Console.Out.Flush()
                | Conversation.ToolStarted(_, name, args) ->
                    Console.Error.WriteLine(sprintf "[tool: %s(%s)]" name args)
                | Conversation.ToolCompleted(id, output, isErr) ->
                    ignore id
                    if isErr then Console.Error.WriteLine(sprintf "[tool error: %s]" output)
                | Conversation.Finished -> ()
                | Conversation.Failed ex -> raise ex
            else hasNext <- false
        Console.Out.WriteLine()
}
