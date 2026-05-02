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

/// Collapse newlines/tabs to spaces and clip to 60 chars for the /turns picker.
/// Public so tests can lock in invariants without driving a TTY.
let previewTurnContent (s: string) : string =
    let collapsed = s.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ')
    if collapsed.Length <= 60 then collapsed
    else collapsed.Substring(0, 60)

// Last turn that had tool errors — used by /report-bug.
let mutable private lastFailedTurn : BugReport.TurnRecord option = None

// Whether the last AI response looked like a plan (for smart follow-up detection).
let mutable private lastResponseWasPlan = false

/// Delegate to SessionPersistence — kept internal so existing ReplTests still compile.
let internal generateUlid () : string = Fugue.Core.SessionPersistence.generateUlid ()

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
                Surface.markupLine($"[dim]{Markup.Escape (System.String.Format(strings.AtFileNotFound, pathPart))}[/]")
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
                    Surface.markupLine($"[dim]{Markup.Escape (System.String.Format(strings.AtFileNotFound, pathPart))}[/]")
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
        (cfg: AppConfig) (cancelSrc: CancelSource) (zen: bool)
        (recordFn: Fugue.Core.SessionRecord.SessionRecord -> unit) : Task<unit> = task {
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
                        Surface.write t
                        responseText.Append t |> ignore
                        StatusBar.recordToken ()
                        assistantStreaming <- true
                    | Conversation.ToolStarted(id, name, args) ->
                        toolMeta.[id] <- (name, args, Stopwatch.GetTimestamp())
                        toolCallsThisTurn <- toolCallsThisTurn + 1
                        recordFn (Fugue.Core.SessionRecord.ToolCall(DateTimeOffset.UtcNow, name, args, id))
                        if assistantStreaming then
                            Surface.lineBreak ()
                            assistantStreaming <- false
                        // Show a retry diff when the same tool previously failed with different args.
                        match failedArgs.TryGetValue name with
                        | true, prevArgs when prevArgs <> args ->
                            let diff = ToolRetryDiff.diffArgs prevArgs args
                            if not diff.IsEmpty && not zen then
                                let formatted = ToolRetryDiff.formatDiff diff
                                if Render.isColorEnabled () then
                                    Surface.markupLine("[dim]↻ retry diff:[/]")
                                    for line in formatted.Split('\n') do
                                        let color = if line.StartsWith "+" then "green" elif line.StartsWith "-" then "red" else "yellow"
                                        Surface.markupLine($"  [{color}]{Markup.Escape line}[/]")
                                else
                                    Surface.writeLine($"↻ retry diff:\n{formatted}")
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
                        recordFn (Fugue.Core.SessionRecord.ToolResult(DateTimeOffset.UtcNow, id, output, isErr))
                        let state =
                            if isErr then Render.Failed(name, args, output, elapsed)
                            else Render.Completed(name, args, output, elapsed)
                        if not zen then
                            Surface.writeRenderable(Render.toolBullet strings state)
                            Surface.lineBreak ()
                        StatusBar.refresh()
                    | Conversation.Finished -> ()
                    | Conversation.Failed ex -> raise ex
                else hasNext <- false
            if assistantStreaming then
                let text = responseText.ToString()
                // Emit AssistantTurn with full content (Phase 3: FTS5 indexer capture).
                if text.Length > 0 then
                    recordFn (Fugue.Core.SessionRecord.AssistantTurn(DateTimeOffset.UtcNow, text))
                let final =
                    if Render.isColorEnabled () then Some (Render.assistantFinal text)
                    else None
                do! StreamRender.finalizeTurn text final (Render.isTypewriterMode ())
                let wordCount = text.Split([|' '; '\n'; '\r'; '\t'|], StringSplitOptions.RemoveEmptyEntries).Length
                StatusBar.recordWords wordCount
                if wordCount >= 20 then
                    let mins = max 1 (int (Math.Round(float wordCount / 220.0)))
                    let timeStr = if mins = 1 then "~1 min read" else $"~{mins} min read"
                    let footer = $"[~{wordCount} words · {timeStr}]"
                    if Render.isColorEnabled () then
                        Surface.markupLine($"[dim]{Markup.Escape footer}[/]")
                    else
                        Surface.writeLine footer
                if toolCallsThisTurn >= 10 then
                    let msg = strings.ToolCallsWarning.Replace("{0}", string toolCallsThisTurn)
                    if Render.isColorEnabled () then
                        Surface.markupLine($"[dim yellow]⚠️ {Markup.Escape msg}[/]")
                    else
                        Surface.writeLine($"⚠️ {msg}")
                if errorToolCalls.Count > 0 then
                    lastFailedTurn <- Some { BugReport.UserPrompt = input; BugReport.ToolCalls = errorToolCalls |> Seq.toList; BugReport.AiResponse = responseText.ToString(); BugReport.ErrorText = None }
                lastResponseWasPlan <- Fugue.Core.ConversationIntent.looksLikePlan (responseText.ToString())
            elif toolCallsThisTurn = 0 then
                // The agent finished with no TextContent and no tool calls — the
                // model produced nothing renderable.  Common cause (#923): the
                // OpenAI-compat adapter for the active model returns only
                // UsageContent, no TextContent (observed with LM Studio + Gemma).
                // Without this hint the user sees their prompt echoed and silence,
                // and assumes Fugue hung.
                Surface.lineBreak ()
                Surface.markupLine "[yellow]⚠ model returned no content[/] [dim](only usage / tool-call stats — check provider + model loading; try /model set <other>)[/]"
                Surface.lineBreak ()
        with
        | :? OperationCanceledException ->
            if assistantStreaming then Surface.lineBreak ()
            Surface.writeRenderable(Render.cancelled strings)
            Surface.lineBreak ()
        | ex ->
            if assistantStreaming then Surface.lineBreak ()
            Surface.writeRenderable(Render.errorLine strings ex.Message)
            Surface.lineBreak ()
            lastFailedTurn <- Some { BugReport.UserPrompt = input; BugReport.ToolCalls = errorToolCalls |> Seq.toList; BugReport.AiResponse = responseText.ToString(); BugReport.ErrorText = Some ex.Message }
    finally
        StatusBar.stopStreaming()
        StatusBar.refresh()
}

let private bellChar () = Surface.write "\a"

let private prelude (cfg: AppConfig) =
    if cfg.Ui.Bell then
        bellChar ()
        System.Threading.Thread.Sleep 80
        bellChar ()

let private coda (cfg: AppConfig) =
    if cfg.Ui.Bell then bellChar ()

[<RequiresUnreferencedCode("Calls Conversation.run which uses STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
[<RequiresDynamicCode("Calls Conversation.run which uses STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
let run (initialAgent: AIAgent) (sessionRef: (AgentSession | null) ref) (initialCfg: AppConfig) (cwd: string) (lastSummary: string option) (rebuildAgent: AppConfig -> AIAgent) : Task<unit> = task {
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
    sessionRef.Value <- session
    // JSONL session file — appended throughout the session.
    let providerName, modelName = providerInfo cfg.Provider
    let sessionFilePath = Fugue.Core.SessionPersistence.sessionPath cwd currentSessionId
    Fugue.Core.SessionPersistence.appendRecord sessionFilePath
        (Fugue.Core.SessionRecord.SessionStart(DateTimeOffset.UtcNow, cwd, modelName, providerName))
    // Pre-fill for the next ReadLine call. Used by /menu and similar pickers
    // that hand a partial command back to the user for completion.
    let mutable nextInputPrefill : string = ""
    // Model name cache for /model set autocomplete.
    // Populated in background at session start; re-fetched on provider change via /model set.
    // cachedModelsStale flips true when a fetch returns an empty list (HTTP failure
    // proxy — getModels returns [] on any non-2xx or exception). Surfaced as `[stale]`
    // hint in the inline /model set suggestions render. Reset to false on any non-empty
    // refresh.
    let cachedModels : string list ref = ref []
    let cachedModelsStale : bool ref = ref false
    Async.Start(async {
        let! models = Fugue.Agent.ModelDiscovery.getModels cfg.Provider cfg.BaseUrl |> Async.AwaitTask
        if models.IsEmpty then
            cachedModelsStale.Value <- true
        else
            cachedModels.Value <- models
            cachedModelsStale.Value <- false
    })
    DebugLog.sessionStart providerName modelName cwd
    // Load hooks config once per session (file absent → silent no-op).
    let hooksConfig = Fugue.Core.Hooks.load ()
    // Fire SessionStart hooks before the first prompt is shown.
    let sessionStartPayload =
        Fugue.Core.Hooks.sessionStartPayload cwd modelName providerName currentSessionId
    do! Fugue.Core.Hooks.runLifecycle Fugue.Core.Hooks.HookEvent.SessionStart sessionStartPayload hooksConfig
    StatusBar.start cwd cfg
    Render.showBanner ()
    if cfg.DryRun then
        if Render.isColorEnabled () then
            Surface.markupLine "[yellow bold]⚑ dry-run[/] [dim]— tools will log calls without executing[/]"
        else
            Surface.writeLine "⚑ dry-run — tools will log calls without executing"
        Surface.lineBreak ()
    // Show last-session resuming hint if available
    let strings0 = pick cfg.Ui.Locale
    lastSummary |> Option.iter (fun s ->
        let first = let i = s.IndexOf('.') in if i > 0 && i < 120 then s.[..i - 1] else s.[..min 119 (s.Length - 1)]
        let msg = strings0.SessionResuming.Replace("{0}", first)
        if Render.isColorEnabled () then
            Surface.markupLine($"[dim]{Markup.Escape msg}[/]")
        else
            Surface.writeLine msg
        Surface.lineBreak ())
    // Show template badge on startup
    cfg.TemplateName |> Option.iter (fun name ->
        if Render.isColorEnabled () then
            Surface.markupLine($"[dim cyan]▸ template: {Markup.Escape name}[/]")
        else
            Surface.writeLine($"template: {name}")
        Surface.lineBreak ())
    prelude cfg
    Surface.write "\x1b[?2004h"   // enable bracketed paste

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
            Surface.markupLine($"[dim]▸ .env detected ({lines.Length} vars){badge} — /env load to import[/]")
        else
            Surface.writeLine($".env detected ({lines.Length} vars) — /env load to import")
        Surface.lineBreak ()

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
                        // Propagate the full new config so the status bar re-renders with
                        // the updated locale, model, and provider label immediately.
                        StatusBar.setCfg newCfg
                        Surface.markupLine "[dim]Config reloaded.[/]"
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
                Surface.markupLine($"[dim]{sym} turn {idx}[/]")
            let callbacks : ReadLine.ReadLineCallbacks =
                { OnClearScreen       = StatusBar.refresh
                  OnAnnotateUp        = fun () -> doAnnotate Fugue.Core.Annotation.Up
                  OnAnnotateDown      = fun () -> doAnnotate Fugue.Core.Annotation.Down
                  OnCycleApprovalMode =
                    fun () ->
                        let m = StatusBar.cycleApprovalMode ()
                        StatusBar.refresh ()
                        Surface.markupLine($"[dim]→ approval mode: {Fugue.Core.ApprovalMode.label m}[/]") }
            // Drain file-watch triggers from background FileSystemWatcher
            let watchTriggers = FileWatcher.drain ()
            for wcmd in watchTriggers do
                Surface.writeRenderable(Markup($"[dim]⟳ watch → {Markup.Escape wcmd}[/]"))
                Surface.lineBreak ()
                turnNumber <- turnNumber + 1
                Surface.writeRenderable(Render.userMessage cfg.Ui $"[watch] {wcmd}" turnNumber)
                Surface.lineBreak ()
                prelude cfg
                do! streamAndRender agent session wcmd cfg cancelSrc zenMode ignore
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
                    Surface.writeRenderable(Markup($"[dim]▶ {Markup.Escape step}[/]"))
                    Surface.lineBreak ()
                    System.Threading.Tasks.Task.FromResult(Some step)
                | None ->
                    let initial = nextInputPrefill
                    nextInputPrefill <- ""
                    let slashHelp = SlashCommands.getAll liveStrings
                    let modelCompleter (prefix: string) =
                        let matches =
                            cachedModels.Value
                            |> List.filter (fun m -> m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        (matches, cachedModelsStale.Value)
                    ReadLine.readAsync (Render.prompt cfg.Ui modelShort) liveStrings slashHelp modelCompleter callbacks (Render.isColorEnabled ()) initial cancelSrc.Token
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
                // Show "exiting…" so the brief shutdown blocking on actor flush
                // doesn't look like a hang. Goes straight to Console.Out — actor
                // is about to be drained and shut down anyway.
                Surface.markupLine($"[dim italic]{Markup.Escape liveStrings.ExitingMessage}[/]")
                cancelSrc.RequestQuit()
            | Some s when System.String.IsNullOrWhiteSpace s -> ()
            | Some s when s = "/exit" || s = "/quit" ->
                Surface.markupLine($"[dim italic]{Markup.Escape liveStrings.ExitingMessage}[/]")
                cancelSrc.RequestQuit()
            | Some s when s = "/help" ->
                Surface.writeRenderable(Markup("[bold]" + Markup.Escape liveStrings.HelpHeader + "[/]"))
                Surface.lineBreak ()
                let helpItems = SlashCommands.getAll liveStrings
                for (name, desc) in helpItems do
                    Surface.writeRenderable(Markup("  [cyan]" + Markup.Escape name + "[/]  [dim]" + Markup.Escape desc + "[/]"))
                    Surface.lineBreak ()
                Surface.lineBreak ()
                Surface.markupLine "[dim]Tip: turn numbers [[N]] are searchable — use your terminal's [bold]Cmd+F[/] / [bold]Ctrl+Shift+F[/] to jump back to any past message.[/]"
                Surface.markupLine "[dim]Tip: try [bold]/menu[/] for a scrollable arrow-key picker.[/]"
                StatusBar.refresh ()
            | Some s when s = "/menu" || s = "/commands" ->
                let allCmds = SlashCommands.getAll liveStrings
                match Picker.pick "Slash commands" allCmds 0 with
                | None ->
                    Surface.markupLine "[dim]Cancelled[/]"
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
                        Surface.writeRenderable(Render.errorLine liveStrings liveStrings.GitLogNoRepo)
                        Surface.lineBreak ()
                    | true ->
                        let out = proc.StandardOutput.ReadToEnd()
                        proc.WaitForExit()
                        if proc.ExitCode <> 0 || String.IsNullOrWhiteSpace out then
                            Surface.writeRenderable(Render.errorLine liveStrings liveStrings.GitLogNoRepo)
                            Surface.lineBreak ()
                        else
                            let prompt = liveStrings.GitLogPrompt.Replace("%s", out)
                            do! streamAndRender agent session prompt cfg cancelSrc zenMode ignore
                with ex ->
                    Surface.writeRenderable(Render.errorLine liveStrings ex.Message)
                    Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s = "/clear" ->
                Surface.write "\x1b[2J\x1b[H"
                StatusBar.refresh ()
            | Some s when s = "/squash" || s.StartsWith "/squash " ->
                let rawArg = if s = "/squash" then "" else s.Substring(8).TrimStart()
                let n = if String.IsNullOrWhiteSpace rawArg then 0 else (try int rawArg with _ -> 0)
                if n <= 0 then
                    Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.SquashUsage + "[/]"))
                    Surface.lineBreak ()
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
                            Surface.writeRenderable(Render.errorLine liveStrings liveStrings.SquashNoRepo)
                            Surface.lineBreak ()
                        | true ->
                            let out = proc.StandardOutput.ReadToEnd()
                            proc.WaitForExit()
                            if proc.ExitCode <> 0 || String.IsNullOrWhiteSpace out then
                                Surface.writeRenderable(Render.errorLine liveStrings liveStrings.SquashNoRepo)
                                Surface.lineBreak ()
                            else
                                let prompt = $"""Here are the last {n} commits to be squashed:\n\n{out}\n\nPlease generate a single conventional commit message that summarizes all these changes.\nFollow the format: type(scope): description\n\nAlso show the git command to execute the squash:\n  git reset --soft HEAD~{n} && git commit -m "<your message>"\n\nDo NOT run the command — just show it for the user to review and run manually."""
                                do! streamAndRender agent session prompt cfg cancelSrc zenMode ignore
                    with ex ->
                        Surface.writeRenderable(Render.errorLine liveStrings ex.Message)
                        Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s = "/short" ->
                verbosityPrefix <- Some "[VERBOSITY: respond briefly, 1-2 sentences per point, no elaboration unless asked]\n\n"
                Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.VerbosityShortSet + "[/]"))
                Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s = "/long" ->
                verbosityPrefix <- Some "[VERBOSITY: respond in detail, include explanations, examples, and context]\n\n"
                Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.VerbosityLongSet + "[/]"))
                Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s = "/zen" ->
                zenMode <- not zenMode
                if zenMode then
                    StatusBar.stop ()
                    Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.ZenOn + "[/]"))
                else
                    StatusBar.start cwd cfg
                    Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.ZenOff + "[/]"))
                Surface.lineBreak ()
                if not zenMode then StatusBar.refresh ()
            | Some s when s.StartsWith "/snippet" ->
                let parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                let sub = if parts.Length > 1 then parts.[1] else ""
                match sub with
                | "list" ->
                    if snippetStore.IsEmpty then
                        Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.SnippetNone + "[/]"))
                    else
                        for KeyValue(name, (lang, code, src)) in snippetStore do
                            let preview = code.Split('\n').[0].[..60].Trim()
                            Surface.writeRenderable(Markup($"  [cyan]{Markup.Escape name}[/] [dim]({Markup.Escape lang} · {Markup.Escape src})[/]  {Markup.Escape preview}"))
                            Surface.lineBreak ()
                    Surface.lineBreak ()
                    StatusBar.refresh ()
                | "save" when parts.Length >= 4 ->
                    let name = parts.[2]
                    let fileRef = parts.[3]
                    // parse file:start-end
                    let colonIdx = fileRef.LastIndexOf ':'
                    if colonIdx < 1 then
                        Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.SnippetUsage + "[/]"))
                        Surface.lineBreak ()
                    else
                        let filePart = fileRef.[..colonIdx-1]
                        let rangePart = fileRef.[colonIdx+1..]
                        let dashIdx = rangePart.IndexOf '-'
                        match (if dashIdx > 0 then System.Int32.TryParse(rangePart.[..dashIdx-1]), System.Int32.TryParse(rangePart.[dashIdx+1..]) else (false, 0), (false, 0)) with
                        | (true, startL), (true, endL) ->
                            let fullPath = Fugue.Tools.PathSafety.resolve cwd filePart
                            if not (System.IO.File.Exists fullPath) then
                                Surface.writeRenderable(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.AtFileNotFound, filePart)) + "[/]"))
                            else
                                let lines = System.IO.File.ReadAllLines fullPath
                                let s1 = max 1 startL
                                let e1 = min lines.Length endL
                                let code = lines.[s1-1..e1-1] |> String.concat "\n"
                                let ext = (System.IO.Path.GetExtension filePart |> Option.ofObj |> Option.defaultValue "").TrimStart('.')
                                let lang = if ext = "" then "text" else ext
                                snippetStore <- snippetStore |> Map.add name (lang, code, fileRef)
                                Surface.writeRenderable(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.SnippetSaved, name)) + "[/]"))
                            Surface.lineBreak ()
                        | _ ->
                            Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.SnippetUsage + "[/]"))
                            Surface.lineBreak ()
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
                        Surface.writeRenderable(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.SnippetNotFound, query)) + "[/]"))
                        Surface.lineBreak ()
                    | Some(lang, code, _) ->
                        Surface.writeRenderable(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.SnippetInjected, query)) + "[/]"))
                        Surface.lineBreak ()
                        let block = $"```{lang}\n{code}\n```"
                        do! streamAndRender agent session block cfg cancelSrc zenMode ignore
                    StatusBar.refresh ()
                | "remove" when parts.Length >= 3 ->
                    let name = parts.[2..] |> String.concat " "
                    if snippetStore.ContainsKey name then
                        snippetStore <- snippetStore |> Map.remove name
                        Surface.writeRenderable(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.SnippetRemoved, name)) + "[/]"))
                    else
                        Surface.writeRenderable(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.SnippetNotFound, name)) + "[/]"))
                    Surface.lineBreak ()
                    StatusBar.refresh ()
                | _ ->
                    Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.SnippetUsage + "[/]"))
                    Surface.lineBreak ()
                    StatusBar.refresh ()
            | Some s when s = "/bookmarks" || (s.StartsWith "/bookmark " && s.Contains " remove ") || s.StartsWith "/bookmark" ->
                let parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                let isBookmarks = s = "/bookmarks"
                let isRemove = parts.Length >= 3 && parts.[1] = "remove"
                if isBookmarks then
                    if bookmarks.IsEmpty then
                        Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.BookmarkNone + "[/]"))
                        Surface.lineBreak ()
                    else
                        for (i, (name, file, s1, e1)) in bookmarks |> List.indexed do
                            Surface.writeRenderable(Markup($"  [cyan]{i+1}.[/] [bold]{Markup.Escape name}[/] [dim]{Markup.Escape file}:{s1}-{e1}[/]"))
                            Surface.lineBreak ()
                    StatusBar.refresh ()
                elif isRemove then
                    let name = parts.[2..] |> String.concat " "
                    let before = bookmarks.Length
                    bookmarks <- bookmarks |> List.filter (fun (n, _, _, _) -> n <> name)
                    if bookmarks.Length < before then
                        Surface.writeRenderable(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.BookmarkRemoved, name)) + "[/]"))
                    else
                        Surface.writeRenderable(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.BookmarkNotFound, name)) + "[/]"))
                    Surface.lineBreak ()
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
                        Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.BookmarkUsage + "[/]"))
                    | Some(fullPath, s1, e1) ->
                        bookmarks <- bookmarks @ [(name, fullPath, s1, e1)]
                        Surface.writeRenderable(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.BookmarkSaved, name)) + "[/]"))
                    Surface.lineBreak ()
                    StatusBar.refresh ()
                else
                    Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.BookmarkUsage + "[/]"))
                    Surface.lineBreak ()
                    StatusBar.refresh ()
            | Some s when s = "/clear-history" ->
                match box session with
                | :? IAsyncDisposable as d ->
                    try do! d.DisposeAsync().AsTask() with _ -> ()
                | _ -> ()
                try
                    let! newSession = agent.CreateSessionAsync(CancellationToken.None)
                    session <- newSession
                    sessionRef.Value <- session
                    Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.ClearHistoryDone + "[/]"))
                    Surface.lineBreak ()
                with ex ->
                    session <- null
                    sessionRef.Value <- session
                    Surface.writeRenderable(Render.errorLine liveStrings ex.Message)
                    Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/http " || s = "/http" ->
                let raw = if s = "/http" then "" else s.Substring("/http ".Length)
                if raw.Trim() = "" then
                    Surface.writeRenderable(Markup("[dim]Usage: /http METHOD URL [-H \"K: V\"] [-d BODY] [--timeout N] [--inject][/]"))
                    Surface.lineBreak ()
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
                        Surface.writeRenderable(Markup("[red]Missing URL[/]"))
                        Surface.lineBreak ()
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
                                Surface.markupLine($"[bold][{statusColor}]HTTP {status} {resp.StatusCode}[/][/]")
                            else
                                Surface.writeLine($"HTTP {status} {resp.StatusCode}")
                            let preview = if respBody.Length > 4096 then respBody.[..4095] + $"\n… [{respBody.Length} chars total]" else respBody
                            Surface.writeRenderable(Render.assistantFinal preview)
                            Surface.lineBreak ()
                            if inject then
                                let ctx = $"[HTTP response from {method'} {url} — status {status}]\n{respBody}"
                                turnNumber <- turnNumber + 1
                                Surface.writeRenderable(Render.userMessage cfg.Ui $"[injected HTTP response from {method'} {url}]" turnNumber)
                                Surface.lineBreak ()
                                do! streamAndRender agent session ctx cfg cancelSrc zenMode ignore
                        with ex ->
                            Surface.writeRenderable(Render.errorLine liveStrings $"HTTP error: {ex.Message}")
                            Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s = "/tools" ->
                Surface.writeRenderable(Markup("[bold]" + Markup.Escape liveStrings.ToolsHeader + "[/]"))
                Surface.lineBreak ()
                for (name, desc) in Fugue.Tools.ToolRegistry.descriptions do
                    Surface.writeRenderable(Markup("  [cyan]" + Markup.Escape name + "[/]  [dim]" + Markup.Escape desc + "[/]"))
                    Surface.lineBreak ()
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
                    Surface.writeRenderable tbl
                else
                    let yn ok = if ok then "yes" else "no"
                    Surface.writeLine($"ANSI color:      {yn ansiColor}")
                    Surface.writeLine($"True color:      {yn trueColor}  COLORTERM={colorTerm}")
                    Surface.writeLine($"256 colors:      {yn colors256}  TERM={term}")
                    Surface.writeLine($"UTF-8:           {yn utf8}  LANG={lang}")
                    Surface.writeLine($"Emoji:           {yn emoji}")
                    Surface.writeLine($"Kitty protocol:  {yn kitty}")
                    Surface.writeLine($"OSC 8:           {yn osc8}")
                    Surface.writeLine($"Terminal width:  {termW}")
                    Surface.writeLine($"Docker:          {yn inDocker}")
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
                            Surface.markupLine($"[dim]{Markup.Escape (String.Format(liveStrings.BenchRunning, string attempt, string nArg))}[/]")
                        else
                            Surface.writeLine(String.Format(liveStrings.BenchRunning, string attempt, string nArg))
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
                    Surface.writeRenderable(Render.errorLine liveStrings liveStrings.BenchAborted)
                    Surface.lineBreak ()
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
                        Surface.writeRenderable tbl
                    else
                        Surface.writeLine(String.Format("{0,-6}  {1,-8}  {2,-8}", "metric", "TTFT", "Total"))
                        let pr label p = Surface.writeLine($"{label,-6}  {pct ttfts p,-6}ms  {pct totals p,-6}ms")
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
                    Surface.writeRenderable(Markup("[dim]no session history yet[/]"))
                    Surface.lineBreak ()
                else
                    for (idx, (ts, summary)) in records |> List.mapi (fun i r -> i, r) do
                        let ago =
                            let diff = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds ts
                            if diff.TotalDays >= 1.0 then $"{int diff.TotalDays}d ago"
                            elif diff.TotalHours >= 1.0 then $"{int diff.TotalHours}h ago"
                            else $"{max 1 (int diff.TotalMinutes)}m ago"
                        if Render.isColorEnabled () then
                            Surface.markupLine($"[dim]#{idx + 1} ({ago})[/]")
                            Surface.writeRenderable(MarkdownRender.toRenderable summary)
                            Surface.lineBreak ()
                        else
                            Surface.writeLine($"#{idx + 1} ({ago})")
                            Surface.writeLine summary
                            Surface.lineBreak ()
                StatusBar.refresh ()
            | Some "/turns" ->
                let records = Fugue.Core.SessionPersistence.readRecords sessionFilePath
                let turnItems =
                    records
                    |> List.choose (function
                        | Fugue.Core.SessionRecord.UserTurn(_, c)      -> Some ("user", c)
                        | Fugue.Core.SessionRecord.AssistantTurn(_, c) -> Some ("asst", c)
                        | _                                            -> None)
                    |> List.mapi (fun i (role, c) ->
                        ($"[{i + 1}] {role}", previewTurnContent c))
                if turnItems.IsEmpty then
                    Surface.markupLine "[dim]No turns yet in this session.[/]"
                else
                    match Picker.pick "Turns — current session" turnItems (turnItems.Length - 1) with
                    | None -> Surface.markupLine "[dim]Cancelled[/]"
                    | Some i ->
                        let (label, _) = turnItems.[i]
                        let closeIdx = label.IndexOf ']'
                        if closeIdx > 1 then
                            let n = label.Substring(1, closeIdx - 1)
                            nextInputPrefill <- $"/show {n}"
                StatusBar.refresh ()
            | Some "/templates" ->
                let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                let dir = System.IO.Path.Combine(home, ".fugue", "templates")
                if not (System.IO.Directory.Exists dir) then
                    Surface.markupLine("[dim]No templates found. Create ~/.fugue/templates/<name>.md to add one.[/]")
                else
                    let files = System.IO.Directory.GetFiles(dir, "*.md")
                    if files.Length = 0 then
                        Surface.markupLine("[dim]No templates found. Create ~/.fugue/templates/<name>.md to add one.[/]")
                    else
                        Surface.markupLine("[dim]Available templates (use fugue --template <name>):[/]")
                        for f in files |> Array.sort do
                            let name = System.IO.Path.GetFileNameWithoutExtension f |> Option.ofObj |> Option.defaultValue ""
                            if name <> "" then
                                Surface.markupLine($"  [cyan]{Markup.Escape name}[/]")
                StatusBar.refresh ()
            | Some s when s = "/gen" || s.StartsWith "/gen " ->
                let kind = (if s = "/gen" then "" else s.Substring(4)).Trim().ToLowerInvariant()
                match kind with
                | "uuid" | "" ->
                    let v = System.Guid.NewGuid().ToString()
                    Surface.markupLine($"[green]{v}[/]")
                    ClipRing.push v
                | "ulid" ->
                    let v = generateUlid ()
                    Surface.markupLine($"[green]{v}[/]")
                    ClipRing.push v
                | "nanoid" ->
                    let v = generateNanoid ()
                    Surface.markupLine($"[green]{v}[/]")
                    ClipRing.push v
                | other ->
                    Surface.markupLine($"[red]Unknown generator '[/][bold red]{Markup.Escape other}[/][red]'. Use: /gen uuid|ulid|nanoid[/]")
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
                        Surface.markupLine($"[{color}]{Markup.Escape msg}[/]")
                    else Surface.writeLine msg
                | _ ->
                    if Render.isColorEnabled () then
                        Surface.markupLine($"[dim]{Markup.Escape liveStrings.RateUsage}[/]")
                    else Surface.writeLine liveStrings.RateUsage
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
                    Surface.markupLine($"[dim]{Markup.Escape liveStrings.AnnotateUsage}[/]")
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
                    Surface.markupLine($"[dim]annotated: {Markup.Escape msg}[/]")
                StatusBar.refresh ()
            | Some s when s = "/issue" || s.StartsWith "/issue " ->
                let rawArg = if s = "/issue" then "" else s.Substring(7).TrimStart()
                if String.IsNullOrWhiteSpace rawArg then
                    Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.IssueUsage + "[/]"))
                    Surface.lineBreak ()
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
                            Surface.writeRenderable(Render.errorLine liveStrings (String.Format(liveStrings.IssueNotFound, rawArg)))
                            Surface.lineBreak ()
                        | true ->
                            let output = proc.StandardOutput.ReadToEnd()
                            proc.WaitForExit()
                            if proc.ExitCode <> 0 then
                                let err = proc.StandardError.ReadToEnd().Trim()
                                Surface.writeRenderable(Render.errorLine liveStrings (String.Format(liveStrings.IssueNotFound, err)))
                                Surface.lineBreak ()
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
                                Surface.markupLine($"[dim]{Markup.Escape (String.Format(liveStrings.IssueFetched, rawArg, title))}[/]")
                                Surface.lineBreak ()
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
                                            Surface.markupLine($"[dim]created branch {Markup.Escape branchName}[/]")
                                            Surface.lineBreak ()
                                with _ -> ()   // git unavailable or not a repo — silent
                                // Inject issue context into conversation
                                do! streamAndRender agent session issueContext cfg cancelSrc zenMode ignore
                    with ex ->
                        Surface.writeRenderable(Render.errorLine liveStrings ex.Message)
                        Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s = "/model suggest" ->
                Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.AskingModelRecommendation + "[/]"))
                Surface.lineBreak ()
                do! streamAndRender agent session liveStrings.ModelSuggestPrompt cfg cancelSrc zenMode ignore
                StatusBar.refresh ()
            | Some s when s = "/model" || s = "/model list" ->
                let prov, currentModel = providerInfo cfg.Provider
                Surface.markupLine($"[bold]Current:[/] [cyan]{Markup.Escape prov}[/] / [green]{Markup.Escape currentModel}[/]")
                Surface.markupLine($"[dim]Fetching models from {Markup.Escape prov}…[/]")
                let! models = ModelDiscovery.getModels cfg.Provider cfg.BaseUrl
                if List.isEmpty models then
                    Surface.markupLine "[yellow]No models discovered. Use /model set <name> to switch manually.[/]"
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
                                sessionRef.Value <- session
                                let provName, _ = providerInfo cfg.Provider
                                Surface.markupLine($"[green]✓[/] model → [cyan]{Markup.Escape provName}[/] / [green]{Markup.Escape newModel}[/] [dim](history reset)[/]")
                                StatusBar.setCfg cfg
                            with ex ->
                                Surface.markupLine($"[red]✗ failed to switch model: {Markup.Escape ex.Message}[/]")
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

                    Surface.lineBreak ()
                    // Initial render. Each row + blank line + footer.
                    for i in 0 .. totalRows - 1 do
                        if colour then Surface.markupLine(renderRow i (i = 0))
                        else Surface.writeLine(renderRow i (i = 0))
                    Surface.lineBreak ()
                    if colour then Surface.markupLine footer
                    else Surface.writeLine footer

                    let renderedLines = totalRows + 2  // rows + blank + footer

                    let redraw (idx: int) =
                        // Move cursor up to start of menu, clear each line, redraw.
                        Surface.write($"\x1b[{renderedLines}F")
                        for i in 0 .. totalRows - 1 do
                            Surface.write "\x1b[K"
                            if colour then Surface.markupLine(renderRow i (i = idx))
                            else Surface.writeLine(renderRow i (i = idx))
                        Surface.write "\x1b[K"
                        Surface.lineBreak ()
                        Surface.write "\x1b[K"
                        if colour then Surface.markupLine footer
                        else Surface.writeLine footer

                    let clearMenu () =
                        // Wipe the menu before printing the result.
                        Surface.write($"\x1b[{renderedLines}F")
                        for _ in 1 .. renderedLines do
                            Surface.write "\x1b[K"
                            Surface.write "\x1b[1B"
                        Surface.write($"\x1b[{renderedLines}F")

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
                        Surface.markupLine "[dim]Cancelled[/]"
                    | Some i when i = cancelIdx ->
                        Surface.markupLine "[dim]Cancelled[/]"
                    | Some i when i = suggestIdx ->
                        Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.AskingModelRecommendation + "[/]"))
                        Surface.lineBreak ()
                        do! streamAndRender agent session liveStrings.ModelSuggestPrompt cfg cancelSrc zenMode ignore
                    | Some i ->
                        do! pickModel modelsArr.[i]
                    StatusBar.refresh ()
            | Some s when s.StartsWith "/model set " ->
                let newModel = s.Substring("/model set ".Length).Trim()
                if newModel = "" then
                    Surface.markupLine "[red]/model set requires a model name[/]"
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
                        sessionRef.Value <- session
                        // Persist the model change to ~/.fugue/config.json so it
                        // survives a session restart. Best-effort: I/O failures
                        // shouldn't bring down the active session — the in-memory
                        // change is already applied above. Same shape as the /short
                        // and /long handlers (#922).
                        try Fugue.Core.Config.saveToFile cfg with _ -> ()
                        let prov, _ = providerInfo cfg.Provider
                        Surface.markupLine($"[green]✓[/] model → [cyan]{Markup.Escape prov}[/] / [green]{Markup.Escape newModel}[/] [dim](saved · history reset)[/]")
                        StatusBar.setCfg cfg
                        // Re-fetch model list for the (potentially) new provider in background.
                        // On empty result (HTTP failure proxy), keep the previous list and
                        // mark stale — the user will see `[stale]` next time they hit
                        // `/model set`. Successful refresh clears the stale flag.
                        Async.Start(async {
                            let! models = Fugue.Agent.ModelDiscovery.getModels cfg.Provider cfg.BaseUrl |> Async.AwaitTask
                            if models.IsEmpty then
                                cachedModelsStale.Value <- true
                            else
                                cachedModels.Value <- models
                                cachedModelsStale.Value <- false
                        })
                    with ex ->
                        Surface.markupLine($"[red]✗ failed to switch model: {Markup.Escape ex.Message}[/]")
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
                do! streamAndRender agent session onboardPrompt cfg cancelSrc zenMode ignore
            | Some s when s = "/review pr" || s = "/review pr " ->
                Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.ReviewPrUsage + "[/]"))
                Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/review pr " ->
                let arg = s.Substring(11).Trim()
                match System.Int32.TryParse arg with
                | false, _ ->
                    Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.ReviewPrUsage + "[/]"))
                    Surface.lineBreak ()
                | true, prNum when prNum < 1 ->
                    Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.ReviewPrUsage + "[/]"))
                    Surface.lineBreak ()
                | true, prNum ->
                    let meta = runCmd "gh" [| "pr"; "view"; string prNum; "--json"; "title,body" |] cwd
                    let diff = runCmd "gh" [| "pr"; "diff"; string prNum |] cwd
                    match diff with
                    | None ->
                        Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.ReviewPrNotFound + "[/]"))
                        Surface.lineBreak ()
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
                        Surface.writeRenderable(Render.userMessage cfg.Ui $"Reviewing PR #{prNum}…" 0)
                        Surface.lineBreak ()
                        do! streamAndRender agent session prompt cfg cancelSrc zenMode ignore
                StatusBar.refresh ()
            | Some s when s.StartsWith "/ask " ->
                let question = s.Substring(5).Trim()
                if String.IsNullOrWhiteSpace question then
                    Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.AskUsage + "[/]"))
                    Surface.lineBreak ()
                else
                    let augmented = $"[System: answer from your training knowledge only — do not invoke any tools, do not read files, do not run commands]\n\nUser: {question}"
                    Surface.writeRenderable(Render.userMessage cfg.Ui question 0)
                    Surface.lineBreak ()
                    do! streamAndRender agent session augmented cfg cancelSrc zenMode ignore
                StatusBar.refresh ()
            | Some s when s = "/ask" ->
                Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.AskUsage + "[/]"))
                Surface.lineBreak ()
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
                        Surface.writeRenderable(Render.errorLine liveStrings msg)
                        Surface.lineBreak ()
                    elif System.String.IsNullOrWhiteSpace output then
                        Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.NoDiff + "[/]"))
                        Surface.lineBreak ()
                    else
                        Surface.writeRenderable(DiffRender.toRenderable output)
                        Surface.lineBreak ()
                with ex ->
                    Surface.writeRenderable(Render.errorLine liveStrings ex.Message)
                    Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s = "/init" ->
                let fugueMdPath = System.IO.Path.Combine(cwd, "FUGUE.md")
                if System.IO.File.Exists fugueMdPath then
                    Surface.writeRenderable(Markup("[yellow]" + Markup.Escape liveStrings.InitExists + "[/]"))
                    Surface.lineBreak ()
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
                    Surface.writeRenderable(Render.userMessage cfg.Ui "/init" 0)
                    Surface.lineBreak ()
                    do! streamAndRender agent session initPrompt cfg cancelSrc zenMode ignore
                    StatusBar.refresh ()
            | Some s when s = "/new" ->
                Surface.write "\x1b[2J\x1b[H"
                match box session with
                | :? IAsyncDisposable as d ->
                    try do! d.DisposeAsync().AsTask() with _ -> ()
                | _ -> ()
                try
                    let! newSession = agent.CreateSessionAsync(CancellationToken.None)
                    session <- newSession
                    sessionRef.Value <- session
                with ex ->
                    session <- null
                    sessionRef.Value <- session
                    Surface.writeRenderable(Render.errorLine liveStrings ex.Message)
                    Surface.lineBreak ()
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
                            Surface.writeRenderable(Render.errorLine liveStrings ex.Message)
                            Surface.lineBreak ()
                    finally
                        cancelSrc.ExitChild ()
                        StatusBar.start cwd cfg
            | Some s when s = "/report-bug" ->
                match lastFailedTurn with
                | None ->
                    if Render.isColorEnabled () then Surface.markupLine "[dim]No failed turn to report yet.[/]"
                    else Surface.writeLine "No failed turn to report yet."
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
                        Surface.markupLine($"[dim]Bug report saved to {tmpPath}[/]")
                        Surface.markupLine($"[dim]To file: gh issue create -R korat-ai/fugue --title '...' --body-file {tmpPath}[/]")
                    else
                        Surface.writeLine($"Bug report saved to {tmpPath}")
                        Surface.writeLine($"To file: gh issue create -R korat-ai/fugue --title '...' --body-file {tmpPath}")
                Surface.lineBreak ()
            | Some s when s = "/summary" ->
                let summaryPrompt =
                    "Please generate a structured summary of our session so far.\n\n" +
                    "Include:\n" +
                    "- What we worked on (tasks, files changed, problems solved)\n" +
                    "- Key decisions made\n" +
                    "- Any open questions or next steps\n\n" +
                    "Be concise but complete."
                Surface.writeRenderable(Render.userMessage cfg.Ui "/summary" 0)
                Surface.lineBreak ()
                do! streamAndRender agent session summaryPrompt cfg cancelSrc zenMode ignore
                StatusBar.refresh ()
            | Some s when s.StartsWith "/document" ->
                let arg = s.Substring("/document".Length).Trim()
                let docPath =
                    if String.IsNullOrWhiteSpace arg then "docs/session-notes.md"
                    else
                        let clean = arg.TrimStart('/')
                        if clean.StartsWith "docs/" then clean else "docs/" + clean
                let docPrompt = System.String.Format(liveStrings.DocumentPrompt, docPath)
                Surface.writeRenderable(Render.userMessage cfg.Ui $"/document {docPath}" 0)
                Surface.lineBreak ()
                do! streamAndRender agent session docPrompt cfg cancelSrc zenMode ignore
                StatusBar.refresh ()
            | Some s when s = "/activity" ->
                if activityStore.IsEmpty then
                    Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.ActivityNone + "[/]"))
                    Surface.lineBreak ()
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
                            Surface.markupLine($"  [cyan]{heat}[/] [dim]{Markup.Escape label}[/] [grey]{count}x[/]")
                        else
                            printfn "  %s %s %dx" bar label count
                    Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/macro" ->
                let rest = s.Substring("/macro".Length).Trim()
                let sub  = if rest.Contains " " then rest.Substring(0, rest.IndexOf ' ') else rest
                let arg  = if rest.Contains " " then rest.Substring(rest.IndexOf ' ' + 1).Trim() else ""
                match sub with
                | "record" when not (String.IsNullOrWhiteSpace arg) ->
                    if Macros.isRecording() then Macros.stopRecording() |> ignore
                    Macros.startRecording arg
                    Surface.markupLine($"[dim]{Markup.Escape (String.Format(liveStrings.MacroRecording, arg))}[/]")
                | "stop" ->
                    match Macros.stopRecording() with
                    | Some name ->
                        let cnt = Macros.list() |> List.tryFind (fun (n, _) -> n = name) |> Option.map snd |> Option.defaultValue 0
                        Surface.markupLine($"[dim]{Markup.Escape (String.Format(liveStrings.MacroSaved, name, cnt))}[/]")
                    | None ->
                        Surface.markupLine("[dim]no recording in progress[/]")
                | "play" when not (String.IsNullOrWhiteSpace arg) ->
                    match Macros.list() |> List.tryFind (fun (n, _) -> n = arg) with
                    | Some (_, cnt) ->
                        if Macros.enqueuePlay arg then
                            Surface.markupLine($"[dim]{Markup.Escape (String.Format(liveStrings.MacroPlaying, arg, cnt))}[/]")
                        else
                            Surface.markupLine($"[dim]{Markup.Escape (String.Format(liveStrings.MacroNotFound, arg))}[/]")
                    | None ->
                        Surface.markupLine($"[dim]{Markup.Escape (String.Format(liveStrings.MacroNotFound, arg))}[/]")
                | "list" | "" ->
                    let macros = Macros.list()
                    if macros.IsEmpty then
                        Surface.markupLine($"[dim]{Markup.Escape liveStrings.MacroNone}[/]")
                    else
                        for (name, cnt) in macros do
                            Surface.markupLine($"  [cyan]{Markup.Escape name}[/] [dim]({cnt} steps)[/]")
                | "remove" when not (String.IsNullOrWhiteSpace arg) ->
                    if Macros.remove arg then
                        Surface.markupLine($"[dim]{Markup.Escape (String.Format(liveStrings.MacroRemoved, arg))}[/]")
                    else
                        Surface.markupLine($"[dim]{Markup.Escape (String.Format(liveStrings.MacroNotFound, arg))}[/]")
                | _ ->
                    Surface.markupLine($"[dim]{Markup.Escape liveStrings.MacroUsage}[/]")
                Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/theme" ->
                let sub = s.Substring("/theme".Length).Trim()
                match sub with
                | "bubbles" ->
                    Render.toggleBubbles ()
                    let on = Render.isBubblesMode ()
                    if on then
                        Surface.markupLine($"[dim]{Markup.Escape liveStrings.ThemeBubblesOn}[/]")
                    else
                        Surface.markupLine($"[dim]{Markup.Escape liveStrings.ThemeBubblesOff}[/]")
                    savedUi <- { savedUi with BubblesMode = on }
                    try Config.saveToFile { cfg with Ui = savedUi } with _ -> ()
                | "typewriter" ->
                    Render.toggleTypewriter ()
                    let on = Render.isTypewriterMode ()
                    if on then
                        Surface.markupLine($"[dim]{Markup.Escape liveStrings.ThemeTypewriterOn}[/]")
                    else
                        Surface.markupLine($"[dim]{Markup.Escape liveStrings.ThemeTypewriterOff}[/]")
                    savedUi <- { savedUi with TypewriterMode = on }
                    try Config.saveToFile { cfg with Ui = savedUi } with _ -> ()
                | _ ->
                    Surface.markupLine($"[dim]{Markup.Escape liveStrings.ThemeUsage}[/]")
                Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/alias" ->
                let parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                let sub = if parts.Length > 1 then parts.[1] else ""
                match sub with
                | "list" | "" when s = "/alias" || s = "/alias list" ->
                    if aliasStore.IsEmpty then
                        Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.AliasNone + "[/]"))
                    else
                        for KeyValue(name, expansion) in aliasStore do
                            Surface.writeRenderable(Markup($"  [cyan]/{Markup.Escape name}[/] [dim]→ {Markup.Escape expansion}[/]"))
                            Surface.lineBreak ()
                    Surface.lineBreak ()
                | "remove" when parts.Length >= 3 ->
                    let name = parts.[2]
                    aliasStore <- aliasStore |> Map.remove name
                    Surface.writeRenderable(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.AliasRemoved, name)) + "[/]"))
                    Surface.lineBreak ()
                | _ ->
                    // Parse: /alias <name> = <expansion>
                    let rest = s.Substring("/alias ".Length)
                    let eqIdx = rest.IndexOf '='
                    if eqIdx < 1 then
                        Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.AliasUsage + "[/]"))
                    else
                        let name = rest.[..eqIdx-1].Trim().TrimStart('/')
                        let expansion = rest.[eqIdx+1..].Trim()
                        if not (String.IsNullOrWhiteSpace name) && not (String.IsNullOrWhiteSpace expansion) then
                            aliasStore <- aliasStore |> Map.add name expansion
                            Surface.writeRenderable(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.AliasSet, name, expansion)) + "[/]"))
                        else
                            Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.AliasUsage + "[/]"))
                    Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s = "/cron" || s.StartsWith "/cron " ->
                let desc = (if s = "/cron" then "" else s.Substring(5)).Trim().ToLowerInvariant()
                if String.IsNullOrWhiteSpace desc then
                    Surface.markupLine "[dim]Usage: /cron \"every weekday at 9am UTC\"[/]"
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
                    | None -> Surface.markupLine "[red]Could not parse cron expression. Try: \"every day at 9am\", \"every 5 minutes\", \"every weekday at 17:00\"[/]"
                    | Some expr ->
                        Surface.markupLine($"[bold cyan]{Markup.Escape expr}[/]")
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
                        Surface.markupLine "[dim]Next 5 fire times (UTC):[/]"
                        while count < 5 do
                            if matchField t.Minute minPart && matchField t.Hour hourPart && matchField (int t.DayOfWeek) dowPart then
                                let tStr = t.ToString("yyyy-MM-dd HH:mm")
                                Surface.markupLine($"  [dim]{tStr}[/]")
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
                    Surface.markupLine "[dim]Usage: /regex \"pattern\" \"input\"[/]"
                else
                    try
                        let rx = System.Text.RegularExpressions.Regex(pattern)
                        let matches = rx.Matches(input)
                        if matches.Count = 0 then
                            Surface.markupLine "[dim]No matches[/]"
                        else
                            Surface.markupLine($"[dim]{matches.Count} match(es) for [/][cyan]{Markup.Escape pattern}[/]")
                            for m in matches do
                                Surface.markupLine($"  index=[bold]{m.Index}[/] length=[bold]{m.Length}[/] value=[green]{Markup.Escape m.Value}[/]")
                                for g in rx.GetGroupNames() do
                                    if g <> "0" then
                                        let grp = m.Groups.[g]
                                        if grp.Success then
                                            Surface.markupLine($"    [cyan]{Markup.Escape g}[/]=[green]{Markup.Escape grp.Value}[/]")
                    with ex ->
                        Surface.markupLine($"[red]Regex error: {Markup.Escape ex.Message}[/]")
                StatusBar.refresh ()

            | Some s when s = "/rename" || s.StartsWith "/rename " ->
                let title = (if s = "/rename" then "" else s.Substring(7)).Trim().Trim('"').Trim('\'')
                if String.IsNullOrWhiteSpace title then
                    match sessionTitle with
                    | Some t -> Surface.markupLine($"[dim]Session title: [/][bold]{Markup.Escape t}[/]")
                    | None   -> Surface.markupLine "[dim]No session title set. Usage: /rename <title>[/]"
                else
                    sessionTitle <- Some title
                    Surface.markupLine($"[dim]Session renamed to: [/][bold cyan]{Markup.Escape title}[/]")
                StatusBar.refresh ()

            | Some s when s = "/env" || s.StartsWith "/env " ->
                let sub = (if s = "/env" then "list" else s.Substring(4).Trim())
                match sub with
                | "list" | "" ->
                    if sessionEnvVars.IsEmpty then
                        Surface.markupLine "[dim]No session env vars set.[/]"
                    else
                        Surface.markupLine "[dim]Session env vars:[/]"
                        for name in sessionEnvVars do
                            let v = Environment.GetEnvironmentVariable name |> Option.ofObj |> Option.defaultValue ""
                            Surface.markupLine($"  [cyan]{Markup.Escape name}[/]=[green]{Markup.Escape v}[/]")
                | cmd when cmd.StartsWith "set " ->
                    let pair = cmd.Substring(4).Trim()
                    let eqIdx = pair.IndexOf '='
                    if eqIdx <= 0 then
                        Surface.markupLine "[red]Usage: /env set NAME=value[/]"
                    else
                        let name  = pair.[..eqIdx - 1].Trim()
                        let value = pair.[eqIdx + 1..]
                        Environment.SetEnvironmentVariable(name, value)
                        sessionEnvVars <- sessionEnvVars |> Set.add name
                        Surface.markupLine($"[dim]Set [cyan]{Markup.Escape name}[/]=[green]{Markup.Escape value}[/][/]")
                | cmd when cmd.StartsWith "unset " ->
                    let name = cmd.Substring(6).Trim()
                    Environment.SetEnvironmentVariable(name, null)
                    sessionEnvVars <- sessionEnvVars |> Set.remove name
                    Surface.markupLine($"[dim]Unset [cyan]{Markup.Escape name}[/][/]")
                | "load" ->
                    let dotEnvPath = System.IO.Path.Combine(cwd, ".env")
                    if not (System.IO.File.Exists dotEnvPath) then
                        Surface.markupLine "[dim yellow].env not found in current directory.[/]"
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
                        Surface.markupLine($"[dim]Loaded {loaded} vars from .env[/]")
                | other ->
                    Surface.markupLine($"[red]Unknown /env subcommand: {Markup.Escape other}. Use: /env list | /env load | /env set NAME=value | /env unset NAME[/]")
                StatusBar.refresh ()

            | Some s when s.StartsWith "/scratch" ->
                let sub = s.Substring("/scratch".Length).Trim()
                match sub with
                | "send" when not scratchLines.IsEmpty ->
                    let text = scratchLines |> String.concat "\n"
                    scratchLines <- []
                    Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.ScratchSent + "[/]"))
                    Surface.lineBreak ()
                    do! streamAndRender agent session text cfg cancelSrc zenMode ignore
                | "send" ->
                    Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.ScratchEmpty + "[/]"))
                    Surface.lineBreak ()
                | "clear" ->
                    scratchLines <- []
                    Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.ScratchCleared + "[/]"))
                    Surface.lineBreak ()
                | "show" ->
                    if scratchLines.IsEmpty then
                        Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.ScratchEmpty + "[/]"))
                    else
                        Surface.writeRenderable(Markup("[bold]" + Markup.Escape liveStrings.ScratchShowHeader + "[/]"))
                        Surface.lineBreak ()
                        for line in scratchLines do
                            Surface.writeRenderable(Markup("[dim]  " + Markup.Escape line + "[/]"))
                            Surface.lineBreak ()
                    Surface.lineBreak ()
                | text when not (String.IsNullOrWhiteSpace text) ->
                    scratchLines <- scratchLines @ [text]
                    Surface.writeRenderable(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.ScratchAppended, scratchLines.Length)) + "[/]"))
                    Surface.lineBreak ()
                | _ ->
                    // /scratch alone with no subcommand: show usage
                    Surface.writeRenderable(Markup("[dim]usage: /scratch <text> | /scratch send | /scratch show | /scratch clear[/]"))
                    Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s = "/watches" ->
                let ws = FileWatcher.list ()
                if ws.IsEmpty then
                    Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.WatchNone + "[/]"))
                    Surface.lineBreak ()
                else
                    for (path, cmd) in ws do
                        Surface.writeRenderable(Markup($"  [cyan]{Markup.Escape path}[/] → [dim]{Markup.Escape cmd}[/]"))
                        Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/unwatch " ->
                let path = s.Substring("/unwatch ".Length).Trim()
                let absPath = System.IO.Path.GetFullPath(path)
                FileWatcher.remove absPath
                Surface.writeRenderable(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.WatchRemoved, path)) + "[/]"))
                Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/watch " ->
                let rest = s.Substring("/watch ".Length).Trim()
                let spaceIdx = rest.IndexOf ' '
                if spaceIdx <= 0 then
                    Surface.writeRenderable(Markup("[dim]" + Markup.Escape liveStrings.WatchUsage + "[/]"))
                    Surface.lineBreak ()
                else
                    let path = rest.Substring(0, spaceIdx).Trim()
                    let cmd  = rest.Substring(spaceIdx + 1).Trim()
                    let absPath = System.IO.Path.GetFullPath(path)
                    if not (System.IO.File.Exists absPath) then
                        Surface.writeRenderable(Markup($"[dim]{Markup.Escape (System.String.Format(liveStrings.AtFileNotFound, path))}[/]"))
                        Surface.lineBreak ()
                    else
                        FileWatcher.add absPath cmd
                        Surface.writeRenderable(Markup("[dim]" + Markup.Escape (System.String.Format(liveStrings.WatchAdded, path, cmd)) + "[/]"))
                        Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/note " ->
                let text = s.Substring("/note ".Length).Trim()
                if not (String.IsNullOrWhiteSpace text) then
                    sessionNotes <- sessionNotes @ [(turnNumber, text)]
                    Surface.markupLine($"[dim]{Markup.Escape liveStrings.NoteAdded}[/]")
                    Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s = "/notes" ->
                if sessionNotes.IsEmpty then
                    Surface.markupLine($"[dim]{Markup.Escape liveStrings.NoteNone}[/]")
                else
                    for (turn, text) in sessionNotes do
                        if Render.isColorEnabled () then
                            Surface.markupLine($"[dim][[turn {turn}]][/] {Markup.Escape text}")
                        else
                            Surface.writeLine($"[turn {turn}] {text}")
                Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s = "/undo" ->
                match Fugue.Core.Checkpoint.undo () with
                | Fugue.Core.Checkpoint.NothingToUndo ->
                    Surface.markupLine($"[dim]{Markup.Escape liveStrings.UndoNothingToUndo}[/]")
                | Fugue.Core.Checkpoint.Restored path ->
                    Surface.markupLine($"[green]✓[/] {Markup.Escape liveStrings.UndoRestored}: [dim]{Markup.Escape path}[/]")
                | Fugue.Core.Checkpoint.Deleted path ->
                    Surface.markupLine($"[yellow]✓[/] {Markup.Escape liveStrings.UndoDeleted}: [dim]{Markup.Escape path}[/]")
                Surface.lineBreak ()
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
                    Surface.writeRenderable(Render.errorLine liveStrings "rg and grep unavailable")
                    Surface.lineBreak ()
                | Some "" ->
                    Surface.markupLine("[dim]" + Markup.Escape liveStrings.TodoNone + "[/]")
                    Surface.lineBreak ()
                | Some output ->
                    let lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    let truncated, note =
                        if lines.Length > 200 then
                            Array.take 200 lines, $"\n\n[truncated — showing 200 of {lines.Length} matches]"
                        else lines, ""
                    let body = String.concat "\n" truncated
                    let summary =
                        $"Found {lines.Length} TODO/FIXME/HACK items in workspace:\n\n{body}{note}\n\nPlease review these and suggest which ones are worth addressing now."
                    do! streamAndRender agent session summary cfg cancelSrc zenMode ignore
                StatusBar.refresh ()
            | Some s when s.StartsWith "/find " ->
                let query = s.Substring("/find ".Length).Trim()
                if String.IsNullOrWhiteSpace query then
                    Surface.writeRenderable(Markup("[dim]Usage: /find <query>[/]"))
                    Surface.lineBreak ()
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
                        Surface.markupLine($"[dim]{Markup.Escape liveStrings.FindNoResults}[/]")
                    else
                        for (_score, rel, snippet) in results do
                            let snipStr =
                                match snippet with
                                | Some s -> $"  → {s.Trim()}"
                                | None   -> ""
                            if Render.isColorEnabled () then
                                Surface.markupLine($"[cyan]{Markup.Escape rel}[/]{Markup.Escape snipStr}")
                            else
                                Surface.writeLine($"{rel}{snipStr}")
                        Surface.markupLine($"[dim]{Markup.Escape liveStrings.FindInjectHint}[/]")
                    Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/summarize" ->
                let rawArg = s.Substring("/summarize".Length).Trim()
                if System.String.IsNullOrWhiteSpace rawArg then
                    Surface.writeRenderable(Markup("[dim]Usage: /summarize <path>[/]"))
                    Surface.lineBreak ()
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
                    Surface.writeRenderable(Render.userMessage cfg.Ui ("/summarize " + rawArg) 0)
                    Surface.lineBreak ()
                    do! streamAndRender agent session summarizePrompt cfg cancelSrc zenMode ignore
                StatusBar.refresh ()
            | Some s when s = "/check exhaustive" || s.StartsWith "/check exhaustive " ->
                // Run dotnet build and feed FS0025 (incomplete pattern match) warnings back
                let arg = if s = "/check exhaustive" then "." else s.Substring("/check exhaustive ".Length).Trim()
                let buildDir = if arg = "" || arg = "." then cwd else if IO.Path.IsPathRooted arg then arg else IO.Path.Combine(cwd, arg)
                Surface.markupLine "[dim]Running dotnet build to check pattern match exhaustiveness…[/]"
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
                    Surface.markupLine "[green]No FS0025 incomplete pattern match warnings found.[/]"
                    Surface.lineBreak ()
                else
                    let diagnostics = String.concat "\n" fs0025
                    let prompt =
                        $"The build found incomplete pattern match warnings (FS0025):\n\n```\n{diagnostics}\n```\n\nPlease:\n1. Read each affected file.\n2. Add the missing match cases using `failwith \"TODO: <CaseName>\"` as placeholders.\n3. Apply the fixes using the Edit tool.\n\nFix all warnings before responding."
                    Surface.writeRenderable(Render.userMessage cfg.Ui "/check exhaustive" 0)
                    Surface.lineBreak ()
                    do! streamAndRender agent session prompt cfg cancelSrc zenMode ignore
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
                    Surface.markupLine "[bold]Context budget estimate[/] [dim](word-count based, ×1.35)[/]"
                    Surface.markupLine($"  [dim]system prompt[/]  ~{sysTok} tokens")
                    Surface.markupLine($"  [dim]context limit[/]   {contextLimit} tokens (provider estimate)")
                    Surface.markupLine($"  [dim]system usage[/]   {pctUsed}%% of limit")
                    Surface.markupLine "[dim]  (turn history token count requires provider usage reporting)[/]"
                else
                    Surface.writeLine($"System prompt: ~{sysTok} tokens / {contextLimit} limit ({pctUsed}%%)")
                Surface.lineBreak ()
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
                    Surface.markupLine($"[bold]/tokens[/] — provider: [cyan]{prov}[/]  model: [cyan]{friendly}[/]")
                    Surface.markupLine($"  system prompt:  ~{sysTok} tokens")
                    Surface.markupLine "[dim]  Exact per-turn counts require the provider to return usage metadata.[/]"
                    Surface.markupLine "[dim]  Use /context show for a full budget breakdown.[/]"
                else
                    Surface.writeLine($"Provider: {prov}  Model: {friendly}  System: ~{sysTok} tokens")
                Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/locale " || s = "/locale" ->
                let arg = if s = "/locale" then "" else s.Substring("/locale ".Length).Trim()
                if arg = "" then
                    let current = savedUi.Locale
                    if Render.isColorEnabled () then
                        Surface.markupLine($"[dim]Current locale: [cyan]{current}[/]  Usage: /locale [en|ru][/]")
                    else
                        Surface.writeLine($"Current locale: {current}  Usage: /locale [en|ru]")
                elif arg <> "en" && arg <> "ru" then
                    if Render.isColorEnabled () then
                        Surface.markupLine "[dim yellow]Supported locales: en, ru[/]"
                    else
                        Surface.writeLine "Supported locales: en, ru"
                else
                    savedUi <- { savedUi with Locale = arg }
                    liveStrings <- Fugue.Core.Localization.pick arg
                    // Propagate new locale to status bar so its strings re-resolve immediately.
                    StatusBar.setCfg { cfg with Ui = savedUi }
                    try
                        Config.saveToFile { cfg with Ui = savedUi }
                        if Render.isColorEnabled () then
                            Surface.markupLine($"[dim green]Locale set to [cyan]{arg}[/] and saved.[/]")
                        else
                            Surface.writeLine($"Locale set to {arg} and saved.")
                    with ex ->
                        if Render.isColorEnabled () then
                            Surface.markupLine($"[dim yellow]Could not save config: {Markup.Escape ex.Message}[/]")
                        else
                            Surface.writeLine($"Could not save config: {ex.Message}")
                Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/pin " ->
                let msg = s.Substring("/pin ".Length).Trim()
                if msg = "" then
                    if pinnedMessages.IsEmpty then
                        Surface.markupLine "[dim]No pinned messages. Usage: /pin <message>[/]"
                    else
                        Surface.markupLine "[bold]Pinned:[/]"
                        pinnedMessages |> List.iteri (fun i m ->
                            if Render.isColorEnabled () then Surface.markupLine($"  [cyan]{i+1}.[/] {Markup.Escape m}")
                            else Surface.writeLine($"  {i+1}. {m}"))
                else
                    pinnedMessages <- pinnedMessages @ [msg]
                    if Render.isColorEnabled () then
                        Surface.markupLine($"[dim green]Pinned ({pinnedMessages.Length} total): [/]{Markup.Escape msg}")
                    else
                        Surface.writeLine($"Pinned ({pinnedMessages.Length} total): {msg}")
                Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s = "/pin" ->
                if pinnedMessages.IsEmpty then
                    Surface.markupLine "[dim]No pinned messages. Usage: /pin <message>[/]"
                else
                    Surface.markupLine "[bold]Pinned messages:[/]"
                    pinnedMessages |> List.iteri (fun i m ->
                        if Render.isColorEnabled () then Surface.markupLine($"  [cyan]{i+1}.[/] {Markup.Escape m}")
                        else Surface.writeLine($"  {i+1}. {m}"))
                Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s = "/compress" ->
                // Compress context: ask AI to summarise history, then reset session and inject summary.
                Surface.markupLine "[dim]Compressing context — summarising session history…[/]"
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
                        sessionRef.Value <- session
                    with ex ->
                        Surface.writeRenderable(Render.errorLine liveStrings ex.Message)
                        Surface.lineBreak ()
                    let injection = $"[Compressed session context]\n{summary}"
                    do! streamAndRender agent session injection cfg cancelSrc zenMode ignore
                    if Render.isColorEnabled () then
                        Surface.markupLine "[dim]Context compressed — previous history summarised and reset.[/]"
                    else
                        Surface.writeLine "Context compressed — previous history summarised and reset."
                else
                    Surface.markupLine "[dim yellow]Could not generate summary — context unchanged.[/]"
                Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s = "/session offload" ->
                // 1. Flush SessionEnd to JSONL
                Fugue.Core.SessionPersistence.appendRecord sessionFilePath
                    (Fugue.Core.SessionRecord.SessionEnd(DateTimeOffset.UtcNow, turnNumber, 0L))
                // 2. Print session id to user
                let offloadMsg = $"Session saved: {currentSessionId}\nFile: {sessionFilePath}"
                if Render.isColorEnabled () then
                    Surface.markupLine($"[dim]{Markup.Escape offloadMsg}[/]")
                else
                    Surface.writeLine offloadMsg
                Surface.lineBreak ()
                // 3. Reset in-memory history (same pattern as /clear-history)
                match box session with
                | :? IAsyncDisposable as d -> try do! d.DisposeAsync().AsTask() with _ -> ()
                | _ -> ()
                try
                    let! newSession = agent.CreateSessionAsync(CancellationToken.None)
                    session <- newSession
                    sessionRef.Value <- session
                    // 4. Inject synthetic orientation note so agent knows context was offloaded
                    let offloadNote = $"Previous context offloaded to session {currentSessionId}. Use GetConversation to inspect history if needed."
                    do! streamAndRender agent session offloadNote cfg cancelSrc zenMode ignore
                with ex ->
                    Surface.writeRenderable(Render.errorLine liveStrings ex.Message)
                    Surface.lineBreak ()
                // 5. Reset per-turn counters (keep currentSessionId so offload note references it)
                turnNumber <- 0
                sessionNotes <- []
                lastFile <- None
                StatusBar.refresh ()
            | Some s when s.StartsWith "/session resume " ->
                let resumeId = s.Substring("/session resume ".Length).Trim()
                match Fugue.Core.SessionPersistence.findByIdInCwd cwd resumeId with
                | None ->
                    if Render.isColorEnabled () then
                        Surface.markupLine($"[red]Session not found: {Markup.Escape resumeId}[/]")
                    else
                        Surface.writeLine($"Session not found: {resumeId}")
                    Surface.lineBreak ()
                | Some path ->
                    let records = Fugue.Core.SessionPersistence.readRecords path
                    let messages = System.Collections.Generic.List<Microsoft.Extensions.AI.ChatMessage>()
                    for r in records do
                        match r with
                        | Fugue.Core.SessionRecord.UserTurn(_, content) ->
                            messages.Add(Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, content))
                        | Fugue.Core.SessionRecord.AssistantTurn(_, content) ->
                            messages.Add(Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, content))
                        | Fugue.Core.SessionRecord.ToolCall(_, toolName, args, callId) ->
                            // Encode as text prefix to avoid FunctionCallContent AOT issues
                            let text = $"[tool_call:{toolName} call_id:{callId}] {args}"
                            messages.Add(Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, text))
                        | Fugue.Core.SessionRecord.ToolResult(_, callId, output, isError) ->
                            let errorTag = if isError then " [error]" else ""
                            let text = $"[tool_result:{callId}]{errorTag} {output}"
                            messages.Add(Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, text))
                        | _ -> ()  // SessionStart / SessionEnd skipped
                    match box session with
                    | :? IAsyncDisposable as d -> try do! d.DisposeAsync().AsTask() with _ -> ()
                    | _ -> ()
                    try
                        let! newSession = agent.CreateSessionAsync(CancellationToken.None)
                        session <- newSession
                        sessionRef.Value <- session
                        match session with
                        | null -> ()
                        | sess ->
                            let stateKey : string | null = null
                            let jsonOpts : System.Text.Json.JsonSerializerOptions | null = null
                            AgentSessionExtensions.SetInMemoryChatHistory(sess, messages, stateKey, jsonOpts)
                        if Render.isColorEnabled () then
                            Surface.markupLine($"[dim]Resumed session {Markup.Escape resumeId} — {messages.Count} messages loaded.[/]")
                        else
                            Surface.writeLine($"Resumed session {resumeId} — {messages.Count} messages loaded.")
                    with ex ->
                        Surface.writeRenderable(Render.errorLine liveStrings ex.Message)
                    Surface.lineBreak ()
                    StatusBar.refresh ()
            | Some s when s = "/sessions all" ->
                let rows = Fugue.Core.SearchIndex.listAll 50
                if rows.IsEmpty then
                    if Render.isColorEnabled () then Surface.markupLine "[dim]No sessions found.[/]"
                    else Surface.writeLine "No sessions found."
                else
                    if Render.isColorEnabled () then
                        Surface.markupLine $"[bold]All sessions ({rows.Length})[/]"
                        Surface.markupLine "[dim]ID                         Started                   Turns  Project[/]"
                        for r in rows do
                            let sid = if r.Id.Length > 26 then r.Id.[..25] else r.Id.PadRight 26
                            let ts  = r.StartedAt.[..18].PadRight 25
                            let tc  = string r.TurnCount |> fun s -> s.PadLeft 5
                            let cwd = if r.Cwd.Length > 40 then "…" + r.Cwd.[r.Cwd.Length-39..] else r.Cwd
                            Surface.markupLine $"[dim]{Markup.Escape sid}[/]  [cyan]{Markup.Escape ts}[/]  {tc}  [dim]{Markup.Escape cwd}[/]"
                    else
                        Surface.writeLine $"All sessions ({rows.Length})"
                        Surface.writeLine "ID                         Started                   Turns  Project"
                        for r in rows do
                            let sid = if r.Id.Length > 26 then r.Id.[..25] else r.Id.PadRight 26
                            let ts  = r.StartedAt.[..18].PadRight 25
                            let tc  = string r.TurnCount |> fun s -> s.PadLeft 5
                            let cwd = if r.Cwd.Length > 40 then "…" + r.Cwd.[r.Cwd.Length-39..] else r.Cwd
                            Surface.writeLine $"{sid}  {ts}  {tc}  {cwd}"
                Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s = "/sessions" ->
                let rows = Fugue.Core.SearchIndex.listForCwd cwd 20
                if rows.IsEmpty then
                    if Render.isColorEnabled () then Surface.markupLine "[dim]No sessions for this project.[/]"
                    else Surface.writeLine "No sessions for this project."
                else
                    if Render.isColorEnabled () then
                        Surface.markupLine $"[bold]Sessions for {Markup.Escape cwd} ({rows.Length})[/]"
                        Surface.markupLine "[dim]ID                         Started                   Turns  Summary[/]"
                        for r in rows do
                            let sid = if r.Id.Length > 26 then r.Id.[..25] else r.Id.PadRight 26
                            let ts  = r.StartedAt.[..18].PadRight 25
                            let tc  = string r.TurnCount |> fun s -> s.PadLeft 5
                            let sum = r.Summary |> Option.map (fun s -> if s.Length > 50 then s.[..49] + "…" else s) |> Option.defaultValue ""
                            Surface.markupLine $"[dim]{Markup.Escape sid}[/]  [cyan]{Markup.Escape ts}[/]  {tc}  [dim]{Markup.Escape sum}[/]"
                    else
                        Surface.writeLine $"Sessions for {cwd} ({rows.Length})"
                        Surface.writeLine "ID                         Started                   Turns  Summary"
                        for r in rows do
                            let sid = if r.Id.Length > 26 then r.Id.[..25] else r.Id.PadRight 26
                            let ts  = r.StartedAt.[..18].PadRight 25
                            let tc  = string r.TurnCount |> fun s -> s.PadLeft 5
                            let sum = r.Summary |> Option.map (fun s -> if s.Length > 50 then s.[..49] + "…" else s) |> Option.defaultValue ""
                            Surface.writeLine $"{sid}  {ts}  {tc}  {sum}"
                Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s = "/index" || s = "/index --update" ->
                let cfg  = Fugue.Core.Index.defaultIndexConfig
                let prov =
                    match cfg.ProviderKind.ToLowerInvariant() with
                    | "lmstudio" ->
                        Fugue.Agent.Indexer.LmStudio(cfg.LmStudioBaseUrl |> Option.defaultValue "http://localhost:1234")
                    | _ ->
                        Fugue.Agent.Indexer.Ollama cfg.OllamaBaseUrl
                let runFn = if s = "/index --update" then Fugue.Agent.Indexer.runIncrementalScan else Fugue.Agent.Indexer.runFullScan
                Surface.markupLine "[dim]▸ Indexing workspace…[/]"
                let (n, fails) = Async.RunSynchronously (runFn cfg prov cwd)
                if fails > 0 then
                    Surface.markupLine $"[yellow]✓[/] {n} files indexed, {fails} embedding failures (check provider URL)"
                else
                    Surface.markupLine $"[green]✓[/] {n} files indexed"
                StatusBar.refresh ()
            | Some s when s.StartsWith "/sem " ->
                let q = s.Substring("/sem ".Length).Trim()
                if q = "" then
                    if Render.isColorEnabled () then Surface.markupLine "[yellow]Usage: /sem <query>[/]"
                    else Surface.writeLine "Usage: /sem <query>"
                else
                    let chunksFile = Fugue.Core.Index.chunksPath cwd
                    let chunks = Fugue.Core.Index.loadChunks chunksFile
                    if chunks.IsEmpty then
                        if Render.isColorEnabled () then Surface.markupLine "[dim]No workspace index. Run /index to build one.[/]"
                        else Surface.writeLine "No workspace index. Run /index to build one."
                    else
                        // Embed query then rank by cosine similarity
                        let cfg  = Fugue.Core.Index.defaultIndexConfig
                        let prov =
                            match cfg.ProviderKind.ToLowerInvariant() with
                            | "lmstudio" ->
                                Fugue.Agent.Indexer.LmStudio(cfg.LmStudioBaseUrl |> Option.defaultValue "http://localhost:1234")
                            | _ ->
                                Fugue.Agent.Indexer.Ollama cfg.OllamaBaseUrl
                        use http = new System.Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds 10.0)
                        let vecOpt = Async.RunSynchronously (Fugue.Agent.Indexer.embedAsync prov cfg.EmbeddingModel http q |> Async.AwaitTask)
                        match vecOpt with
                        | None ->
                            if Render.isColorEnabled () then Surface.markupLine "[red]Embedding failed — check provider URL/model[/]"
                            else Surface.writeLine "Embedding failed — check provider URL/model"
                        | Some qVec ->
                            let top5 =
                                chunks
                                |> List.map (fun c -> c, Fugue.Core.Index.cosineSimilarity qVec c.Vector)
                                |> List.sortByDescending snd
                                |> List.truncate 5
                            if Render.isColorEnabled () then
                                Surface.markupLine $"[bold]Semantic results for «{Markup.Escape q}» ({top5.Length})[/]"
                                for (c, score) in top5 do
                                    Surface.markupLine $"[cyan]{Markup.Escape c.FilePath}[/] L{c.StartLine}-{c.EndLine}  [dim]score={score:F3}[/]"
                                    let preview = if c.Text.Length > 120 then c.Text.[..119] + "…" else c.Text
                                    Surface.markupLine $"  [dim]{Markup.Escape preview}[/]"
                            else
                                Surface.writeLine $"Semantic results for «{q}» ({top5.Length})"
                                for (c, score) in top5 do
                                    Surface.writeLine $"{c.FilePath} L{c.StartLine}-{c.EndLine}  score={score:F3}"
                                    let preview = if c.Text.Length > 120 then c.Text.[..119] + "…" else c.Text
                                    Surface.writeLine $"  {preview}"
                Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/search " ->
                let q = s.Substring("/search ".Length).Trim()
                if q = "" then
                    if Render.isColorEnabled () then Surface.markupLine "[yellow]Usage: /search <query>[/]"
                    else Surface.writeLine "Usage: /search <query>"
                else
                    let hits = Fugue.Core.SearchIndex.search q 10
                    if hits.IsEmpty then
                        if Render.isColorEnabled () then Surface.markupLine $"[dim]No results for: {Markup.Escape q}[/]"
                        else Surface.writeLine $"No results for: {q}"
                    else
                        if Render.isColorEnabled () then
                            Surface.markupLine $"[bold]Search results for «{Markup.Escape q}» ({hits.Length})[/]"
                            for h in hits do
                                let ts  = h.StartedAt.[..18]
                                let cw  = if h.Cwd.Length > 35 then "…" + h.Cwd.[h.Cwd.Length-34..] else h.Cwd
                                Surface.markupLine $"[dim]{Markup.Escape h.SessionId.[..25]}[/]  [cyan]{Markup.Escape ts}[/]  [dim]{Markup.Escape cw}[/]"
                                Surface.markupLine $"  {Markup.Escape h.Snippet}"
                        else
                            Surface.writeLine $"Search results for «{q}» ({hits.Length})"
                            for h in hits do
                                let ts  = h.StartedAt.[..18]
                                let cw  = if h.Cwd.Length > 35 then "…" + h.Cwd.[h.Cwd.Length-34..] else h.Cwd
                                Surface.writeLine $"{h.SessionId.[..25]}  {ts}  {cw}"
                                Surface.writeLine $"  {h.Snippet}"
                Surface.lineBreak ()
                StatusBar.refresh ()
            | Some s when s.StartsWith "/" ->
                // Generic prompt-template dispatcher — catches any /command not handled above.
                // Tries compound-name lookup first (e.g. "/scaffold cqrs Foo" → "scaffold-cqrs"),
                // then falls back to the simple command name ("scaffold").
                let body = s.[1..]
                let spaceIdx = body.IndexOf(' ')
                let cmd0, rawArgs0 =
                    if spaceIdx < 0 then body, ""
                    else body.[..spaceIdx-1], body.[spaceIdx+1..].Trim()
                // Attempt compound: cmd0 + "-" + firstWord(rawArgs0)
                let cmd, rawArgs =
                    if rawArgs0 = "" then cmd0, ""
                    else
                        let nextSpace = rawArgs0.IndexOf(' ')
                        let firstWord, rest =
                            if nextSpace < 0 then rawArgs0, ""
                            else rawArgs0.[..nextSpace-1], rawArgs0.[nextSpace+1..].Trim()
                        let compound = $"{cmd0}-{firstWord}"
                        match PromptRegistry.find compound with
                        | Some _ -> compound, rest
                        | None   -> cmd0, rawArgs0
                match PromptRegistry.find cmd with
                | None ->
                    // Unknown slash command — show hint
                    if Render.isColorEnabled () then
                        Surface.writeRenderable(Markup $"[dim yellow]Unknown command: /{Markup.Escape cmd}. Type /help for a list.[/]")
                    else
                        Surface.writeLine $"Unknown command: /{cmd}. Type /help for a list."
                    Surface.lineBreak ()
                    StatusBar.refresh ()
                | Some tmpl ->
                    // Build args map.
                    // Special cases for commands that read files or have multi-arg parsing.
                    let argsMap =
                        match cmd with
                        | "refactor-pipeline" ->
                            // /refactor pipeline [file:start-end] — parse range
                            let arg = rawArgs
                            if arg = "" then Map.empty
                            else
                                let colonIdx = arg.LastIndexOf ':'
                                let code, label =
                                    if colonIdx > 0 then
                                        let filePart  = arg.[..colonIdx-1]
                                        let rangePart = arg.[colonIdx+1..]
                                        let fullPath  = if IO.Path.IsPathRooted filePart then filePart else IO.Path.GetFullPath(IO.Path.Combine(cwd, filePart))
                                        if IO.File.Exists fullPath then
                                            let lines = IO.File.ReadAllLines fullPath
                                            let parsed =
                                                let parts = rangePart.Split '-'
                                                if parts.Length = 2 then
                                                    match System.Int32.TryParse parts.[0], System.Int32.TryParse parts.[1] with
                                                    | (true, startL), (true, endL) -> Some (max 0 (startL-1), min (lines.Length-1) (endL-1))
                                                    | _ -> None
                                                else None
                                            match parsed with
                                            | Some (startL, endL) -> String.concat "\n" lines.[startL..endL], $"{filePart}:{startL+1}-{endL+1}"
                                            | None                -> String.concat "\n" lines, filePart
                                        else "", ""
                                    else
                                        let fullPath = if IO.Path.IsPathRooted arg then arg else IO.Path.GetFullPath(IO.Path.Combine(cwd, arg))
                                        if IO.File.Exists fullPath then IO.File.ReadAllText fullPath, arg else "", ""
                                Map.ofList ["code", code; "label", label]
                        | "port" ->
                            // /port <file> [target-lang]
                            let parts = rawArgs.Split(' ') |> Array.toList
                            if parts.IsEmpty || parts.Head = "" then Map.empty
                            else
                                let file   = parts.Head
                                let target = if parts.Length > 1 then String.concat " " parts.Tail else "F#"
                                let fullPath = if IO.Path.IsPathRooted file then file else IO.Path.GetFullPath(IO.Path.Combine(cwd, file))
                                let code = if IO.File.Exists fullPath then IO.File.ReadAllText fullPath else file
                                Map.ofList ["file", file; "code", code; "target", target]
                        | c when List.contains c ["check-tail-rec"; "check-effects"; "migrate-oop-to-fp"] ->
                            // File-reading commands: read file into {code}, keep {file}
                            let arg = rawArgs
                            if arg = "" then Map.empty
                            else
                                let fullPath = if IO.Path.IsPathRooted arg then arg else IO.Path.GetFullPath(IO.Path.Combine(cwd, arg))
                                let code = if IO.File.Exists fullPath then IO.File.ReadAllText fullPath else arg
                                Map.ofList ["file", arg; "code", code]
                        | "complexity" ->
                            // Default target to "." when no arg given
                            let target = if rawArgs = "" then "." else rawArgs
                            Map.ofList ["target", target]
                        | _ ->
                            // Generic single-arg: map rawArgs to the first declared arg name (or "arg")
                            let argName =
                                match tmpl.Args with
                                | first :: _ when first <> "" -> first
                                | _                           -> "arg"
                            Map.ofList [argName, rawArgs]
                    // Validate: if the primary arg is required and empty, show usage
                    let primaryArg =
                        match tmpl.Args with
                        | first :: _ -> Map.tryFind first argsMap |> Option.defaultValue ""
                        | []         -> rawArgs
                    let codeArg = Map.tryFind "code" argsMap |> Option.defaultValue ""
                    // Commands that need code must have it; commands that just need a label/file check rawArgs
                    let needsCode = List.contains cmd ["check-tail-rec"; "check-effects"; "migrate-oop-to-fp"; "refactor-pipeline"]
                    let isEmpty =
                        if needsCode then codeArg.Trim() = ""
                        else primaryArg = ""
                    if isEmpty then
                        let argList = tmpl.Args |> List.map (fun a -> $"<{a}>") |> String.concat " "
                        let usage = if argList = "" then $"/{cmd}" else $"/{cmd.Replace('-', ' ')} {argList}"
                        if Render.isColorEnabled () then
                            Surface.writeRenderable(Markup $"[dim]Usage: {Markup.Escape usage}[/]")
                        else
                            Surface.write $"Usage: {usage}"
                        Surface.lineBreak ()
                    else
                        let prompt = PromptRegistry.render tmpl argsMap
                        Surface.writeRenderable(Render.userMessage cfg.Ui s 0)
                        Surface.lineBreak ()
                        do! streamAndRender agent session prompt cfg cancelSrc zenMode ignore
                    StatusBar.refresh ()
            | Some userInput ->
                let userInput = ReadLine.normalizeInput userInput
                if ReadLine.hasZeroWidth userInput then
                    Surface.writeRenderable(Markup("[dim yellow]" + Markup.Escape liveStrings.ZeroWidthWarning + "[/]"))
                    Surface.lineBreak ()
                let expandedInput = expandAtFiles cwd liveStrings userInput
                let expandedInput = expandClipboard expandedInput liveStrings
                let expandedInput = expandClipRing expandedInput
                StatusBar.recordWords (expandedInput.Split([|' '; '\n'; '\r'; '\t'|], StringSplitOptions.RemoveEmptyEntries).Length)
                Surface.writeRenderable(Render.userMessage cfg.Ui userInput (turnNumber + 1))
                Surface.lineBreak ()
                let expandedInput =
                    match Fugue.Core.ConversationIntent.classify lastResponseWasPlan expandedInput with
                    | Fugue.Core.ConversationIntent.Affirmation ->
                        if Render.isColorEnabled () then Surface.markupLine "[dim]↳ proceeding with plan[/]"
                        else Surface.writeLine "↳ proceeding with plan"
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
                // Fire UserPromptSubmit hooks before dispatching to the agent.
                let! hookResult = Fugue.Core.Hooks.runUserPromptSubmit effectiveInput currentSessionId hooksConfig
                do!
                    match hookResult with
                    | Fugue.Core.Hooks.BlockPrompt reason ->
                        task {
                            if Render.isColorEnabled () then
                                Surface.markupLine $"[yellow]⊘ prompt blocked by hook:[/] {Markup.Escape reason}"
                            else
                                Surface.writeLine $"⊘ prompt blocked by hook: {reason}"
                            Surface.lineBreak ()
                        }
                    | _ ->
                        let effectiveInput =
                            match hookResult with
                            | Fugue.Core.Hooks.Replace newPrompt -> newPrompt
                            | Fugue.Core.Hooks.Append  extra     -> effectiveInput + "\n" + extra
                            | _                                  -> effectiveInput
                        turnNumber <- turnNumber + 1
                        task {
                            // Record UserTurn with the EFFECTIVE input — i.e., the post-hook
                            // text the agent actually saw. On /session resume, replaying this
                            // record reconstructs the same agent state including any hook
                            // Replace/Append transformations. The user's raw typed text is
                            // already echoed to the terminal via Render.userMessage at type-time;
                            // JSONL captures the durable agent-processed view.
                            Fugue.Core.SessionPersistence.appendRecord sessionFilePath
                                (Fugue.Core.SessionRecord.UserTurn(DateTimeOffset.UtcNow, effectiveInput))
                            let persistFn = Fugue.Core.SessionPersistence.appendRecord sessionFilePath
                            // persistFn is passed as recordFn so streamAndRender emits AssistantTurn with full content.
                            do! streamAndRender agent session effectiveInput cfg cancelSrc zenMode persistFn
                            StatusBar.refresh ()
                            // Nudge if the same tool error has recurred across sessions.
                            let recurring = Fugue.Core.ErrorTrend.findRecurring 3 14
                            match recurring with
                            | (sig', count) :: _ ->
                                let short = if sig'.Length > 80 then sig'.[..79] + "…" else sig'
                                if Render.isColorEnabled () then
                                    Surface.markupLine($"[dim yellow]⚠ recurring error ({count}×): {Markup.Escape short} — type [bold]/report-bug[/] to file a report[/]")
                                else
                                    Surface.writeLine($"⚠ recurring error ({count}×): {short} — type /report-bug to file a report")
                            | [] -> ()
                        }
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
        Surface.write "\x1b[?2004l"   // disable bracketed paste
        let nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        let durationMs = nowMs - sessionStartedAt
        // Append SessionEnd record to JSONL file (best-effort).
        Fugue.Core.SessionPersistence.appendRecord sessionFilePath
            (Fugue.Core.SessionRecord.SessionEnd(DateTimeOffset.UtcNow, turnNumber, durationMs))
        // Batch-index this session into FTS5 (best-effort; failure is silent — reindex recovers).
        try
            let idxRow : Fugue.Core.SearchIndex.SessionRow = {
                Id        = currentSessionId
                Cwd       = cwd
                StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(sessionStartedAt).ToString("o")
                EndedAt   = Some (DateTimeOffset.UtcNow.ToString("o"))
                TurnCount = turnNumber
                Summary   = None
            }
            Fugue.Core.SearchIndex.upsertSession idxRow
            let content =
                Fugue.Core.SessionPersistence.readRecords sessionFilePath
                |> List.choose (function
                    | Fugue.Core.SessionRecord.UserTurn(_, c)                         -> Some c
                    | Fugue.Core.SessionRecord.AssistantTurn(_, c) when c <> ""      -> Some c
                    | Fugue.Core.SessionRecord.ToolCall(_, n, a, _)                  -> Some $"{n} {a}"
                    | _                                                               -> None)
                |> String.concat " "
            Fugue.Core.SearchIndex.upsertContent currentSessionId content
        with _ -> ()
        // Fire SessionEnd hooks (best-effort; failures are logged, never surfaced).
        let sessionEndPayload =
            Fugue.Core.Hooks.sessionEndPayload cwd durationMs turnNumber
        try
            (Fugue.Core.Hooks.runLifecycle
                Fugue.Core.Hooks.HookEvent.SessionEnd
                sessionEndPayload
                hooksConfig).Wait()
        with ex ->
            eprintfn $"[hooks] SessionEnd hook error: {ex.Message}"
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
                    Surface.write t
                | Conversation.ToolStarted(_, name, args) ->
                    Console.Error.WriteLine($"[tool: {name}({args})]")
                | Conversation.ToolCompleted(id, output, isErr) ->
                    ignore id
                    if isErr then Console.Error.WriteLine($"[tool error: {output}]")
                | Conversation.Finished -> ()
                | Conversation.Failed ex -> raise ex
            else hasNext <- false
        Surface.lineBreak ()
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
                    Surface.write t
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
        Surface.lineBreak ()
        return exitCode
}
