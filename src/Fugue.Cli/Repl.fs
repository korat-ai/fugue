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

// Pinned messages: prepended to each turn's effective input
let mutable private pinnedMessages : string list = []

// Session title (set via /rename)
let mutable private sessionTitle : string option = None

// Session-scoped env vars set via /env
let mutable private sessionEnvVars : Set<string> = Set.empty

// Per-session ID for error trend logging (reset on /new).
let mutable private currentSessionId = ""

// Last turn that had tool errors — used by /report-bug.
let mutable private lastFailedTurn : BugReport.TurnRecord option = None

// Whether the last AI response looked like a plan (for smart follow-up detection).
let mutable private lastResponseWasPlan = false

let internal generateUlid () : string =
    let alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
    let rng = System.Security.Cryptography.RandomNumberGenerator.Create()
    let ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    let sb = System.Text.StringBuilder(26)
    let mutable t = ts
    let tsChars = Array.zeroCreate 10
    for i in 9 .. -1 .. 0 do
        tsChars.[i] <- alphabet.[int (t % 32L)]
        t <- t / 32L
    for c in tsChars do sb.Append c |> ignore
    let randBytes = Array.zeroCreate 10
    rng.GetBytes randBytes
    let mutable bits = 0UL
    let mutable bitsAvail = 0
    for b in randBytes do
        bits <- (bits <<< 8) ||| uint64 b
        bitsAvail <- bitsAvail + 8
        while bitsAvail >= 5 do
            bitsAvail <- bitsAvail - 5
            let idx = int ((bits >>> bitsAvail) &&& 0x1FUL)
            sb.Append alphabet.[idx] |> ignore
    sb.ToString().[..25]

let internal generateNanoid () : string =
    let alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-"
    let rng = System.Security.Cryptography.RandomNumberGenerator.Create()
    let bytes = Array.zeroCreate 21
    rng.GetBytes bytes
    System.String(bytes |> Array.map (fun b -> alphabet.[int b &&& 63]))

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
    // @recent N: expand to N most recently modified files
    let input =
        let m = System.Text.RegularExpressions.Regex.Match(input, @"@recent\s+(\d+)")
        if not m.Success then input
        else
            let n = max 1 (min 20 (int m.Groups.[1].Value))
            let files =
                try
                    System.IO.Directory.EnumerateFiles(cwd, "*", System.IO.SearchOption.AllDirectories)
                    |> Seq.filter (fun f ->
                        let rel = try System.IO.Path.GetRelativePath(cwd, f) with _ -> f
                        Fugue.Tools.PathSafety.isUnder cwd f
                        && not (rel.Contains(string System.IO.Path.DirectorySeparatorChar + ".git"))
                        && not (rel.StartsWith ".git"))
                    |> Seq.sortByDescending (fun f -> (System.IO.FileInfo f).LastWriteTimeUtc)
                    |> Seq.truncate n
                    |> Seq.toList
                with _ -> []
            let injection =
                files
                |> List.map (fun f ->
                    let rel = try System.IO.Path.GetRelativePath(cwd, f) with _ -> f
                    let content =
                        try
                            let text = System.IO.File.ReadAllText f
                            if text.Length > 3000 then text.[..2999] + "\n[truncated]" else text
                        with _ -> "[unreadable]"
                    $"[contents of {rel}]:\n{content}")
                |> String.concat "\n\n"
            input.Substring(0, m.Index) + injection + input.Substring(m.Index + m.Length)
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
                AnsiConsole.MarkupLine($"[dim]{Markup.Escape (System.String.Format(strings.AtFileNotFound, pathPart))}[/]")
            else
                try
                    let content = System.IO.File.ReadAllText fullPath
                    let truncated = content.Length > 4000
                    let body = if truncated then content.[..3999] else content
                    let tooBig = if truncated then " " + strings.AtFileTooBig else ""
                    let header = $"[contents of {pathPart}]{tooBig}:\n"
                    let hint =
                        match tryFindTestFile cwd fullPath with
                        | Some testRel ->
                            let msg = System.String.Format(strings.TestFileHint, testRel, testRel)
                            $"\n\n{msg}"
                        | None -> ""
                    result <- result.[0 .. start - 1] + header + body + hint + result.[start + length ..]
                with _ ->
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape (System.String.Format(strings.AtFileNotFound, pathPart))}[/]")
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
            let token = $"@clip{n}"
            if result.Contains token then
                let replacement = ClipRing.get n |> Option.defaultValue $"[clip{n} empty]"
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
    let errorToolCalls = ResizeArray<string * string * string>()
    // Track last failed args per tool name to compute retry diffs.
    let failedArgs = Dictionary<string, string>()

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
                        // Show a retry diff when the same tool previously failed with different args.
                        match failedArgs.TryGetValue name with
                        | true, prevArgs when prevArgs <> args ->
                            let diff = ToolRetryDiff.diffArgs prevArgs args
                            if not diff.IsEmpty && not zen then
                                let formatted = ToolRetryDiff.formatDiff diff
                                if Render.isColorEnabled () then
                                    AnsiConsole.MarkupLine("[dim]↻ retry diff:[/]")
                                    for line in formatted.Split('\n') do
                                        let color = if line.StartsWith "+" then "green" elif line.StartsWith "-" then "red" else "yellow"
                                        AnsiConsole.MarkupLine($"  [{color}]{Markup.Escape line}[/]")
                                else
                                    Console.Out.WriteLine($"↻ retry diff:\n{formatted}")
                        | _ -> ()
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
                                    String.concat "\n" lines.[..499] + $"\n… [low-bandwidth: {lines.Length - 500} lines truncated]"
                                else output
                            else output
                        let name, args, elapsed =
                            match toolMeta.TryGetValue id with
                            | true, (n, a, tick) -> n, a, Stopwatch.GetElapsedTime(tick)
                            | false, _ -> "?", "?", TimeSpan.Zero
                        // Track file edits for /activity heatmap
                        if not isErr && (name = "Write" || name = "Edit") then
                            let key' = "\"path\":"
                            let idx = args.IndexOf key'
                            if idx >= 0 then
                                let rest = args.Substring(idx + key'.Length).TrimStart()
                                if rest.StartsWith '"' then
                                    let endIdx = rest.IndexOf('"', 1)
                                    if endIdx > 1 then
                                        let p = rest.[1..endIdx-1]
                                        let count = activityStore |> Map.tryFind p |> Option.defaultValue 0
                                        activityStore <- activityStore |> Map.add p (count + 1)
                        if not isErr && output.Length > 0 then
                            ClipRing.push output
                        if isErr then
                            errorToolCalls.Add((name, args, output))
                            Fugue.Core.ErrorTrend.record currentSessionId name output
                            failedArgs.[name] <- args
                        else
                            failedArgs.Remove name |> ignore
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
                        Console.Out.Write($"\x1b[{rawLines}A\x1b[J")
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
                    let timeStr = if mins = 1 then "~1 min read" else $"~{mins} min read"
                    let footer = $"[~{wordCount} words · {timeStr}]"
                    if Render.isColorEnabled () then
                        AnsiConsole.MarkupLine($"[dim]{Markup.Escape footer}[/]")
                    else
                        Console.Out.WriteLine footer
                if toolCallsThisTurn >= 10 then
                    let msg = strings.ToolCallsWarning.Replace("{0}", string toolCallsThisTurn)
                    if Render.isColorEnabled () then
                        AnsiConsole.MarkupLine($"[dim yellow]⚠️ {Markup.Escape msg}[/]")
                    else
                        Console.Out.WriteLine($"⚠️ {msg}")
                if errorToolCalls.Count > 0 then
                    lastFailedTurn <- Some { BugReport.UserPrompt = input; BugReport.ToolCalls = errorToolCalls |> Seq.toList; BugReport.AiResponse = responseText.ToString(); BugReport.ErrorText = None }
                lastResponseWasPlan <- Fugue.Core.ConversationIntent.looksLikePlan (responseText.ToString())
        with
        | :? OperationCanceledException ->
            if assistantStreaming then Console.Out.WriteLine ()
            AnsiConsole.Write(Render.cancelled strings)
            AnsiConsole.WriteLine()
        | ex ->
            if assistantStreaming then Console.Out.WriteLine ()
            AnsiConsole.Write(Render.errorLine strings ex.Message)
            AnsiConsole.WriteLine()
            lastFailedTurn <- Some { BugReport.UserPrompt = input; BugReport.ToolCalls = errorToolCalls |> Seq.toList; BugReport.AiResponse = responseText.ToString(); BugReport.ErrorText = Some ex.Message }
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
let run (initialAgent: AIAgent) (initialCfg: AppConfig) (cwd: string) (lastSummary: string option) (rebuildAgent: AppConfig -> AIAgent) : Task<unit> = task {
    let mutable agent = initialAgent
    let mutable cfg   = initialCfg
    let sessionStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    currentSessionId <- generateUlid ()
    use cancelSrc = new CancelSource()
    let handler = ConsoleCancelEventHandler(fun _ args -> cancelSrc.OnCtrlC args)
    Console.CancelKeyPress.AddHandler handler
    use _sigtermReg =
        System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM,
            fun ctx ->
                ctx.Cancel <- true
                cancelSrc.RequestQuit())
    let! initialSession = agent.CreateSessionAsync(CancellationToken.None)
    let mutable session : AgentSession | null = initialSession
    // Pre-fill for the next ReadLine call. Used by /menu and similar pickers
    // that hand a partial command back to the user for completion.
    let mutable nextInputPrefill : string = ""
    let providerName, modelName = providerInfo cfg.Provider
    DebugLog.sessionStart providerName modelName cwd
    StatusBar.start cwd cfg
    Render.showBanner ()
    if cfg.DryRun then
        if Render.isColorEnabled () then
            AnsiConsole.MarkupLine "[yellow bold]⚑ dry-run[/] [dim]— tools will log calls without executing[/]"
        else
            Console.Out.WriteLine "⚑ dry-run — tools will log calls without executing"
        AnsiConsole.WriteLine()
    // Show last-session resuming hint if available
    let strings0 = pick cfg.Ui.Locale
    lastSummary |> Option.iter (fun s ->
        let first = let i = s.IndexOf('.') in if i > 0 && i < 120 then s.[..i - 1] else s.[..min 119 (s.Length - 1)]
        let msg = strings0.SessionResuming.Replace("{0}", first)
        if Render.isColorEnabled () then
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape msg}[/]")
        else
            Console.Out.WriteLine msg
        AnsiConsole.WriteLine())
    // Show template badge on startup
    cfg.TemplateName |> Option.iter (fun name ->
        if Render.isColorEnabled () then
            AnsiConsole.MarkupLine($"[dim cyan]▸ template: {Markup.Escape name}[/]")
        else
            Console.Out.WriteLine($"template: {name}")
        AnsiConsole.WriteLine())
    prelude cfg
    Console.Out.Write "\x1b[?2004h"   // enable bracketed paste
    Console.Out.Flush()

    // .env file awareness: detect .env in cwd and offer to load into session env.
    let dotEnvPath = System.IO.Path.Combine(cwd, ".env")
    if System.IO.File.Exists dotEnvPath then
        let suspiciousPrefixes = [| "sk-"; "sk-ant-"; "ghp_"; "gho_"; "ghs_"; "AKIA"; "AIza"; "xox" |]
        let lines =
            System.IO.File.ReadAllLines dotEnvPath
            |> Array.filter (fun l -> not (l.TrimStart().StartsWith '#') && l.Contains '=')
        let hasSecrets =
            lines |> Array.exists (fun l ->
                let v = let i = l.IndexOf '=' in if i >= 0 then l.Substring(i + 1).Trim().Trim('"', '\'') else ""
                suspiciousPrefixes |> Array.exists (fun p -> v.StartsWith p))
        let badge = if hasSecrets then " [yellow]⚠ contains secrets[/]" else ""
        if Render.isColorEnabled () then
            AnsiConsole.MarkupLine($"[dim]▸ .env detected ({lines.Length} vars){badge} — /env load to import[/]")
        else
            Console.Out.WriteLine($".env detected ({lines.Length} vars) — /env load to import")
        AnsiConsole.WriteLine()

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

    let mutable liveStrings = pick cfg.Ui.Locale
    let mutable savedUi = cfg.Ui   // mutable copy for persisting runtime UI toggles
    let mutable verbosityPrefix : string option = None
    let mutable turnNumber = 0
    let mutable sessionNotes : (int * string) list = []  // (turnNumber, text)

    // Hot-reload: watch ~/.fugue/config.json for changes.
    // On each turn check the sentinel channel; if fired, reload UI settings.
    let configWatchSentinel = "__config_reload__"
    let cfgPath = Fugue.Core.Config.configFilePath ()
    if System.IO.File.Exists cfgPath then
        FileWatcher.add cfgPath configWatchSentinel

    try
        while not cancelSrc.QuitRequested do
            // Apply any pending hot-reloads before reading input.
            for cmd in FileWatcher.drain () do
                if cmd = configWatchSentinel then
                    match Fugue.Core.Config.load [||] with
                    | Ok newCfg ->
                        MarkdownRender.initTheme newCfg.Ui.Theme
                        Render.initTheme newCfg.Ui.Theme
                        StatusBar.initTheme newCfg.Ui.Theme
                        StatusBar.setLowBandwidth newCfg.LowBandwidth
                        Render.initTypewriter newCfg.Ui.TypewriterMode
                        Render.initBubbles (newCfg.Ui.BubblesMode || newCfg.Ui.Theme = "bubbles")
                        let emojiEnabled =
                            match newCfg.Ui.EmojiMode with
                            | "always" -> true
                            | "never"  -> false
                            | _        -> not (Fugue.Core.EmojiMap.terminalSupportsEmoji ())
                        MarkdownRender.initEmoji emojiEnabled
                        savedUi <- newCfg.Ui
                        liveStrings <- pick newCfg.Ui.Locale
                        AnsiConsole.MarkupLine "[dim]Config reloaded.[/]"
                        StatusBar.refresh ()
                    | Error _ -> ()   // ignore malformed config
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
                AnsiConsole.MarkupLine($"[dim]{sym} turn {idx}[/]")
            let callbacks : ReadLine.ReadLineCallbacks =
                { OnClearScreen  = StatusBar.refresh
                  OnAnnotateUp   = fun () -> doAnnotate Fugue.Core.Annotation.Up
                  OnAnnotateDown = fun () -> doAnnotate Fugue.Core.Annotation.Down }
            // Drain file-watch triggers from background FileSystemWatcher
            let watchTriggers = FileWatcher.drain ()
            for wcmd in watchTriggers do
                AnsiConsole.Write(Markup($"[dim]⟳ watch → {Markup.Escape wcmd}[/]"))
                AnsiConsole.WriteLine()
                turnNumber <- turnNumber + 1
                AnsiConsole.Write(Render.userMessage cfg.Ui $"[watch] {wcmd}" turnNumber)
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
                    AnsiConsole.Write(Markup($"[dim]▶ {Markup.Escape step}[/]"))
                    AnsiConsole.WriteLine()
                    System.Threading.Tasks.Task.FromResult(Some step)
                | None ->
                    let initial = nextInputPrefill
                    nextInputPrefill <- ""
                    ReadLine.readAsync (Render.prompt cfg.Ui modelShort) liveStrings callbacks (Render.isColorEnabled ()) initial cancelSrc.Token
            // Colon command expansion: :q → /exit, :n → /new, :h → /help, :s → /scratch send
            let colonExpand (s: string) =
                if not (s.StartsWith ':') || s.Length < 2 then s
                else
                    match s.Substring(1).Trim() with
                    | "q" | "Q" -> "/exit"
                    | "n"       -> "/new"
                    | "h" | "?" -> "/help"
                    | "s"       -> "/scratch send"
                    | rest when rest.StartsWith "e " -> $"@{rest.Substring(2).Trim()}"
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
                AnsiConsole.Write(Markup("[bold]" + Markup.Escape liveStrings.HelpHeader + "[/]"))
                AnsiConsole.WriteLine()
                let helpItems = SlashCommands.getAll liveStrings
                for (name, desc) in helpItems do
                    AnsiConsole.Write(Markup("  [cyan]" + Markup.Escape name + "[/]  [dim]" + Markup.Escape desc + "[/]"))
                    AnsiConsole.WriteLine()
                AnsiConsole.WriteLine()
                AnsiConsole.MarkupLine "[dim]Tip: turn numbers [[N]] are searchable — use your terminal's [bold]Cmd+F[/] / [bold]Ctrl+Shift+F[/] to jump back to any past message.[/]"
                AnsiConsole.MarkupLine "[dim]Tip: try [bold]/menu[/] for a scrollable arrow-key picker.[/]"
                StatusBar.refresh ()
            | Some s when s = "/menu" || s = "/commands" ->
                let allCmds = SlashCommands.getAll liveStrings
                match Picker.pick "Slash commands" allCmds 0 with
                | None ->
                    AnsiConsole.MarkupLine "[dim]Cancelled[/]"
                | Some i ->
                    let (name, _) = allCmds.[i]
                    nextInputPrefill <- Picker.templateOf name
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
                        AnsiConsole.Write(Render.errorLine liveStrings liveStrings.GitLogNoRepo)
                        AnsiConsole.WriteLine()
                    | true ->
                        let out = proc.StandardOutput.ReadToEnd()
                        proc.WaitForExit()
                        if proc.ExitCode <> 0 || String.IsNullOrWhiteSpace out then
                            AnsiConsole.Write(Render.errorLine liveStrings liveStrings.GitLogNoRepo)
                            AnsiConsole.WriteLine()
                        else
                            let prompt = liveStrings.GitLogPrompt.Replace("%s", out)
                            do! streamAndRender agent session prompt cfg cancelSrc zenMode
                with ex ->
                    AnsiConsole.Write(Render.errorLine liveStrings ex.Message)
                    AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/clear" ->
                AnsiConsole.Clear()
                StatusBar.refresh ()
            | Some s when s = "/squash" || s.StartsWith "/squash " ->
                let rawArg = if s = "/squash" then "" else s.Substring(8).TrimStart()
                let n = if String.IsNullOrWhiteSpace rawArg then 0 else (try int rawArg with _ -> 0)
                if n <= 0 then
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.SquashUsage + "[/]"))
                    AnsiConsole.WriteLine()
                else
                    let psi = System.Diagnostics.ProcessStartInfo()
                    psi.FileName <- "git"
                    psi.ArgumentList.Add "log"
                    psi.ArgumentList.Add "--oneline"
                    psi.ArgumentList.Add $"-{n}"
                    psi.UseShellExecute <- false
                    psi.RedirectStandardOutput <- true
                    psi.WorkingDirectory <- cwd
                    try
                        use proc = new System.Diagnostics.Process()
                        proc.StartInfo <- psi
                        match proc.Start() with
                        | false ->
                            AnsiConsole.Write(Render.errorLine liveStrings liveStrings.SquashNoRepo)
                            AnsiConsole.WriteLine()
                        | true ->
                            let out = proc.StandardOutput.ReadToEnd()
                            proc.WaitForExit()
                            if proc.ExitCode <> 0 || String.IsNullOrWhiteSpace out then
                                AnsiConsole.Write(Render.errorLine liveStrings liveStrings.SquashNoRepo)
                                AnsiConsole.WriteLine()
                            else
                                let prompt = $"""Here are the last {n} commits to be squashed:\n\n{out}\n\nPlease generate a single conventional commit message that summarizes all these changes.\nFollow the format: type(scope): description\n\nAlso show the git command to execute the squash:\n  git reset --soft HEAD~{n} && git commit -m "<your message>"\n\nDo NOT run the command — just show it for the user to review and run manually."""
                                do! streamAndRender agent session prompt cfg cancelSrc zenMode
                    with ex ->
                        AnsiConsole.Write(Render.errorLine liveStrings ex.Message)
                        AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/short" ->
                verbosityPrefix <- Some "[VERBOSITY: respond briefly, 1-2 sentences per point, no elaboration unless asked]\n\n"
                AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.VerbosityShortSet + "[/]"))
                AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/long" ->
                verbosityPrefix <- Some "[VERBOSITY: respond in detail, include explanations, examples, and context]\n\n"
                AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.VerbosityLongSet + "[/]"))
                AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/zen" ->
                zenMode <- not zenMode
                if zenMode then
                    StatusBar.stop ()
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.ZenOn + "[/]"))
                else
                    StatusBar.start cwd cfg
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.ZenOff + "[/]"))
                AnsiConsole.WriteLine()
                if not zenMode then StatusBar.refresh ()
            | Some s when s.StartsWith "/snippet" ->
                let parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                let sub = if parts.Length > 1 then parts.[1] else ""
                match sub with
                | "list" ->
                    if snippetStore.IsEmpty then
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.SnippetNone + "[/]"))
                    else
                        for KeyValue(name, (lang, code, src)) in snippetStore do
                            let preview = code.Split('\n').[0].[..60].Trim()
                            AnsiConsole.Write(Markup($"  [cyan]{Markup.Escape name}[/] [dim]({Markup.Escape lang} · {Markup.Escape src})[/]  {Markup.Escape preview}"))
                            AnsiConsole.WriteLine()
                    AnsiConsole.WriteLine()
                    StatusBar.refresh ()
                | "save" when parts.Length >= 4 ->
                    let name = parts.[2]
                    let fileRef = parts.[3]
                    // parse file:start-end
                    let colonIdx = fileRef.LastIndexOf ':'
                    if colonIdx < 1 then
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.SnippetUsage + "[/]"))
                        AnsiConsole.WriteLine()
                    else
                        let filePart = fileRef.[..colonIdx-1]
                        let rangePart = fileRef.[colonIdx+1..]
                        let dashIdx = rangePart.IndexOf '-'
                        match (if dashIdx > 0 then System.Int32.TryParse(rangePart.[..dashIdx-1]), System.Int32.TryParse(rangePart.[dashIdx+1..]) else (false, 0), (false, 0)) with
                        | (true, startL), (true, endL) ->
                            let fullPath = Fugue.Tools.PathSafety.resolve cwd filePart
                            if not (System.IO.File.Exists fullPath) then
                                AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.AtFileNotFound, filePart)) + "[/]"))
                            else
                                let lines = System.IO.File.ReadAllLines fullPath
                                let s1 = max 1 startL
                                let e1 = min lines.Length endL
                                let code = lines.[s1-1..e1-1] |> String.concat "\n"
                                let ext = (System.IO.Path.GetExtension filePart |> Option.ofObj |> Option.defaultValue "").TrimStart('.')
                                let lang = if ext = "" then "text" else ext
                                snippetStore <- snippetStore |> Map.add name (lang, code, fileRef)
                                AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.SnippetSaved, name)) + "[/]"))
                            AnsiConsole.WriteLine()
                        | _ ->
                            AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.SnippetUsage + "[/]"))
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
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.SnippetNotFound, query)) + "[/]"))
                        AnsiConsole.WriteLine()
                    | Some(lang, code, _) ->
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.SnippetInjected, query)) + "[/]"))
                        AnsiConsole.WriteLine()
                        let block = $"```{lang}\n{code}\n```"
                        do! streamAndRender agent session block cfg cancelSrc zenMode
                    StatusBar.refresh ()
                | "remove" when parts.Length >= 3 ->
                    let name = parts.[2..] |> String.concat " "
                    if snippetStore.ContainsKey name then
                        snippetStore <- snippetStore |> Map.remove name
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.SnippetRemoved, name)) + "[/]"))
                    else
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.SnippetNotFound, name)) + "[/]"))
                    AnsiConsole.WriteLine()
                    StatusBar.refresh ()
                | _ ->
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.SnippetUsage + "[/]"))
                    AnsiConsole.WriteLine()
                    StatusBar.refresh ()
            | Some s when s = "/bookmarks" || (s.StartsWith "/bookmark " && s.Contains " remove ") || s.StartsWith "/bookmark" ->
                let parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                let isBookmarks = s = "/bookmarks"
                let isRemove = parts.Length >= 3 && parts.[1] = "remove"
                if isBookmarks then
                    if bookmarks.IsEmpty then
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.BookmarkNone + "[/]"))
                        AnsiConsole.WriteLine()
                    else
                        for (i, (name, file, s1, e1)) in bookmarks |> List.indexed do
                            AnsiConsole.Write(Markup($"  [cyan]{i+1}.[/] [bold]{Markup.Escape name}[/] [dim]{Markup.Escape file}:{s1}-{e1}[/]"))
                            AnsiConsole.WriteLine()
                    StatusBar.refresh ()
                elif isRemove then
                    let name = parts.[2..] |> String.concat " "
                    let before = bookmarks.Length
                    bookmarks <- bookmarks |> List.filter (fun (n, _, _, _) -> n <> name)
                    if bookmarks.Length < before then
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.BookmarkRemoved, name)) + "[/]"))
                    else
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.BookmarkNotFound, name)) + "[/]"))
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
                            let grepArgs = [| "--include=*.fs"; "--include=*.cs"; "--include=*.ts"; "-rn"; $"\\b{name}\\b"; cwd |]
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
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.BookmarkUsage + "[/]"))
                    | Some(fullPath, s1, e1) ->
                        bookmarks <- bookmarks @ [(name, fullPath, s1, e1)]
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.BookmarkSaved, name)) + "[/]"))
                    AnsiConsole.WriteLine()
                    StatusBar.refresh ()
                else
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.BookmarkUsage + "[/]"))
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
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.ClearHistoryDone + "[/]"))
                    AnsiConsole.WriteLine()
                with ex ->
                    session <- null
                    AnsiConsole.Write(Render.errorLine liveStrings ex.Message)
                    AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/http " || s = "/http" ->
                let raw = if s = "/http" then "" else s.Substring("/http ".Length)
                if raw.Trim() = "" then
                    AnsiConsole.Write(Markup("[dim]Usage: /http METHOD URL [-H \"K: V\"] [-d BODY] [--timeout N] [--inject][/]"))
                    AnsiConsole.WriteLine()
                else
                    // Parse: METHOD URL [-H "K: V"]... [-d BODY] [--timeout N] [--inject]
                    let argv = raw.Trim().Split(' ', System.StringSplitOptions.RemoveEmptyEntries) |> Array.toList
                    let method', rest =
                        match argv with
                        | m :: tail when System.Text.RegularExpressions.Regex.IsMatch(m, @"^[A-Z]+$") -> m, tail
                        | _ -> "GET", argv
                    let url, rest2 =
                        match rest with
                        | u :: tail -> System.Environment.ExpandEnvironmentVariables(u), tail
                        | [] -> "", []
                    let mutable headers : (string * string) list = []
                    let mutable body : string option = None
                    let mutable timeoutSec = 30
                    let mutable inject = false
                    let mutable i = 0
                    let arr = List.toArray rest2
                    while i < arr.Length do
                        match arr.[i] with
                        | "-H" when i + 1 < arr.Length ->
                            let h = arr.[i + 1]
                            let ci = h.IndexOf(':')
                            if ci > 0 then headers <- headers @ [(h.[..ci-1].Trim(), h.[ci+1..].Trim())]
                            i <- i + 2
                        | "-d" when i + 1 < arr.Length ->
                            body <- Some (System.Environment.ExpandEnvironmentVariables arr.[i + 1])
                            i <- i + 2
                        | "--timeout" when i + 1 < arr.Length ->
                            match System.Int32.TryParse arr.[i + 1] with
                            | true, n -> timeoutSec <- n
                            | _ -> ()
                            i <- i + 2
                        | "--inject" -> inject <- true; i <- i + 1
                        | _ -> i <- i + 1
                    if url = "" then
                        AnsiConsole.Write(Markup("[red]Missing URL[/]"))
                        AnsiConsole.WriteLine()
                    else
                        use client = new System.Net.Http.HttpClient()
                        client.Timeout <- System.TimeSpan.FromSeconds(float timeoutSec)
                        use req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod(method'), url)
                        for (k, v) in headers do
                            req.Headers.TryAddWithoutValidation(k, v) |> ignore
                        match body with
                        | Some b ->
                            let ct = headers |> List.tryFind (fst >> (=) "Content-Type") |> Option.map snd |> Option.defaultValue "application/json"
                            req.Content <- new System.Net.Http.StringContent(b, System.Text.Encoding.UTF8, ct)
                        | None -> ()
                        try
                            let! resp = client.SendAsync(req)
                            let! respBody = resp.Content.ReadAsStringAsync()
                            let status = int resp.StatusCode
                            let statusColor = if status >= 500 then "red" elif status >= 400 then "yellow" elif status >= 200 then "green" else "dim"
                            if Render.isColorEnabled () then
                                AnsiConsole.MarkupLine($"[bold][{statusColor}]HTTP {status} {resp.StatusCode}[/][/]")
                            else
                                Console.Out.WriteLine($"HTTP {status} {resp.StatusCode}")
                            let preview = if respBody.Length > 4096 then respBody.[..4095] + $"\n… [{respBody.Length} chars total]" else respBody
                            AnsiConsole.Write(Render.assistantFinal preview)
                            AnsiConsole.WriteLine()
                            if inject then
                                let ctx = $"[HTTP response from {method'} {url} — status {status}]\n{respBody}"
                                turnNumber <- turnNumber + 1
                                AnsiConsole.Write(Render.userMessage cfg.Ui $"[injected HTTP response from {method'} {url}]" turnNumber)
                                AnsiConsole.WriteLine()
                                do! streamAndRender agent session ctx cfg cancelSrc zenMode
                        with ex ->
                            AnsiConsole.Write(Render.errorLine liveStrings $"HTTP error: {ex.Message}")
                            AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/tools" ->
                AnsiConsole.Write(Markup("[bold]" + Markup.Escape liveStrings.ToolsHeader + "[/]"))
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
                        tbl.AddRow([| Markup.Escape feat; check ok; $"[dim]{Markup.Escape src}[/]" |]) |> ignore
                    row "ANSI color"        ansiColor  "--color flag"
                    row "True color (24-bit)" trueColor $"COLORTERM={colorTerm}"
                    row "256 colors"        colors256  $"TERM={term}"
                    let langDisplay = if lang <> "" then lang else lcAll
                    row "UTF-8"            utf8       $"LANG={langDisplay}"
                    row "Emoji"            emoji      $"TERM_PROGRAM={termProg}"
                    row "Kitty protocol"   kitty      $"TERM_PROGRAM={termProg}"
                    row "OSC 8 hyperlinks" osc8       (if kitty then "kitty" elif iterm2 then "iTerm2" elif wezterm then "WezTerm" else "—")
                    row $"Terminal width = {termW}" true "Console.WindowWidth"
                    row "Docker / container" inDocker   (if inDocker then "/.dockerenv or cgroup" else "not detected")
                    AnsiConsole.Write tbl
                else
                    let yn ok = if ok then "yes" else "no"
                    Console.Out.WriteLine($"ANSI color:      {yn ansiColor}")
                    Console.Out.WriteLine($"True color:      {yn trueColor}  COLORTERM={colorTerm}")
                    Console.Out.WriteLine($"256 colors:      {yn colors256}  TERM={term}")
                    Console.Out.WriteLine($"UTF-8:           {yn utf8}  LANG={lang}")
                    Console.Out.WriteLine($"Emoji:           {yn emoji}")
                    Console.Out.WriteLine($"Kitty protocol:  {yn kitty}")
                    Console.Out.WriteLine($"OSC 8:           {yn osc8}")
                    Console.Out.WriteLine($"Terminal width:  {termW}")
                    Console.Out.WriteLine($"Docker:          {yn inDocker}")
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
                            AnsiConsole.MarkupLine($"[dim]{Markup.Escape (String.Format(liveStrings.BenchRunning, string attempt, string nArg))}[/]")
                        else
                            Console.Out.WriteLine(String.Format(liveStrings.BenchRunning, string attempt, string nArg))
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
                    AnsiConsole.Write(Render.errorLine liveStrings liveStrings.BenchAborted)
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
                            tbl.AddRow([| label; $"{ttftVal}ms"; $"{totalVal}ms" |]) |> ignore
                        addRow "min"  (pct ttfts 0)   (pct totals 0)
                        addRow "p50"  (pct ttfts 50)  (pct totals 50)
                        addRow "p90"  (pct ttfts 90)  (pct totals 90)
                        addRow "p99"  (pct ttfts 99)  (pct totals 99)
                        addRow "max"  (pct ttfts 100) (pct totals 100)
                        AnsiConsole.Write tbl
                    else
                        Console.Out.WriteLine(String.Format("{0,-6}  {1,-8}  {2,-8}", "metric", "TTFT", "Total"))
                        let pr label p = Console.Out.WriteLine($"{label,-6}  {pct ttfts p,-6}ms  {pct totals p,-6}ms")
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
                            if diff.TotalDays >= 1.0 then $"{int diff.TotalDays}d ago"
                            elif diff.TotalHours >= 1.0 then $"{int diff.TotalHours}h ago"
                            else $"{max 1 (int diff.TotalMinutes)}m ago"
                        if Render.isColorEnabled () then
                            AnsiConsole.MarkupLine($"[dim]#{idx + 1} ({ago})[/]")
                            AnsiConsole.Write(MarkdownRender.toRenderable summary)
                            AnsiConsole.WriteLine()
                        else
                            Console.Out.WriteLine($"#{idx + 1} ({ago})")
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
                                AnsiConsole.MarkupLine($"  [cyan]{Markup.Escape name}[/]")
                StatusBar.refresh ()
            | Some s when s = "/gen" || s.StartsWith "/gen " ->
                let kind = (if s = "/gen" then "" else s.Substring(4)).Trim().ToLowerInvariant()
                match kind with
                | "uuid" | "" ->
                    let v = System.Guid.NewGuid().ToString()
                    AnsiConsole.MarkupLine($"[green]{v}[/]")
                    ClipRing.push v
                | "ulid" ->
                    let v = generateUlid ()
                    AnsiConsole.MarkupLine($"[green]{v}[/]")
                    ClipRing.push v
                | "nanoid" ->
                    let v = generateNanoid ()
                    AnsiConsole.MarkupLine($"[green]{v}[/]")
                    ClipRing.push v
                | other ->
                    AnsiConsole.MarkupLine($"[red]Unknown generator '[/][bold red]{Markup.Escape other}[/][red]'. Use: /gen uuid|ulid|nanoid[/]")
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
                    let msg = String.Format(liveStrings.RateSaved, string targetTurn, ratingArg)
                    if Render.isColorEnabled () then
                        let color = if ratingArg = "up" then "green" else "red"
                        AnsiConsole.MarkupLine($"[{color}]{Markup.Escape msg}[/]")
                    else Console.Out.WriteLine msg
                | _ ->
                    if Render.isColorEnabled () then
                        AnsiConsole.MarkupLine($"[dim]{Markup.Escape liveStrings.RateUsage}[/]")
                    else Console.Out.WriteLine liveStrings.RateUsage
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
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape liveStrings.AnnotateUsage}[/]")
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
                    let noteStr   = note |> Option.map (fun n -> $" \"{n}\"") |> Option.defaultValue ""
                    let msg = $"turn {targetTurn}{ratingStr}{noteStr}"
                    AnsiConsole.MarkupLine($"[dim]annotated: {Markup.Escape msg}[/]")
                StatusBar.refresh ()
            | Some s when s = "/issue" || s.StartsWith "/issue " ->
                let rawArg = if s = "/issue" then "" else s.Substring(7).TrimStart()
                if String.IsNullOrWhiteSpace rawArg then
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.IssueUsage + "[/]"))
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
                            AnsiConsole.Write(Render.errorLine liveStrings (String.Format(liveStrings.IssueNotFound, rawArg)))
                            AnsiConsole.WriteLine()
                        | true ->
                            let output = proc.StandardOutput.ReadToEnd()
                            proc.WaitForExit()
                            if proc.ExitCode <> 0 then
                                let err = proc.StandardError.ReadToEnd().Trim()
                                AnsiConsole.Write(Render.errorLine liveStrings (String.Format(liveStrings.IssueNotFound, err)))
                                AnsiConsole.WriteLine()
                            else
                                // Extract a string field from compact gh JSON output (AOT-safe, no JsonSerializer)
                                let extractField (field: string) (json: string) =
                                    let key = $"\"{field}\":"
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
                                let issueContext = $"Issue #{rawArg}: {title}\n\n{body}"
                                AnsiConsole.MarkupLine($"[dim]{Markup.Escape (String.Format(liveStrings.IssueFetched, rawArg, title))}[/]")
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
                                let branchName = $"feat/issue-{rawArg}-{words}"
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
                                            AnsiConsole.MarkupLine($"[dim]created branch {Markup.Escape branchName}[/]")
                                            AnsiConsole.WriteLine()
                                with _ -> ()   // git unavailable or not a repo — silent
                                // Inject issue context into conversation
                                do! streamAndRender agent session issueContext cfg cancelSrc zenMode
                    with ex ->
                        AnsiConsole.Write(Render.errorLine liveStrings ex.Message)
                        AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/model suggest" ->
                AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.AskingModelRecommendation + "[/]"))
                AnsiConsole.WriteLine()
                do! streamAndRender agent session liveStrings.ModelSuggestPrompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s = "/model" || s = "/model list" ->
                let prov, currentModel = providerInfo cfg.Provider
                AnsiConsole.MarkupLine($"[bold]Current:[/] [cyan]{Markup.Escape prov}[/] / [green]{Markup.Escape currentModel}[/]")
                AnsiConsole.MarkupLine($"[dim]Fetching models from {Markup.Escape prov}…[/]")
                let! models = ModelDiscovery.getModels cfg.Provider cfg.BaseUrl
                if List.isEmpty models then
                    AnsiConsole.MarkupLine "[yellow]No models discovered. Use /model set <name> to switch manually.[/]"
                    StatusBar.refresh ()
                else
                    let pickModel (newModel: string) =
                        task {
                            let newProvider =
                                match cfg.Provider with
                                | Anthropic(k, _) -> Anthropic(k, newModel)
                                | OpenAI(k, _)    -> OpenAI(k, newModel)
                                | Ollama(ep, _)   -> Ollama(ep, newModel)
                            let newCfg = { cfg with Provider = newProvider }
                            try
                                let newAgent = rebuildAgent newCfg
                                agent <- newAgent
                                cfg   <- newCfg
                                let! freshSession = agent.CreateSessionAsync(CancellationToken.None)
                                session <- freshSession
                                let provName, _ = providerInfo cfg.Provider
                                AnsiConsole.MarkupLine($"[green]✓[/] model → [cyan]{Markup.Escape provName}[/] / [green]{Markup.Escape newModel}[/] [dim](history reset)[/]")
                                StatusBar.start cwd cfg
                            with ex ->
                                AnsiConsole.MarkupLine($"[red]✗ failed to switch model: {Markup.Escape ex.Message}[/]")
                        }
                    // Interactive arrow-key picker.
                    // Indices: 0..models.Length-1 = models; models.Length = [s] suggest; models.Length+1 = [q] cancel.
                    let modelsArr  = List.toArray models
                    let suggestIdx = modelsArr.Length
                    let cancelIdx  = modelsArr.Length + 1
                    let totalRows  = modelsArr.Length + 2
                    let colour     = Render.isColorEnabled ()

                    // Pre-compute one row's text. `selected` switches highlight / arrow.
                    let renderRow (idx: int) (selected: bool) : string =
                        let arrow   = if selected then (if colour then "▸" else ">") else " "
                        let prefix  = $"{arrow} "
                        if idx < modelsArr.Length then
                            let name    = modelsArr.[idx]
                            let isCurr  = (name = currentModel)
                            let suffix  =
                                if isCurr then
                                    if colour then "  [green]← current[/]" else "  ← current"
                                else ""
                            if colour then
                                if selected then $"{prefix}[bold cyan]{Markup.Escape name}[/]{suffix}"
                                else            $"{prefix}{Markup.Escape name}{suffix}"
                            else
                                $"{prefix}{name}{suffix}"
                        elif idx = suggestIdx then
                            if colour then
                                if selected then $"{prefix}[bold cyan][[s]] /model suggest[/]"
                                else            $"{prefix}[dim][[s]] /model suggest[/]"
                            else
                                $"{prefix}[s] /model suggest"
                        else // cancelIdx
                            if colour then
                                if selected then $"{prefix}[bold cyan][[q]] cancel[/]"
                                else            $"{prefix}[dim][[q]] cancel[/]"
                            else
                                $"{prefix}[q] cancel"

                    let footer =
                        if colour then "[dim]↑/↓ navigate · Enter select · Esc cancel[/]"
                        else "↑/↓ navigate · Enter select · Esc cancel"

                    AnsiConsole.WriteLine()
                    // Initial render. Each row + blank line + footer.
                    for i in 0 .. totalRows - 1 do
                        if colour then AnsiConsole.MarkupLine(renderRow i (i = 0))
                        else Console.Out.WriteLine(renderRow i (i = 0))
                    AnsiConsole.WriteLine()
                    if colour then AnsiConsole.MarkupLine footer
                    else Console.Out.WriteLine footer

                    let renderedLines = totalRows + 2  // rows + blank + footer

                    let redraw (idx: int) =
                        // Move cursor up to start of menu, clear each line, redraw.
                        Console.Out.Write($"\x1b[{renderedLines}F")
                        for i in 0 .. totalRows - 1 do
                            Console.Out.Write "\x1b[K"
                            if colour then AnsiConsole.MarkupLine(renderRow i (i = idx))
                            else Console.Out.WriteLine(renderRow i (i = idx))
                        Console.Out.Write "\x1b[K"
                        Console.Out.WriteLine()
                        Console.Out.Write "\x1b[K"
                        if colour then AnsiConsole.MarkupLine footer
                        else Console.Out.WriteLine footer

                    let clearMenu () =
                        // Wipe the menu before printing the result.
                        Console.Out.Write($"\x1b[{renderedLines}F")
                        for _ in 1 .. renderedLines do
                            Console.Out.Write "\x1b[K"
                            Console.Out.Write "\x1b[1B"
                        Console.Out.Write($"\x1b[{renderedLines}F")

                    let mutable currentIndex = 0
                    let mutable picked       : int option = None
                    let mutable cancelled    = false

                    while picked.IsNone && not cancelled do
                        let key = Console.ReadKey(intercept = true)
                        match key.Key with
                        | ConsoleKey.UpArrow ->
                            currentIndex <- (currentIndex - 1 + totalRows) % totalRows
                            redraw currentIndex
                        | ConsoleKey.DownArrow ->
                            currentIndex <- (currentIndex + 1) % totalRows
                            redraw currentIndex
                        | ConsoleKey.Enter ->
                            picked <- Some currentIndex
                        | ConsoleKey.Escape ->
                            cancelled <- true
                        | _ ->
                            let ch = key.KeyChar
                            if ch = 'q' || ch = 'Q' then
                                cancelled <- true
                            elif ch = 's' || ch = 'S' then
                                picked <- Some suggestIdx
                            elif key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key = ConsoleKey.C then
                                cancelled <- true
                            elif ch >= '1' && ch <= '9' then
                                let n = int ch - int '0'
                                if n >= 1 && n <= modelsArr.Length then
                                    picked <- Some (n - 1)

                    clearMenu ()

                    match picked with
                    | None ->
                        AnsiConsole.MarkupLine "[dim]Cancelled[/]"
                    | Some i when i = cancelIdx ->
                        AnsiConsole.MarkupLine "[dim]Cancelled[/]"
                    | Some i when i = suggestIdx ->
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.AskingModelRecommendation + "[/]"))
                        AnsiConsole.WriteLine()
                        do! streamAndRender agent session liveStrings.ModelSuggestPrompt cfg cancelSrc zenMode
                    | Some i ->
                        do! pickModel modelsArr.[i]
                    StatusBar.refresh ()
            | Some s when s.StartsWith "/model set " ->
                let newModel = s.Substring("/model set ".Length).Trim()
                if newModel = "" then
                    AnsiConsole.MarkupLine "[red]/model set requires a model name[/]"
                else
                    let newProvider =
                        match cfg.Provider with
                        | Anthropic(k, _) -> Anthropic(k, newModel)
                        | OpenAI(k, _)    -> OpenAI(k, newModel)
                        | Ollama(ep, _)   -> Ollama(ep, newModel)
                    let newCfg = { cfg with Provider = newProvider }
                    try
                        let newAgent = rebuildAgent newCfg
                        agent <- newAgent
                        cfg   <- newCfg
                        let! freshSession = agent.CreateSessionAsync(CancellationToken.None)
                        session <- freshSession
                        let prov, _ = providerInfo cfg.Provider
                        AnsiConsole.MarkupLine($"[green]✓[/] model → [cyan]{Markup.Escape prov}[/] / [green]{Markup.Escape newModel}[/] [dim](history reset)[/]")
                        StatusBar.start cwd cfg
                    with ex ->
                        AnsiConsole.MarkupLine($"[red]✗ failed to switch model: {Markup.Escape ex.Message}[/]")
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
                          yield $"=== FUGUE.md ===\n{trimmed}"
                      | None -> yield "(no FUGUE.md found)"
                      yield $"=== Top-level directory contents of {cwd} ===\n{treeOutput}" ]
                let onboardPrompt =
                    let ctx = String.concat "\n\n" contextParts
                    $"""Based on the following project context, generate a developer onboarding checklist.
Include: setup steps, key files to read, how to build/test, coding conventions, and first tasks to attempt.

{ctx}

Please generate a clear, actionable onboarding checklist."""
                do! streamAndRender agent session onboardPrompt cfg cancelSrc zenMode
            | Some s when s = "/review pr" || s = "/review pr " ->
                AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.ReviewPrUsage + "[/]"))
                AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/review pr " ->
                let arg = s.Substring(11).Trim()
                match System.Int32.TryParse arg with
                | false, _ ->
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.ReviewPrUsage + "[/]"))
                    AnsiConsole.WriteLine()
                | true, prNum when prNum < 1 ->
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.ReviewPrUsage + "[/]"))
                    AnsiConsole.WriteLine()
                | true, prNum ->
                    let meta = runCmd "gh" [| "pr"; "view"; string prNum; "--json"; "title,body" |] cwd
                    let diff = runCmd "gh" [| "pr"; "diff"; string prNum |] cwd
                    match diff with
                    | None ->
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.ReviewPrNotFound + "[/]"))
                        AnsiConsole.WriteLine()
                    | Some diffText ->
                        let truncatedDiff =
                            if diffText.Length > 8000 then diffText.[..7999] + "\n[diff truncated]"
                            else diffText
                        let titleBody = meta |> Option.defaultValue ""
                        let prompt =
                            liveStrings.ReviewPrPrompt
                                .Replace("{0}", string prNum)
                                .Replace("{1}", titleBody)
                                .Replace("{2}", truncatedDiff)
                        AnsiConsole.Write(Render.userMessage cfg.Ui $"Reviewing PR #{prNum}…" 0)
                        AnsiConsole.WriteLine()
                        do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/ask " ->
                let question = s.Substring(5).Trim()
                if String.IsNullOrWhiteSpace question then
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.AskUsage + "[/]"))
                    AnsiConsole.WriteLine()
                else
                    let augmented = $"[System: answer from your training knowledge only — do not invoke any tools, do not read files, do not run commands]\n\nUser: {question}"
                    AnsiConsole.Write(Render.userMessage cfg.Ui question 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session augmented cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s = "/ask" ->
                AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.AskUsage + "[/]"))
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
                        let msg = if errOut <> "" then errOut else $"git diff exited {proc.ExitCode}"
                        AnsiConsole.Write(Render.errorLine liveStrings msg)
                        AnsiConsole.WriteLine()
                    elif System.String.IsNullOrWhiteSpace output then
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.NoDiff + "[/]"))
                        AnsiConsole.WriteLine()
                    else
                        AnsiConsole.Write(DiffRender.toRenderable output)
                        AnsiConsole.WriteLine()
                with ex ->
                    AnsiConsole.Write(Render.errorLine liveStrings ex.Message)
                    AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/init" ->
                let fugueMdPath = System.IO.Path.Combine(cwd, "FUGUE.md")
                if System.IO.File.Exists fugueMdPath then
                    AnsiConsole.Write(Markup("[yellow]" + Markup.Escape liveStrings.InitExists + "[/]"))
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
                    AnsiConsole.Write(Render.errorLine liveStrings ex.Message)
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
                sessionTitle <- None
                for name in sessionEnvVars do
                    Environment.SetEnvironmentVariable(name, null)
                sessionEnvVars <- Set.empty
                FileWatcher.clear ()
                Macros.clear ()
                Fugue.Core.Checkpoint.clear ()
                currentSessionId <- generateUlid ()
                lastFailedTurn <- None
                lastResponseWasPlan <- false
                pinnedMessages <- []
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
                            AnsiConsole.Write(Render.errorLine liveStrings ex.Message)
                            AnsiConsole.WriteLine()
                    finally
                        cancelSrc.ExitChild ()
                        StatusBar.start cwd cfg
            | Some s when s = "/report-bug" ->
                match lastFailedTurn with
                | None ->
                    if Render.isColorEnabled () then AnsiConsole.MarkupLine "[dim]No failed turn to report yet.[/]"
                    else Console.Out.WriteLine "No failed turn to report yet."
                | Some turn ->
                    let asm     = System.Reflection.Assembly.GetExecutingAssembly()
                    let ver     = asm.GetName().Version |> Option.ofObj |> Option.map (fun v -> v.ToString()) |> Option.defaultValue "unknown"
                    let os      = System.Runtime.InteropServices.RuntimeInformation.OSDescription
                    let prov    = match cfg.Provider with | Anthropic _ -> "anthropic" | OpenAI _ -> "openai" | Ollama _ -> "ollama"
                    let model   = match cfg.Provider with | Anthropic(_, m) -> m | OpenAI(_, m) -> m | Ollama(_, m) -> m
                    let body    = BugReport.format ver os prov model cwd turn
                    let tmpPath = IO.Path.GetTempFileName() + ".md"
                    IO.File.WriteAllText(tmpPath, body)
                    if Render.isColorEnabled () then
                        AnsiConsole.MarkupLine($"[dim]Bug report saved to {tmpPath}[/]")
                        AnsiConsole.MarkupLine($"[dim]To file: gh issue create -R korat-ai/fugue --title '...' --body-file {tmpPath}[/]")
                    else
                        Console.Out.WriteLine($"Bug report saved to {tmpPath}")
                        Console.Out.WriteLine($"To file: gh issue create -R korat-ai/fugue --title '...' --body-file {tmpPath}")
                AnsiConsole.WriteLine()
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
                let docPrompt = System.String.Format(liveStrings.DocumentPrompt, docPath)
                AnsiConsole.Write(Render.userMessage cfg.Ui $"/document {docPath}" 0)
                AnsiConsole.WriteLine()
                do! streamAndRender agent session docPrompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s = "/activity" ->
                if activityStore.IsEmpty then
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.ActivityNone + "[/]"))
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
                            AnsiConsole.MarkupLine($"  [cyan]{heat}[/] [dim]{Markup.Escape label}[/] [grey]{count}x[/]")
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
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape (String.Format(liveStrings.MacroRecording, arg))}[/]")
                | "stop" ->
                    match Macros.stopRecording() with
                    | Some name ->
                        let cnt = Macros.list() |> List.tryFind (fun (n, _) -> n = name) |> Option.map snd |> Option.defaultValue 0
                        AnsiConsole.MarkupLine($"[dim]{Markup.Escape (String.Format(liveStrings.MacroSaved, name, cnt))}[/]")
                    | None ->
                        AnsiConsole.MarkupLine("[dim]no recording in progress[/]")
                | "play" when not (String.IsNullOrWhiteSpace arg) ->
                    match Macros.list() |> List.tryFind (fun (n, _) -> n = arg) with
                    | Some (_, cnt) ->
                        if Macros.enqueuePlay arg then
                            AnsiConsole.MarkupLine($"[dim]{Markup.Escape (String.Format(liveStrings.MacroPlaying, arg, cnt))}[/]")
                        else
                            AnsiConsole.MarkupLine($"[dim]{Markup.Escape (String.Format(liveStrings.MacroNotFound, arg))}[/]")
                    | None ->
                        AnsiConsole.MarkupLine($"[dim]{Markup.Escape (String.Format(liveStrings.MacroNotFound, arg))}[/]")
                | "list" | "" ->
                    let macros = Macros.list()
                    if macros.IsEmpty then
                        AnsiConsole.MarkupLine($"[dim]{Markup.Escape liveStrings.MacroNone}[/]")
                    else
                        for (name, cnt) in macros do
                            AnsiConsole.MarkupLine($"  [cyan]{Markup.Escape name}[/] [dim]({cnt} steps)[/]")
                | "remove" when not (String.IsNullOrWhiteSpace arg) ->
                    if Macros.remove arg then
                        AnsiConsole.MarkupLine($"[dim]{Markup.Escape (String.Format(liveStrings.MacroRemoved, arg))}[/]")
                    else
                        AnsiConsole.MarkupLine($"[dim]{Markup.Escape (String.Format(liveStrings.MacroNotFound, arg))}[/]")
                | _ ->
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape liveStrings.MacroUsage}[/]")
                AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/theme" ->
                let sub = s.Substring("/theme".Length).Trim()
                match sub with
                | "bubbles" ->
                    Render.toggleBubbles ()
                    let on = Render.isBubblesMode ()
                    if on then
                        AnsiConsole.MarkupLine($"[dim]{Markup.Escape liveStrings.ThemeBubblesOn}[/]")
                    else
                        AnsiConsole.MarkupLine($"[dim]{Markup.Escape liveStrings.ThemeBubblesOff}[/]")
                    savedUi <- { savedUi with BubblesMode = on }
                    try Config.saveToFile { cfg with Ui = savedUi } with _ -> ()
                | "typewriter" ->
                    Render.toggleTypewriter ()
                    let on = Render.isTypewriterMode ()
                    if on then
                        AnsiConsole.MarkupLine($"[dim]{Markup.Escape liveStrings.ThemeTypewriterOn}[/]")
                    else
                        AnsiConsole.MarkupLine($"[dim]{Markup.Escape liveStrings.ThemeTypewriterOff}[/]")
                    savedUi <- { savedUi with TypewriterMode = on }
                    try Config.saveToFile { cfg with Ui = savedUi } with _ -> ()
                | _ ->
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape liveStrings.ThemeUsage}[/]")
                AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/alias" ->
                let parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                let sub = if parts.Length > 1 then parts.[1] else ""
                match sub with
                | "list" | "" when s = "/alias" || s = "/alias list" ->
                    if aliasStore.IsEmpty then
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.AliasNone + "[/]"))
                    else
                        for KeyValue(name, expansion) in aliasStore do
                            AnsiConsole.Write(Markup($"  [cyan]/{Markup.Escape name}[/] [dim]→ {Markup.Escape expansion}[/]"))
                            AnsiConsole.WriteLine()
                    AnsiConsole.WriteLine()
                | "remove" when parts.Length >= 3 ->
                    let name = parts.[2]
                    aliasStore <- aliasStore |> Map.remove name
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.AliasRemoved, name)) + "[/]"))
                    AnsiConsole.WriteLine()
                | _ ->
                    // Parse: /alias <name> = <expansion>
                    let rest = s.Substring("/alias ".Length)
                    let eqIdx = rest.IndexOf '='
                    if eqIdx < 1 then
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.AliasUsage + "[/]"))
                    else
                        let name = rest.[..eqIdx-1].Trim().TrimStart('/')
                        let expansion = rest.[eqIdx+1..].Trim()
                        if not (String.IsNullOrWhiteSpace name) && not (String.IsNullOrWhiteSpace expansion) then
                            aliasStore <- aliasStore |> Map.add name expansion
                            AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.AliasSet, name, expansion)) + "[/]"))
                        else
                            AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.AliasUsage + "[/]"))
                    AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/cron" || s.StartsWith "/cron " ->
                let desc = (if s = "/cron" then "" else s.Substring(5)).Trim().ToLowerInvariant()
                if String.IsNullOrWhiteSpace desc then
                    AnsiConsole.MarkupLine "[dim]Usage: /cron \"every weekday at 9am UTC\"[/]"
                else
                    // Simple NLP parser for common patterns
                    let dayNames = [| "sun"; "mon"; "tue"; "wed"; "thu"; "fri"; "sat" |]
                    let parseHour (s: string) =
                        let s = s.Replace("am", "").Replace("pm", "").Trim()
                        match System.Int32.TryParse s with
                        | true, h ->
                            if desc.Contains "pm" && h < 12 then Some (h + 12)
                            elif desc.Contains "am" && h = 12 then Some 0
                            else Some h
                        | _ -> None
                    let extractHour () =
                        let m = System.Text.RegularExpressions.Regex.Match(desc, @"\b(\d{1,2})\s*(am|pm)\b")
                        if m.Success then parseHour (m.Groups.[1].Value + m.Groups.[2].Value)
                        else
                            let m2 = System.Text.RegularExpressions.Regex.Match(desc, @"at\s+(\d{1,2})")
                            if m2.Success then parseHour m2.Groups.[1].Value else None
                    let extractMinute () =
                        let m = System.Text.RegularExpressions.Regex.Match(desc, @":(\d{2})")
                        if m.Success then match System.Int32.TryParse m.Groups.[1].Value with | true, n -> Some n | _ -> None
                        else Some 0
                    let extractN unit =
                        let m = System.Text.RegularExpressions.Regex.Match(desc, @"every\s+(\d+)\s+" + unit)
                        if m.Success then match System.Int32.TryParse m.Groups.[1].Value with | true, n -> Some n | _ -> None
                        else None
                    let cronExpr =
                        if desc.Contains "every minute" || desc = "* * * * *" then Some "* * * * *"
                        elif desc.Contains "every hour" && not (desc.Contains "every hour at") then Some "0 * * * *"
                        elif desc.Contains "every day" || desc.Contains "daily" then
                            let h = extractHour () |> Option.defaultValue 0
                            let min = extractMinute () |> Option.defaultValue 0
                            Some $"{min} {h} * * *"
                        elif desc.Contains "weekday" || desc.Contains "working day" then
                            let h = extractHour () |> Option.defaultValue 9
                            let min = extractMinute () |> Option.defaultValue 0
                            Some $"{min} {h} * * 1-5"
                        elif desc.Contains "weekend" then
                            let h = extractHour () |> Option.defaultValue 10
                            let min = extractMinute () |> Option.defaultValue 0
                            Some $"{min} {h} * * 0,6"
                        elif desc.Contains "every week" || (dayNames |> Array.exists (fun d -> desc.Contains d)) then
                            let dow = dayNames |> Array.tryFindIndex (fun d -> desc.Contains d) |> Option.defaultValue 1
                            let h = extractHour () |> Option.defaultValue 0
                            let min = extractMinute () |> Option.defaultValue 0
                            Some $"{min} {h} * * {dow}"
                        else
                            match extractN "minute" |> Option.orElse (extractN "minutes") with
                            | Some n -> Some $"*/{n} * * * *"
                            | None ->
                                match extractN "hour" |> Option.orElse (extractN "hours") with
                                | Some n -> Some $"0 */{n} * * *"
                                | None -> None
                    match cronExpr with
                    | None -> AnsiConsole.MarkupLine "[red]Could not parse cron expression. Try: \"every day at 9am\", \"every 5 minutes\", \"every weekday at 17:00\"[/]"
                    | Some expr ->
                        AnsiConsole.MarkupLine($"[bold cyan]{Markup.Escape expr}[/]")
                        // Compute next 5 fire times
                        let mutable t = DateTimeOffset.UtcNow.AddMinutes 1.0
                        let mutable count = 0
                        let parts = expr.Split(' ')
                        let minPart  = if parts.Length > 0 then parts.[0] else "*"
                        let hourPart = if parts.Length > 1 then parts.[1] else "*"
                        let dowPart  = if parts.Length > 4 then parts.[4] else "*"
                        let matchField (v: int) (field: string) =
                            if field = "*" then true
                            elif field.StartsWith "*/" then
                                match System.Int32.TryParse(field.Substring 2) with
                                | true, n -> v % n = 0
                                | _ -> true
                            elif field.Contains "-" then
                                let r = field.Split '-'
                                match System.Int32.TryParse r.[0], System.Int32.TryParse r.[1] with
                                | (true, lo), (true, hi) -> v >= lo && v <= hi
                                | _ -> true
                            elif field.Contains "," then
                                field.Split(',') |> Array.exists (fun p -> match System.Int32.TryParse p with | true, n -> n = v | _ -> false)
                            else
                                match System.Int32.TryParse field with
                                | true, n -> n = v
                                | _ -> true
                        AnsiConsole.MarkupLine "[dim]Next 5 fire times (UTC):[/]"
                        while count < 5 do
                            if matchField t.Minute minPart && matchField t.Hour hourPart && matchField (int t.DayOfWeek) dowPart then
                                let tStr = t.ToString("yyyy-MM-dd HH:mm")
                                AnsiConsole.MarkupLine($"  [dim]{tStr}[/]")
                                count <- count + 1
                            t <- t.AddMinutes 1.0
                StatusBar.refresh ()

            | Some s when s.StartsWith "/regex " ->
                let raw = s.Substring(7).Trim()
                // Parse: /regex "pattern" "input" or /regex pattern input
                let parseQuotedOrWord (t: string) =
                    let t = t.TrimStart()
                    if t.StartsWith "\"" then
                        let closing = t.IndexOf('"', 1)
                        if closing < 0 then t, ""
                        else t.[1..closing - 1], t.[closing + 1..].TrimStart()
                    else
                        let space = t.IndexOf ' '
                        if space < 0 then t, ""
                        else t.[..space - 1], t.[space..].TrimStart()
                let pattern, rest = parseQuotedOrWord raw
                let input, _     = parseQuotedOrWord rest
                if String.IsNullOrWhiteSpace pattern then
                    AnsiConsole.MarkupLine "[dim]Usage: /regex \"pattern\" \"input\"[/]"
                else
                    try
                        let rx = System.Text.RegularExpressions.Regex(pattern)
                        let matches = rx.Matches(input)
                        if matches.Count = 0 then
                            AnsiConsole.MarkupLine "[dim]No matches[/]"
                        else
                            AnsiConsole.MarkupLine($"[dim]{matches.Count} match(es) for [/][cyan]{Markup.Escape pattern}[/]")
                            for m in matches do
                                AnsiConsole.MarkupLine($"  index=[bold]{m.Index}[/] length=[bold]{m.Length}[/] value=[green]{Markup.Escape m.Value}[/]")
                                for g in rx.GetGroupNames() do
                                    if g <> "0" then
                                        let grp = m.Groups.[g]
                                        if grp.Success then
                                            AnsiConsole.MarkupLine($"    [cyan]{Markup.Escape g}[/]=[green]{Markup.Escape grp.Value}[/]")
                    with ex ->
                        AnsiConsole.MarkupLine($"[red]Regex error: {Markup.Escape ex.Message}[/]")
                StatusBar.refresh ()

            | Some s when s = "/rename" || s.StartsWith "/rename " ->
                let title = (if s = "/rename" then "" else s.Substring(7)).Trim().Trim('"').Trim('\'')
                if String.IsNullOrWhiteSpace title then
                    match sessionTitle with
                    | Some t -> AnsiConsole.MarkupLine($"[dim]Session title: [/][bold]{Markup.Escape t}[/]")
                    | None   -> AnsiConsole.MarkupLine "[dim]No session title set. Usage: /rename <title>[/]"
                else
                    sessionTitle <- Some title
                    AnsiConsole.MarkupLine($"[dim]Session renamed to: [/][bold cyan]{Markup.Escape title}[/]")
                StatusBar.refresh ()

            | Some s when s = "/env" || s.StartsWith "/env " ->
                let sub = (if s = "/env" then "list" else s.Substring(4).Trim())
                match sub with
                | "list" | "" ->
                    if sessionEnvVars.IsEmpty then
                        AnsiConsole.MarkupLine "[dim]No session env vars set.[/]"
                    else
                        AnsiConsole.MarkupLine "[dim]Session env vars:[/]"
                        for name in sessionEnvVars do
                            let v = Environment.GetEnvironmentVariable name |> Option.ofObj |> Option.defaultValue ""
                            AnsiConsole.MarkupLine($"  [cyan]{Markup.Escape name}[/]=[green]{Markup.Escape v}[/]")
                | cmd when cmd.StartsWith "set " ->
                    let pair = cmd.Substring(4).Trim()
                    let eqIdx = pair.IndexOf '='
                    if eqIdx <= 0 then
                        AnsiConsole.MarkupLine "[red]Usage: /env set NAME=value[/]"
                    else
                        let name  = pair.[..eqIdx - 1].Trim()
                        let value = pair.[eqIdx + 1..]
                        Environment.SetEnvironmentVariable(name, value)
                        sessionEnvVars <- sessionEnvVars |> Set.add name
                        AnsiConsole.MarkupLine($"[dim]Set [cyan]{Markup.Escape name}[/]=[green]{Markup.Escape value}[/][/]")
                | cmd when cmd.StartsWith "unset " ->
                    let name = cmd.Substring(6).Trim()
                    Environment.SetEnvironmentVariable(name, null)
                    sessionEnvVars <- sessionEnvVars |> Set.remove name
                    AnsiConsole.MarkupLine($"[dim]Unset [cyan]{Markup.Escape name}[/][/]")
                | "load" ->
                    let dotEnvPath = System.IO.Path.Combine(cwd, ".env")
                    if not (System.IO.File.Exists dotEnvPath) then
                        AnsiConsole.MarkupLine "[dim yellow].env not found in current directory.[/]"
                    else
                        let mutable loaded = 0
                        for line in System.IO.File.ReadAllLines dotEnvPath do
                            let l = line.Trim()
                            if not (l.StartsWith '#') && l.Contains '=' then
                                let eq = l.IndexOf '='
                                let name  = l.[..eq - 1].Trim()
                                let value = l.[eq + 1..].Trim().Trim('"', '\'')
                                if name.Length > 0 then
                                    Environment.SetEnvironmentVariable(name, value)
                                    sessionEnvVars <- sessionEnvVars |> Set.add name
                                    loaded <- loaded + 1
                        AnsiConsole.MarkupLine($"[dim]Loaded {loaded} vars from .env[/]")
                | other ->
                    AnsiConsole.MarkupLine($"[red]Unknown /env subcommand: {Markup.Escape other}. Use: /env list | /env load | /env set NAME=value | /env unset NAME[/]")
                StatusBar.refresh ()

            | Some s when s.StartsWith "/scratch" ->
                let sub = s.Substring("/scratch".Length).Trim()
                match sub with
                | "send" when not scratchLines.IsEmpty ->
                    let text = scratchLines |> String.concat "\n"
                    scratchLines <- []
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.ScratchSent + "[/]"))
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session text cfg cancelSrc zenMode
                | "send" ->
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.ScratchEmpty + "[/]"))
                    AnsiConsole.WriteLine()
                | "clear" ->
                    scratchLines <- []
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.ScratchCleared + "[/]"))
                    AnsiConsole.WriteLine()
                | "show" ->
                    if scratchLines.IsEmpty then
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.ScratchEmpty + "[/]"))
                    else
                        AnsiConsole.Write(Markup("[bold]" + Markup.Escape liveStrings.ScratchShowHeader + "[/]"))
                        AnsiConsole.WriteLine()
                        for line in scratchLines do
                            AnsiConsole.Write(Markup("[dim]  " + Markup.Escape line + "[/]"))
                            AnsiConsole.WriteLine()
                    AnsiConsole.WriteLine()
                | text when not (String.IsNullOrWhiteSpace text) ->
                    scratchLines <- scratchLines @ [text]
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.ScratchAppended, scratchLines.Length)) + "[/]"))
                    AnsiConsole.WriteLine()
                | _ ->
                    // /scratch alone with no subcommand: show usage
                    AnsiConsole.Write(Markup("[dim]usage: /scratch <text> | /scratch send | /scratch show | /scratch clear[/]"))
                    AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/watches" ->
                let ws = FileWatcher.list ()
                if ws.IsEmpty then
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.WatchNone + "[/]"))
                    AnsiConsole.WriteLine()
                else
                    for (path, cmd) in ws do
                        AnsiConsole.Write(Markup($"  [cyan]{Markup.Escape path}[/] → [dim]{Markup.Escape cmd}[/]"))
                        AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/unwatch " ->
                let path = s.Substring("/unwatch ".Length).Trim()
                let absPath = System.IO.Path.GetFullPath(path)
                FileWatcher.remove absPath
                AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.WatchRemoved, path)) + "[/]"))
                AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/watch " ->
                let rest = s.Substring("/watch ".Length).Trim()
                let spaceIdx = rest.IndexOf ' '
                if spaceIdx <= 0 then
                    AnsiConsole.Write(Markup("[dim]" + Markup.Escape liveStrings.WatchUsage + "[/]"))
                    AnsiConsole.WriteLine()
                else
                    let path = rest.Substring(0, spaceIdx).Trim()
                    let cmd  = rest.Substring(spaceIdx + 1).Trim()
                    let absPath = System.IO.Path.GetFullPath(path)
                    if not (System.IO.File.Exists absPath) then
                        AnsiConsole.Write(Markup($"[dim]{Markup.Escape (System.String.Format(liveStrings.AtFileNotFound, path))}[/]"))
                        AnsiConsole.WriteLine()
                    else
                        FileWatcher.add absPath cmd
                        AnsiConsole.Write(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.WatchAdded, path, cmd)) + "[/]"))
                        AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/note " ->
                let text = s.Substring("/note ".Length).Trim()
                if not (String.IsNullOrWhiteSpace text) then
                    sessionNotes <- sessionNotes @ [(turnNumber, text)]
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape liveStrings.NoteAdded}[/]")
                    AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/notes" ->
                if sessionNotes.IsEmpty then
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape liveStrings.NoteNone}[/]")
                else
                    for (turn, text) in sessionNotes do
                        if Render.isColorEnabled () then
                            AnsiConsole.MarkupLine($"[dim][[turn {turn}]][/] {Markup.Escape text}")
                        else
                            Console.Out.WriteLine($"[turn {turn}] {text}")
                AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/undo" ->
                match Fugue.Core.Checkpoint.undo () with
                | Fugue.Core.Checkpoint.NothingToUndo ->
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape liveStrings.UndoNothingToUndo}[/]")
                | Fugue.Core.Checkpoint.Restored path ->
                    AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape liveStrings.UndoRestored}: [dim]{Markup.Escape path}[/]")
                | Fugue.Core.Checkpoint.Deleted path ->
                    AnsiConsole.MarkupLine($"[yellow]✓[/] {Markup.Escape liveStrings.UndoDeleted}: [dim]{Markup.Escape path}[/]")
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
                    AnsiConsole.Write(Render.errorLine liveStrings "rg and grep unavailable")
                    AnsiConsole.WriteLine()
                | Some "" ->
                    AnsiConsole.MarkupLine("[dim]" + Markup.Escape liveStrings.TodoNone + "[/]")
                    AnsiConsole.WriteLine()
                | Some output ->
                    let lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    let truncated, note =
                        if lines.Length > 200 then
                            Array.take 200 lines, $"\n\n[truncated — showing 200 of {lines.Length} matches]"
                        else lines, ""
                    let body = String.concat "\n" truncated
                    let summary =
                        $"Found {lines.Length} TODO/FIXME/HACK items in workspace:\n\n{body}{note}\n\nPlease review these and suggest which ones are worth addressing now."
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
                        AnsiConsole.MarkupLine($"[dim]{Markup.Escape liveStrings.FindNoResults}[/]")
                    else
                        for (_score, rel, snippet) in results do
                            let snipStr =
                                match snippet with
                                | Some s -> $"  → {s.Trim()}"
                                | None   -> ""
                            if Render.isColorEnabled () then
                                AnsiConsole.MarkupLine($"[cyan]{Markup.Escape rel}[/]{Markup.Escape snipStr}")
                            else
                                Console.Out.WriteLine($"{rel}{snipStr}")
                        AnsiConsole.MarkupLine($"[dim]{Markup.Escape liveStrings.FindInjectHint}[/]")
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
                            $"Please summarize the directory '{rawArg}'.\n\nProvide a structured summary:\n1. Purpose — what does this module/package do?\n2. Public API — key functions, types, or entry points\n3. Key dependencies — what does it depend on?\n4. Notable design decisions — any interesting patterns or constraints\n\nBe concise (2–4 sentences per section). Use the Read and Glob tools to explore the files."
                        elif System.IO.File.Exists targetPath then
                            $"Please summarize the file '{rawArg}'.\n\nProvide a structured summary:\n1. Purpose — what does this file do?\n2. Public API — exported functions or types\n3. Key dependencies — what modules/libraries does it use?\n4. Notable design decisions — any interesting patterns or constraints\n\nBe concise (2–4 sentences per section). Use the Read tool to examine the file."
                        else
                            $"The path '{rawArg}' does not exist. Please let me know."
                    AnsiConsole.Write(Render.userMessage cfg.Ui ("/summarize " + rawArg) 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session summarizePrompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/breaking-changes " || s = "/breaking-changes" ->
                // /breaking-changes <old-file> <new-file> — scan for breaking changes
                let arg = if s = "/breaking-changes" then "" else s.Substring("/breaking-changes ".Length).Trim()
                if arg = "" then
                    AnsiConsole.Write(Markup "[dim]Usage: /breaking-changes <old-signature-file-or-symbol> <new-version>[/]")
                    AnsiConsole.WriteLine()
                else
                    let prompt =
                        $"Analyse the following for breaking API changes: \"{arg}\"\n\nCheck:\n1. Renamed or removed public functions, types, or module members.\n2. Changed parameter types or return types.\n3. Added required parameters (non-optional).\n4. Changed DU cases that callers must pattern-match on.\n5. Removed interface members.\n\nFor each breaking change:\n- Quote the old signature vs new signature.\n- List all known call sites (use Grep tool).\n- Suggest a deprecation path (default argument, type alias, or wrapper).\n\nUse Read/Grep tools to discover call sites."
                    AnsiConsole.Write(Render.userMessage cfg.Ui "/breaking-changes" 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/idiomatic " || s = "/idiomatic" ->
                // /idiomatic [file] — post-generation idiomatic rewrite suggestion
                let arg = if s = "/idiomatic" then "" else s.Substring("/idiomatic ".Length).Trim()
                if arg = "" then
                    AnsiConsole.Write(Markup "[dim]Usage: /idiomatic <file>[/]")
                    AnsiConsole.WriteLine()
                else
                    let prompt =
                        $"Review \"{arg}\" for idiomatic style improvements.\n\nFor F#: flag non-idiomatic patterns and suggest pipe-based, DU-based, or computation-expression rewrites.\nFor Elixir: flag imperative loops (use Enum/Stream pipelines instead) and missing pattern match clauses.\nFor Kotlin: flag nullable handling that should use `?.let`/`?:` and unnecessary `!!`.\nFor TypeScript: flag mutation where `const` + spread is preferred and callback nesting that should be Promise chains.\n\nFor each suggestion: quote the original, show the idiomatic alternative, and explain why it's preferred.\n\nRead the file first, then provide structured feedback."
                    AnsiConsole.Write(Render.userMessage cfg.Ui $"/idiomatic {arg}" 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/incremental " || s = "/incremental" ->
                // /incremental <description> — generate only the new function and Edit it in place
                let desc = if s = "/incremental" then "" else s.Substring("/incremental ".Length).Trim()
                if desc = "" then
                    AnsiConsole.Write(Markup "[dim]Usage: /incremental <description of what to add>[/]")
                    AnsiConsole.WriteLine()
                else
                    let prompt =
                        $"Add the following functionality incrementally to the existing codebase: \"{desc}\"\n\nRules:\n1. Generate ONLY the new function(s) or type(s) — no file rewrites.\n2. Find the best insertion point using Read and Grep tools.\n3. Apply changes using the Edit tool to insert at the exact location.\n4. Do not modify existing functions unless absolutely required.\n5. Ensure the new code compiles by running `dotnet build --no-restore` after editing."
                    AnsiConsole.Write(Render.userMessage cfg.Ui "/incremental" 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/port " || s = "/port" ->
                // /port <file> [target-lang] — idiomatic cross-language code translation
                let arg = if s = "/port" then "" else s.Substring("/port ".Length).Trim()
                if arg = "" then
                    AnsiConsole.Write(Markup "[dim]Usage: /port <file> [target-language][/]")
                    AnsiConsole.WriteLine()
                else
                    let parts  = arg.Split(' ') |> Array.toList
                    let file   = parts.Head
                    let target = if parts.Length > 1 then String.concat " " parts.Tail else ""
                    let fullPath = if IO.Path.IsPathRooted file then file else IO.Path.GetFullPath(IO.Path.Combine(cwd, file))
                    let (code, targetLang) =
                        if IO.File.Exists fullPath then IO.File.ReadAllText fullPath, (if target = "" then "F#" else target)
                        else file, (if target = "" then "F#" else target)
                    let prompt =
                        $"Port the following code to idiomatic {targetLang}:\n\n```\n{code}\n```\n\nRequirements:\n1. Use the target language's idiomatic patterns — not a literal transliteration.\n2. For OOP→FP: use DUs/sealed traits, immutable records, pattern matching.\n3. For FP→OOP: map to classes, interfaces, exception handling.\n4. Annotate each non-obvious translation decision with a brief inline comment.\n5. Return the full translated file ready to use."
                    AnsiConsole.Write(Render.userMessage cfg.Ui $"/port {file} → {targetLang}" 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/scaffold " && not (s.StartsWith "/scaffold du") && not (s.StartsWith "/scaffold cqrs") && not (s.StartsWith "/scaffold actor") ->
                // /scaffold [type] <name> — language-aware project bootstrapping
                let arg = s.Substring("/scaffold ".Length).Trim()
                if arg = "" then
                    AnsiConsole.Write(Markup "[dim]Usage: /scaffold <type> <name>  (e.g. /scaffold service UserService)[/]")
                    AnsiConsole.WriteLine()
                else
                    let prompt =
                        $"Generate a project scaffold for \"{arg}\".\n\nInspect the project structure first (use Glob/Read tools) to understand:\n1. Language and framework in use.\n2. Naming conventions, folder layout, and existing patterns.\n3. Test framework and tooling.\n\nThen generate the scaffold files following project conventions. Include:\n- Main implementation file(s)\n- Unit test skeleton\n- Any registration/wiring needed in existing entry points\n\nWrite each file using the Write tool."
                    AnsiConsole.Write(Render.userMessage cfg.Ui $"/scaffold {arg}" 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s = "/complexity" || s.StartsWith "/complexity " ->
                // /complexity [file] — cyclomatic complexity analysis
                let arg = if s = "/complexity" then "" else s.Substring("/complexity ".Length).Trim()
                let target = if arg = "" then "." else arg
                let prompt =
                    $"Analyse cyclomatic complexity of the code in \"{target}\".\n\nFor each function/method:\n1. Compute approximate cyclomatic complexity (count decision points: if/match/loop/exception handlers).\n2. Flag functions with complexity > 10 as high-risk.\n3. Suggest refactorings for the top 3 most complex functions (extract method, simplify conditions, etc.).\n\nPresent results as a table: Function | Complexity | Risk | Refactoring suggestion.\n\nUse Glob and Read tools to discover and examine the source files."
                AnsiConsole.Write(Render.userMessage cfg.Ui $"/complexity {target}" 0)
                AnsiConsole.WriteLine()
                do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/check tail-rec " || s = "/check tail-rec" ->
                // /check tail-rec [file] — detect non-tail-recursive functions and offer rewrites
                let arg = if s = "/check tail-rec" then "" else s.Substring("/check tail-rec ".Length).Trim()
                let (code, label) =
                    if arg = "" then "", ""
                    else
                        let fullPath = if IO.Path.IsPathRooted arg then arg else IO.Path.GetFullPath(IO.Path.Combine(cwd, arg))
                        if IO.File.Exists fullPath then IO.File.ReadAllText fullPath, arg else arg, arg
                if code = "" then
                    AnsiConsole.Write(Markup "[dim]Usage: /check tail-rec <file>[/]")
                    AnsiConsole.WriteLine()
                else
                    let prompt =
                        $"Analyse the following F# code for tail-recursion correctness:\n\n```fsharp\n{code}\n```\n\nFor each recursive function:\n1. Determine whether it is tail-recursive (the recursive call is the last operation in all branches).\n2. If NOT tail-recursive, explain which call site is the problem and why it would stack-overflow on large inputs.\n3. Offer a rewrite using:\n   - Accumulator pattern (preferred)\n   - Continuation-passing style (CPS) if accumulator doesn't fit\n   - `[<TailCall>]` attribute hint where F# can optimise\n4. Show the rewritten function alongside the original.\n\nSource: {label}"
                    AnsiConsole.Write(Render.userMessage cfg.Ui $"/check tail-rec {label}" 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/check effects " || s = "/check effects" ->
                // /check effects [file] — verify ZIO/Cats Effect type consistency
                let arg = if s = "/check effects" then "" else s.Substring("/check effects ".Length).Trim()
                let (code, label) =
                    if arg = "" then "", ""
                    else
                        let fullPath = if IO.Path.IsPathRooted arg then arg else IO.Path.GetFullPath(IO.Path.Combine(cwd, arg))
                        if IO.File.Exists fullPath then IO.File.ReadAllText fullPath, arg else arg, arg
                if code = "" then
                    AnsiConsole.Write(Markup "[dim]Usage: /check effects <file>[/]")
                    AnsiConsole.WriteLine()
                else
                    let prompt =
                        $"Review the following code for effect system type consistency:\n\n```\n{code}\n```\n\nCheck:\n1. Are all effectful operations wrapped in the same effect type (IO/ZIO/Task/F# Async) consistently?\n2. Are effects mixed (e.g. ZIO and Future in the same for-comprehension) without proper lifting?\n3. Are error channels compatible across composed effects?\n4. Are any side effects performed outside the effect system (unsafe I/O, mutable state leaks)?\n\nFor each issue: show the problematic code, explain the violation, and provide a corrected version.\n\nSource: {label}"
                    AnsiConsole.Write(Render.userMessage cfg.Ui $"/check effects {label}" 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/translate comments " || s = "/translate comments" ->
                // /translate comments [file] — translate code comments to English
                let arg = if s = "/translate comments" then "" else s.Substring("/translate comments ".Length).Trim()
                if arg = "" then
                    AnsiConsole.Write(Markup "[dim]Usage: /translate comments <file>[/]")
                    AnsiConsole.WriteLine()
                else
                    let prompt =
                        $"Translate all code comments in the file \"{arg}\" to English.\n\nRules:\n1. Translate only comment text — do NOT touch identifiers, string literals, or code.\n2. Preserve comment style (// vs /// vs /* */).\n3. Keep the original meaning precisely; prefer clarity over literal translation.\n4. Apply the translations using the Edit tool (one edit per comment block or file section).\n\nRead the file first, then apply targeted edits."
                    AnsiConsole.Write(Render.userMessage cfg.Ui $"/translate comments {arg}" 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/derive codec " || s = "/derive codec" ->
                let typeName = if s = "/derive codec" then "" else s.Substring("/derive codec ".Length).Trim()
                if typeName = "" then
                    AnsiConsole.Write(Markup "[dim]Usage: /derive codec <TypeName>[/]")
                    AnsiConsole.WriteLine()
                else
                    let prompt =
                        $"Generate an AOT-safe JSON codec for the F# type \"{typeName}\".\n\n1. Read the type definition from the codebase (use the Read or Grep tools).\n2. Generate a `[<JsonSerializable>]` `JsonSerializerContext` subclass covering the type and any nested types.\n3. Add `[<JsonPropertyName>]` attributes for all fields to ensure stable serialization.\n4. If the type is a DU, add `[<JsonPolymorphic>]` and `[<JsonDerivedType>]` for each case.\n5. Show a usage example: `JsonSerializer.Serialize(value, MyContext.Default.MyType)`.\n\nTarget: System.Text.Json source generation, .NET 10, Native AOT compatible."
                    AnsiConsole.Write(Render.userMessage cfg.Ui $"/derive codec {typeName}" 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/scaffold cqrs " || s = "/scaffold cqrs" ->
                let cmdName = if s = "/scaffold cqrs" then "" else s.Substring("/scaffold cqrs ".Length).Trim()
                if cmdName = "" then
                    AnsiConsole.Write(Markup "[dim]Usage: /scaffold cqrs <CommandName>[/]")
                    AnsiConsole.WriteLine()
                else
                    let prompt =
                        $"Generate a complete CQRS slice for \"{cmdName}\".\n\nInclude:\n1. **Command record** — immutable, validated constructor.\n2. **CommandHandler** — reads from the command, applies business logic, emits an event.\n3. **Domain Event** — added as a new DU case (or new type).\n4. **EventHandler** — updates read model or side-effects.\n5. **xUnit test skeleton** — arrange/act/assert for the happy path and one error case.\n\nFollow the naming and layering conventions visible in the existing codebase (use Glob/Read tools to discover them). Return separate fenced code blocks per file."
                    AnsiConsole.Write(Render.userMessage cfg.Ui $"/scaffold cqrs {cmdName}" 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/scaffold actor " || s = "/scaffold actor" ->
                let actorName = if s = "/scaffold actor" then "" else s.Substring("/scaffold actor ".Length).Trim()
                if actorName = "" then
                    AnsiConsole.Write(Markup "[dim]Usage: /scaffold actor <ActorName> [brief responsibility description][/]")
                    AnsiConsole.WriteLine()
                else
                    let prompt =
                        $"Generate a typed actor stub for \"{actorName}\".\n\nFor F# / Proto.Actor:\n1. Command DU with all message types.\n2. `Actor` class implementing `IActor` with a `ReceiveAsync` dispatch.\n3. Supervision strategy.\n4. Props factory function.\n\nFor Scala / Akka Typed:\n1. `Command` sealed trait hierarchy.\n2. `Behavior[Command]` with `Behaviors.setup`.\n3. `SupervisorStrategy` using `Behaviors.supervise`.\n\nInfer the framework from the project (use Glob/Read to check dependencies). Return separate fenced blocks per file."
                    AnsiConsole.Write(Render.userMessage cfg.Ui $"/scaffold actor {actorName}" 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/scaffold du " || s = "/scaffold du" ->
                // /scaffold du <concept> — generate F# DU / Scala sealed trait with match examples
                let concept = if s = "/scaffold du" then "" else s.Substring("/scaffold du ".Length).Trim()
                if concept = "" then
                    AnsiConsole.Write(Markup "[dim]Usage: /scaffold du <concept name>[/]")
                    AnsiConsole.WriteLine()
                else
                    let prompt =
                        $"Generate a complete F# discriminated union for the concept \"{concept}\".\n\nInclude:\n1. The DU definition with well-named cases and meaningful data payloads.\n2. An exhaustive `match` example covering every case.\n3. Smart constructor helpers (active patterns or module functions) for common construction patterns.\n4. `[<JsonDerivedType>]` / `[<JsonPolymorphic>]` attributes for AOT-safe System.Text.Json serialization.\n5. A brief comment per case explaining its semantics.\n\nTarget: F# 9 / .NET 10, Native AOT compatible."
                    AnsiConsole.Write(Render.userMessage cfg.Ui $"/scaffold du {concept}" 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/monad " || s = "/monad" ->
                // /monad <description> — generate F# CE / Scala for-comprehension
                let desc = if s = "/monad" then "" else s.Substring("/monad ".Length).Trim()
                if desc = "" then
                    AnsiConsole.Write(Markup "[dim]Usage: /monad <imperative description or pseudocode>[/]")
                    AnsiConsole.WriteLine()
                else
                    let prompt =
                        $"Convert the following imperative description into idiomatic monadic F# code:\n\n\"{desc}\"\n\nSteps:\n1. Identify the primary effect (async I/O, error handling, sequence generation, cancellation, …).\n2. Choose the correct F# computation expression (`task`, `async`, `result`, `asyncResult`, `seq`, …).\n3. Generate the CE block — no nested `match`/`if` for error handling; use `let!` and `return`.\n4. If the operation mixes effects (e.g. async + Result), compose them correctly (e.g. `taskResult`).\n5. Include the type signatures.\n\nReturn only the code with a one-line comment on the CE chosen."
                    AnsiConsole.Write(Render.userMessage cfg.Ui "/monad" 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/migrate oop-to-fp " || s = "/migrate oop-to-fp" ->
                // /migrate oop-to-fp [file] — convert C#/Java class hierarchy to F# DUs
                let arg = if s = "/migrate oop-to-fp" then "" else s.Substring("/migrate oop-to-fp ".Length).Trim()
                if arg = "" then
                    AnsiConsole.Write(Markup "[dim]Usage: /migrate oop-to-fp <file-or-paste-code>[/]")
                    AnsiConsole.WriteLine()
                else
                    let code =
                        let fullPath = if IO.Path.IsPathRooted arg then arg else IO.Path.GetFullPath(IO.Path.Combine(cwd, arg))
                        if IO.File.Exists fullPath then IO.File.ReadAllText fullPath else arg
                    let prompt =
                        $"Migrate the following OOP code to idiomatic F#:\n\n```\n{code}\n```\n\nMigration rules:\n1. `abstract class` / `interface` → F# discriminated union (one case per concrete class).\n2. Mutable fields → immutable record fields; setters → `with` copy-and-update expressions.\n3. `null` returns → `Option<T>`; `throw` → `Result<T, Error>` or DU error case.\n4. Virtual method dispatch → exhaustive `match` on the DU.\n5. Constructor logic → module-level factory functions.\n6. Preserve all business logic exactly.\n\nReturn the complete F# translation with brief migration notes per section."
                    AnsiConsole.Write(Render.userMessage cfg.Ui $"/migrate oop-to-fp {arg}" 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/rop " || s = "/rop" ->
                // /rop <description> — generate a railway-oriented Result/Either chain
                let desc = if s = "/rop" then "" else s.Substring(5).Trim()
                if desc = "" then
                    AnsiConsole.Write(Markup "[dim]Usage: /rop <plain-English description of multi-step operation>[/]"  )
                    AnsiConsole.WriteLine()
                else
                    let prompt =
                        $"Generate a railway-oriented programming (ROP) pipeline for the following operation:\n\n\"{desc}\"\n\nRequirements:\n1. Use F# `Result<'ok, 'err>` types (or adapt for the target language).\n2. Define a discriminated union for all possible error cases.\n3. Compose steps with `Result.bind` (or `>>=`) — no exceptions, no `try/catch`.\n4. Unify error types across steps; if types differ, map to the common error DU.\n5. Include brief inline comments explaining each step.\n6. Return only the code (no prose preamble)."
                    AnsiConsole.Write(Render.userMessage cfg.Ui $"/rop {desc}" 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/infer-type " || s = "/infer-type" ->
                // /infer-type <expression> — explain the inferred F# type
                let expr = if s = "/infer-type" then "" else s.Substring("/infer-type ".Length).Trim()
                if expr = "" then
                    AnsiConsole.Write(Markup "[dim]Usage: /infer-type <F# expression or snippet>[/]")
                    AnsiConsole.WriteLine()
                else
                    let prompt =
                        $"Infer and explain the type of the following F# expression:\n\n```fsharp\n{expr}\n```\n\nFormat your answer as:\n1. **Type signature** — in standard F# notation.\n2. **Parameter breakdown** — explain each type parameter, constraint, or variance annotation.\n3. **Plain-English summary** — one or two sentences explaining what this type means semantically.\n4. **Example usage** — a single concrete example showing it in action."
                    AnsiConsole.Write(Render.userMessage cfg.Ui "/infer-type" 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/pointfree " || s = "/pointfree" ->
                // /pointfree <function-definition> — offer point-free rewrite
                let fn = if s = "/pointfree" then "" else s.Substring("/pointfree ".Length).Trim()
                if fn = "" then
                    AnsiConsole.Write(Markup "[dim]Usage: /pointfree <F# function definition>[/]")
                    AnsiConsole.WriteLine()
                else
                    let prompt =
                        $"Offer a point-free rewrite of the following F# function:\n\n```fsharp\n{fn}\n```\n\nFormat your answer as:\n1. **Original** — repeat the function as-is.\n2. **Point-free equivalent** — the rewritten version using partial application, `>>` / `<<`, or combinators.\n3. **Transformation steps** — explain each step of the rewrite.\n4. **Readability verdict** — note whether the point-free form is actually clearer, and when to prefer the explicit form.\n\nIf a point-free form is not natural or readable, say so and explain why."
                    AnsiConsole.Write(Render.userMessage cfg.Ui "/pointfree" 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s.StartsWith "/refactor pipeline" ->
                // /refactor pipeline [file:start-end] — rewrite let-bindings as |> pipe chain
                let arg = s.Substring("/refactor pipeline".Length).Trim()
                let code, label =
                    if arg = "" then "", ""
                    else
                        // Parse file:start-end
                        let colonIdx = arg.LastIndexOf ':'
                        if colonIdx > 0 then
                            let filePart  = arg.[..colonIdx-1]
                            let rangePart = arg.[colonIdx+1..]
                            let fullPath  = if IO.Path.IsPathRooted filePart then filePart else IO.Path.GetFullPath(IO.Path.Combine(cwd, filePart))
                            if IO.File.Exists fullPath then
                                let lines  = IO.File.ReadAllLines fullPath
                                let parsed =
                                    let parts = rangePart.Split '-'
                                    if parts.Length = 2 then
                                        match System.Int32.TryParse parts.[0], System.Int32.TryParse parts.[1] with
                                        | (true, s), (true, e) -> Some (max 0 (s-1), min (lines.Length-1) (e-1))
                                        | _ -> None
                                    else None
                                match parsed with
                                | Some (s, e) -> String.concat "\n" lines.[s..e], $"{filePart}:{s+1}-{e+1}"
                                | None        -> String.concat "\n" lines, filePart
                            else "", ""
                        else
                            let fullPath = if IO.Path.IsPathRooted arg then arg else IO.Path.GetFullPath(IO.Path.Combine(cwd, arg))
                            if IO.File.Exists fullPath then IO.File.ReadAllText fullPath, arg
                            else "", ""
                if code.Trim() = "" then
                    AnsiConsole.Write(Markup("[dim]Usage: /refactor pipeline [file:start-end][/]"))
                    AnsiConsole.WriteLine()
                else
                    let prompt =
                        $"Refactor the following F# code into an idiomatic pipe chain (`|>`).\n\nRules:\n1. Identify a linear data-flow sequence where each binding feeds into the next.\n2. Replace the sequence with a single `|>` chain.\n3. Use partial application where possible; inline lambdas only when necessary.\n4. Preserve all semantics exactly — no behaviour changes.\n5. Return only the refactored code block.\n\nSource ({label}):\n```fsharp\n{code}\n```"
                    AnsiConsole.Write(Render.userMessage cfg.Ui $"/refactor pipeline {label}" 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s = "/check exhaustive" || s.StartsWith "/check exhaustive " ->
                // Run dotnet build and feed FS0025 (incomplete pattern match) warnings back
                let arg = if s = "/check exhaustive" then "." else s.Substring("/check exhaustive ".Length).Trim()
                let buildDir = if arg = "" || arg = "." then cwd else if IO.Path.IsPathRooted arg then arg else IO.Path.Combine(cwd, arg)
                AnsiConsole.MarkupLine "[dim]Running dotnet build to check pattern match exhaustiveness…[/]"
                let psi = System.Diagnostics.ProcessStartInfo()
                psi.FileName  <- "dotnet"
                psi.Arguments <- "build --no-restore -warnaserror- /p:TreatWarningsAsErrors=false 2>&1"
                psi.WorkingDirectory    <- buildDir
                psi.UseShellExecute     <- false
                psi.RedirectStandardOutput <- true
                psi.RedirectStandardError  <- true
                use proc = new System.Diagnostics.Process()
                proc.StartInfo <- psi
                proc.Start() |> ignore
                let out = proc.StandardOutput.ReadToEnd()
                let err = proc.StandardError.ReadToEnd()
                proc.WaitForExit()
                let combined = out + "\n" + err
                let fs0025 =
                    combined.Split('\n')
                    |> Array.filter (fun l -> l.Contains "FS0025" || l.Contains "incomplete pattern")
                    |> Array.toList
                if fs0025.IsEmpty then
                    AnsiConsole.MarkupLine "[green]No FS0025 incomplete pattern match warnings found.[/]"
                    AnsiConsole.WriteLine()
                else
                    let diagnostics = String.concat "\n" fs0025
                    let prompt =
                        $"The build found incomplete pattern match warnings (FS0025):\n\n```\n{diagnostics}\n```\n\nPlease:\n1. Read each affected file.\n2. Add the missing match cases using `failwith \"TODO: <CaseName>\"` as placeholders.\n3. Apply the fixes using the Edit tool.\n\nFix all warnings before responding."
                    AnsiConsole.Write(Render.userMessage cfg.Ui "/check exhaustive" 0)
                    AnsiConsole.WriteLine()
                    do! streamAndRender agent session prompt cfg cancelSrc zenMode
                StatusBar.refresh ()
            | Some s when s = "/context show" || s.StartsWith "/context show" ->
                let wordsToTokens (text: string) =
                    let words = text.Split([|' '; '\n'; '\r'; '\t'|], StringSplitOptions.RemoveEmptyEntries).Length
                    int (float words * 1.35)
                let sysTok =
                    match cfg.SystemPrompt with
                    | Some sp -> wordsToTokens sp
                    | None    -> 0
                let contextLimit = 200_000
                let pctUsed = int (float sysTok / float contextLimit * 100.0)
                if Render.isColorEnabled () then
                    AnsiConsole.MarkupLine "[bold]Context budget estimate[/] [dim](word-count based, ×1.35)[/]"
                    AnsiConsole.MarkupLine($"  [dim]system prompt[/]  ~{sysTok} tokens")
                    AnsiConsole.MarkupLine($"  [dim]context limit[/]   {contextLimit} tokens (provider estimate)")
                    AnsiConsole.MarkupLine($"  [dim]system usage[/]   {pctUsed}%% of limit")
                    AnsiConsole.MarkupLine "[dim]  (turn history token count requires provider usage reporting)[/]"
                else
                    Console.Out.WriteLine($"System prompt: ~{sysTok} tokens / {contextLimit} limit ({pctUsed}%%)")
                AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/tokens" ->
                let wordsToTokens (text: string) =
                    text.Split([|' '; '\n'; '\r'; '\t'|], StringSplitOptions.RemoveEmptyEntries).Length
                    |> fun w -> int (float w * 1.35)
                let sysTok =
                    match cfg.SystemPrompt with
                    | Some sp -> wordsToTokens sp
                    | None    -> 0
                let prov, model =
                    match cfg.Provider with
                    | Fugue.Core.Config.Anthropic(_, m) -> "anthropic", m
                    | Fugue.Core.Config.OpenAI(_, m)    -> "openai", m
                    | Fugue.Core.Config.Ollama(_, m)    -> "ollama", m
                let friendly = Fugue.Core.Config.modelDisplayName model
                if Render.isColorEnabled () then
                    AnsiConsole.MarkupLine($"[bold]/tokens[/] — provider: [cyan]{prov}[/]  model: [cyan]{friendly}[/]")
                    AnsiConsole.MarkupLine($"  system prompt:  ~{sysTok} tokens")
                    AnsiConsole.MarkupLine "[dim]  Exact per-turn counts require the provider to return usage metadata.[/]"
                    AnsiConsole.MarkupLine "[dim]  Use /context show for a full budget breakdown.[/]"
                else
                    Console.Out.WriteLine($"Provider: {prov}  Model: {friendly}  System: ~{sysTok} tokens")
                AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/locale " || s = "/locale" ->
                let arg = if s = "/locale" then "" else s.Substring("/locale ".Length).Trim()
                if arg = "" then
                    let current = savedUi.Locale
                    if Render.isColorEnabled () then
                        AnsiConsole.MarkupLine($"[dim]Current locale: [cyan]{current}[/]  Usage: /locale [en|ru][/]")
                    else
                        Console.Out.WriteLine($"Current locale: {current}  Usage: /locale [en|ru]")
                elif arg <> "en" && arg <> "ru" then
                    if Render.isColorEnabled () then
                        AnsiConsole.MarkupLine "[dim yellow]Supported locales: en, ru[/]"
                    else
                        Console.Out.WriteLine "Supported locales: en, ru"
                else
                    savedUi <- { savedUi with Locale = arg }
                    liveStrings <- Fugue.Core.Localization.pick arg
                    try
                        Config.saveToFile { cfg with Ui = savedUi }
                        if Render.isColorEnabled () then
                            AnsiConsole.MarkupLine($"[dim green]Locale set to [cyan]{arg}[/] and saved.[/]")
                        else
                            Console.Out.WriteLine($"Locale set to {arg} and saved.")
                    with ex ->
                        if Render.isColorEnabled () then
                            AnsiConsole.MarkupLine($"[dim yellow]Could not save config: {Markup.Escape ex.Message}[/]")
                        else
                            Console.Out.WriteLine($"Could not save config: {ex.Message}")
                AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/pin " ->
                let msg = s.Substring("/pin ".Length).Trim()
                if msg = "" then
                    if pinnedMessages.IsEmpty then
                        AnsiConsole.MarkupLine "[dim]No pinned messages. Usage: /pin <message>[/]"
                    else
                        AnsiConsole.MarkupLine "[bold]Pinned:[/]"
                        pinnedMessages |> List.iteri (fun i m ->
                            if Render.isColorEnabled () then AnsiConsole.MarkupLine($"  [cyan]{i+1}.[/] {Markup.Escape m}")
                            else Console.Out.WriteLine($"  {i+1}. {m}"))
                else
                    pinnedMessages <- pinnedMessages @ [msg]
                    if Render.isColorEnabled () then
                        AnsiConsole.MarkupLine($"[dim green]Pinned ({pinnedMessages.Length} total): [/]{Markup.Escape msg}")
                    else
                        Console.Out.WriteLine($"Pinned ({pinnedMessages.Length} total): {msg}")
                AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/pin" ->
                if pinnedMessages.IsEmpty then
                    AnsiConsole.MarkupLine "[dim]No pinned messages. Usage: /pin <message>[/]"
                else
                    AnsiConsole.MarkupLine "[bold]Pinned messages:[/]"
                    pinnedMessages |> List.iteri (fun i m ->
                        if Render.isColorEnabled () then AnsiConsole.MarkupLine($"  [cyan]{i+1}.[/] {Markup.Escape m}")
                        else Console.Out.WriteLine($"  {i+1}. {m}"))
                AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some s when s = "/compress" ->
                // Compress context: ask AI to summarise history, then reset session and inject summary.
                AnsiConsole.MarkupLine "[dim]Compressing context — summarising session history…[/]"
                let compressPrompt =
                    "Summarise the complete conversation so far into a concise context block.\n\n" +
                    "Include:\n" +
                    "- Key decisions and conclusions\n" +
                    "- Files created or modified (with paths)\n" +
                    "- Problems solved and their solutions\n" +
                    "- Open tasks and next steps\n\n" +
                    "Output as a compact bulleted list. Preserve specific file paths, function names, and error codes mentioned."
                let summaryBuf = System.Text.StringBuilder()
                try
                    use compressCts = new CancellationTokenSource(System.TimeSpan.FromSeconds 30.0)
                    let stream = Conversation.run agent session compressPrompt compressCts.Token
                    let enum   = stream.GetAsyncEnumerator(compressCts.Token)
                    let mutable running = true
                    while running do
                        let! ok = enum.MoveNextAsync().AsTask()
                        if ok then
                            match enum.Current with
                            | Conversation.TextChunk t -> summaryBuf.Append t |> ignore
                            | Conversation.Finished    -> running <- false
                            | Conversation.Failed _    -> running <- false
                            | _ -> ()
                        else running <- false
                with _ -> ()
                let summary = summaryBuf.ToString().Trim()
                if summary.Length > 0 then
                    // Reset session and inject compressed context
                    match box session with
                    | :? IAsyncDisposable as d -> try do! d.DisposeAsync().AsTask() with _ -> ()
                    | _ -> ()
                    try
                        let! newSession = agent.CreateSessionAsync(CancellationToken.None)
                        session <- newSession
                    with ex ->
                        AnsiConsole.Write(Render.errorLine liveStrings ex.Message)
                        AnsiConsole.WriteLine()
                    let injection = $"[Compressed session context]\n{summary}"
                    do! streamAndRender agent session injection cfg cancelSrc zenMode
                    if Render.isColorEnabled () then
                        AnsiConsole.MarkupLine "[dim]Context compressed — previous history summarised and reset.[/]"
                    else
                        Console.Out.WriteLine "Context compressed — previous history summarised and reset."
                else
                    AnsiConsole.MarkupLine "[dim yellow]Could not generate summary — context unchanged.[/]"
                AnsiConsole.WriteLine()
                StatusBar.refresh ()
            | Some userInput ->
                let userInput = ReadLine.normalizeInput userInput
                if ReadLine.hasZeroWidth userInput then
                    AnsiConsole.Write(Markup("[dim yellow]" + Markup.Escape liveStrings.ZeroWidthWarning + "[/]"))
                    AnsiConsole.WriteLine()
                let expandedInput = expandAtFiles cwd liveStrings userInput
                let expandedInput = expandClipboard expandedInput liveStrings
                let expandedInput = expandClipRing expandedInput
                StatusBar.recordWords (expandedInput.Split([|' '; '\n'; '\r'; '\t'|], StringSplitOptions.RemoveEmptyEntries).Length)
                turnNumber <- turnNumber + 1
                AnsiConsole.Write(Render.userMessage cfg.Ui userInput turnNumber)
                AnsiConsole.WriteLine()
                let expandedInput =
                    match Fugue.Core.ConversationIntent.classify lastResponseWasPlan expandedInput with
                    | Fugue.Core.ConversationIntent.Affirmation ->
                        if Render.isColorEnabled () then AnsiConsole.MarkupLine "[dim]↳ proceeding with plan[/]"
                        else Console.Out.WriteLine "↳ proceeding with plan"
                        "Yes, please proceed with the plan outlined above."
                    | _ -> expandedInput
                let effectiveInput =
                    let base' =
                        match verbosityPrefix with
                        | Some prefix -> prefix + expandedInput
                        | None -> expandedInput
                    if pinnedMessages.IsEmpty then base'
                    else
                        let pins = pinnedMessages |> List.mapi (fun i m -> $"[pinned {i+1}] {m}") |> String.concat "\n"
                        pins + "\n\n" + base'
                let sw = System.Diagnostics.Stopwatch.StartNew()
                do! streamAndRender agent session effectiveInput cfg cancelSrc zenMode
                StatusBar.refresh ()
                // Nudge if the same tool error has recurred across sessions.
                let recurring = Fugue.Core.ErrorTrend.findRecurring 3 14
                match recurring with
                | (sig', count) :: _ ->
                    let short = if sig'.Length > 80 then sig'.[..79] + "…" else sig'
                    if Render.isColorEnabled () then
                        AnsiConsole.MarkupLine($"[dim yellow]⚠ recurring error ({count}×): {Markup.Escape short} — type [bold]/report-bug[/] to file a report[/]")
                    else
                        Console.Out.WriteLine($"⚠ recurring error ({count}×): {short} — type /report-bug to file a report")
                | [] -> ()
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
                    Console.Error.WriteLine($"[tool: {name}({args})]")
                | Conversation.ToolCompleted(id, output, isErr) ->
                    ignore id
                    if isErr then Console.Error.WriteLine($"[tool error: {output}]")
                | Conversation.Finished -> ()
                | Conversation.Failed ex -> raise ex
            else hasNext <- false
        Console.Out.WriteLine()
}

[<RequiresUnreferencedCode("Calls Conversation.run which uses STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
[<RequiresDynamicCode("Calls Conversation.run which uses STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
let runPrint (agent: AIAgent) (prompt: string) : Task<int> = task {
    // Append stdin if piped — lets "git diff | fugue --print 'review this'" work.
    let stdinSuffix =
        if Console.IsInputRedirected then
            let s = Console.In.ReadToEnd().Trim()
            if String.IsNullOrWhiteSpace s then "" else "\n\n" + s
        else ""
    let fullPrompt = (prompt.Trim() + stdinSuffix).Trim()
    if String.IsNullOrWhiteSpace fullPrompt then
        Console.Error.WriteLine "fugue --print: prompt is empty"
        return 1
    else
        let! session = agent.CreateSessionAsync(CancellationToken.None)
        let stream = Conversation.run agent session fullPrompt CancellationToken.None
        let enumerator = stream.GetAsyncEnumerator(CancellationToken.None)
        let mutable hasNext = true
        let mutable exitCode = 0
        while hasNext do
            let! step = enumerator.MoveNextAsync().AsTask()
            if step then
                match enumerator.Current with
                | Conversation.TextChunk t ->
                    Console.Out.Write t
                    Console.Out.Flush()
                | Conversation.ToolStarted(_, name, args) ->
                    Console.Error.WriteLine($"[tool: {name}({args})]")
                | Conversation.ToolCompleted(id, output, isErr) ->
                    ignore id
                    if isErr then Console.Error.WriteLine($"[tool error: {output}]")
                | Conversation.Finished -> ()
                | Conversation.Failed ex ->
                    Console.Error.WriteLine($"error: {ex.Message}")
                    exitCode <- 1
            else hasNext <- false
        Console.Out.WriteLine()
        return exitCode
}
