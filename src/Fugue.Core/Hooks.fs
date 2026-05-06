module Fugue.Core.Hooks

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

// ── Types ────────────────────────────────────────────────────────────────────

type HookEvent =
    | SessionStart
    | SessionEnd
    | PreToolUse
    | PostToolUse
    | UserPromptSubmit

type OnError =
    | LogContinue   // default: log warning and keep going
    | LogBlock      // propagate failure to caller

type HookDef =
    { Command   : string
      Async     : bool
      TimeoutMs : int option     // None → use global default
      Matcher   : string option  // None = match all tools; "*"/"" = match all
    }

type ContextProvider =
    { Name       : string
      Command    : string
      TtlSeconds : int }   // 0 = no cache

type HooksConfig =
    { HookTimeoutMs    : int          // global default (ms)
      OnError          : OnError
      SessionStart     : HookDef list
      SessionEnd       : HookDef list
      PreToolUse       : HookDef list
      PostToolUse      : HookDef list
      UserPromptSubmit : HookDef list
      ContextProviders : ContextProvider list }

let defaultConfig : HooksConfig =
    { HookTimeoutMs    = 5_000
      OnError          = LogContinue
      SessionStart     = []
      SessionEnd       = []
      PreToolUse       = []
      PostToolUse      = []
      UserPromptSubmit = []
      ContextProviders = [] }

// ── PreToolResult ─────────────────────────────────────────────────────────────

/// Result returned by runPreToolUse.
type PreToolResult =
    | Proceed                                         // proceed with original args
    | Block       of reason : string                  // abort tool call; return reason to LLM
    | ModifyArgs  of changes : (string * string) list // key/rawJsonValue pairs to overwrite

// ── UserPromptResult ──────────────────────────────────────────────────────────

/// Result returned by runUserPromptSubmit.
type UserPromptResult =
    | PassThrough              // pass original prompt unchanged
    | Replace of string        // substitute the prompt entirely
    | Append  of string        // append text to the prompt (newline-separated)
    | BlockPrompt of string    // cancel the turn; display reason to user

// ── Payload builders ─────────────────────────────────────────────────────────
// AOT-safe: manual JSON building, no reflection, no STJ serialization.

let private esc (s: string) : string =
    s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r")

/// Build JSON payload for SessionStart.
let sessionStartPayload (cwd: string) (model: string) (provider: string) (sessionId: string) : string =
    let q = "\""
    "{" + q + "event" + q + ":" + q + "SessionStart" + q +
    "," + q + "version" + q + ":1" +
    "," + q + "cwd" + q + ":" + q + esc cwd + q +
    "," + q + "model" + q + ":" + q + esc model + q +
    "," + q + "provider" + q + ":" + q + esc provider + q +
    "," + q + "sessionId" + q + ":" + q + esc sessionId + q +
    "}"

/// Build JSON payload for SessionEnd.
let sessionEndPayload (cwd: string) (durationMs: int64) (turnCount: int) : string =
    let q = "\""
    "{" + q + "event" + q + ":" + q + "SessionEnd" + q +
    "," + q + "version" + q + ":1" +
    "," + q + "cwd" + q + ":" + q + esc cwd + q +
    "," + q + "durationMs" + q + ":" + string durationMs +
    "," + q + "turnCount" + q + ":" + string turnCount +
    "}"

/// Build JSON payload for PreToolUse.
let preToolUsePayload (toolName: string) (argsJson: string) (sessionId: string) : string =
    let q = "\""
    "{" + q + "event" + q + ":" + q + "PreToolUse" + q +
    "," + q + "version" + q + ":1" +
    "," + q + "tool" + q + ":" + q + esc toolName + q +
    "," + q + "args" + q + ":" + argsJson +
    "," + q + "sessionId" + q + ":" + q + esc sessionId + q +
    "}"

/// Build JSON payload for PostToolUse.
let postToolUsePayload (toolName: string) (argsJson: string) (result: string) (isError: bool) (sessionId: string) : string =
    let q = "\""
    "{" + q + "event" + q + ":" + q + "PostToolUse" + q +
    "," + q + "version" + q + ":1" +
    "," + q + "tool" + q + ":" + q + esc toolName + q +
    "," + q + "args" + q + ":" + argsJson +
    "," + q + "result" + q + ":" + q + esc result + q +
    "," + q + "isError" + q + ":" + (if isError then "true" else "false") +
    "," + q + "sessionId" + q + ":" + q + esc sessionId + q +
    "}"

/// Build JSON payload for UserPromptSubmit.
let userPromptSubmitPayload (prompt: string) (sessionId: string) : string =
    let q = "\""
    "{" + q + "event" + q + ":" + q + "UserPromptSubmit" + q +
    "," + q + "version" + q + ":1" +
    "," + q + "prompt" + q + ":" + q + esc prompt + q +
    "," + q + "sessionId" + q + ":" + q + esc sessionId + q +
    "}"

// ── Config loading ────────────────────────────────────────────────────────────

let private hooksPath () =
    let home =
        match Environment.GetEnvironmentVariable "HOME" |> Option.ofObj with
        | Some h when h <> "" -> h
        | _ ->
            match Environment.GetEnvironmentVariable "USERPROFILE" |> Option.ofObj with
            | Some h -> h
            | None -> "."
    Path.Combine(home, ".fugue", "hooks.json")

/// Load from ~/.fugue/hooks.json. Returns defaultConfig (no-op) when the file is absent.
/// Uses JsonDocument (AOT-safe; no reflection) for parsing.
let load () : HooksConfig =
    let path = hooksPath ()
    if not (File.Exists path) then defaultConfig
    else
        try
            use doc = JsonDocument.Parse(File.ReadAllText path)
            let root = doc.RootElement

            let getInt (el: JsonElement) (key: string) (def: int) =
                let mutable v = Unchecked.defaultof<JsonElement>
                if el.TryGetProperty(key, &v) && v.ValueKind = JsonValueKind.Number then
                    match v.TryGetInt32() with
                    | true, n when n > 0 -> n
                    | _ -> def
                else def

            let getString (el: JsonElement) (key: string) (def: string) =
                let mutable v = Unchecked.defaultof<JsonElement>
                if el.TryGetProperty(key, &v) && v.ValueKind = JsonValueKind.String then
                    v.GetString() |> Option.ofObj |> Option.defaultValue def
                else def

            let getBool (el: JsonElement) (key: string) (def: bool) =
                let mutable v = Unchecked.defaultof<JsonElement>
                if el.TryGetProperty(key, &v) then
                    match v.ValueKind with
                    | JsonValueKind.True  -> true
                    | JsonValueKind.False -> false
                    | _ -> def
                else def

            let globalTimeout = getInt root "hookTimeoutMs" 5_000

            let onError =
                match getString root "onError" "log-continue" with
                | "log-block" -> LogBlock
                | _           -> LogContinue

            // Warn if a matcher contains glob/regex metacharacters reserved for future extension.
            let warnMatcher (m: string) =
                if m.IndexOfAny([| '|'; '?'; '[' |]) >= 0 then
                    eprintfn $"[hooks] matcher \"{m}\" contains reserved metacharacters (|, ?, [) — only exact match is supported in this version; hook may not fire as expected"

            // Parse an array of hook definitions under a given event key
            let parseHooks (eventKey: string) : HookDef list =
                let mutable hooksEl = Unchecked.defaultof<JsonElement>
                let mutable eventEl = Unchecked.defaultof<JsonElement>
                if root.TryGetProperty("hooks", &hooksEl)
                   && hooksEl.ValueKind = JsonValueKind.Object
                   && hooksEl.TryGetProperty(eventKey, &eventEl)
                   && eventEl.ValueKind = JsonValueKind.Array
                then
                    [ for item in eventEl.EnumerateArray() do
                        if item.ValueKind = JsonValueKind.Object then
                            let typ = getString item "type" ""
                            if typ = "command" then
                                let cmd = getString item "command" ""
                                if cmd <> "" then
                                    let async' = getBool item "async" false
                                    let perHookTimeout =
                                        let mutable tv = Unchecked.defaultof<JsonElement>
                                        if item.TryGetProperty("timeoutMs", &tv)
                                           && tv.ValueKind = JsonValueKind.Number
                                        then
                                            match tv.TryGetInt32() with
                                            | true, n when n > 0 -> Some n
                                            | _ -> None
                                        else None
                                    let matcher =
                                        let mutable mv = Unchecked.defaultof<JsonElement>
                                        if item.TryGetProperty("matcher", &mv) && mv.ValueKind = JsonValueKind.String then
                                            let m = mv.GetString() |> Option.ofObj |> Option.defaultValue ""
                                            warnMatcher m
                                            Some m
                                        else None
                                    yield { Command = cmd; Async = async'; TimeoutMs = perHookTimeout; Matcher = matcher } ]
                else []

            let parseHooksPostToolUse (eventKey: string) : HookDef list =
                // PostToolUse hooks cannot block: warn if async=false is set
                let hooks = parseHooks eventKey
                hooks |> List.map (fun def ->
                    if not def.Async then
                        eprintfn $"[hooks] PostToolUse hook \"{def.Command}\" has async:false — PostToolUse is always fire-and-forget; treating as async"
                        { def with Async = true }
                    else def)

            let parseContextProviders () : ContextProvider list =
                let mutable cpEl = Unchecked.defaultof<JsonElement>
                if root.TryGetProperty("contextProviders", &cpEl)
                   && cpEl.ValueKind = JsonValueKind.Array
                then
                    [ for item in cpEl.EnumerateArray() do
                        if item.ValueKind = JsonValueKind.Object then
                            let name    = getString item "name"    ""
                            let command = getString item "command" ""
                            if name <> "" && command <> "" then
                                let ttl = getInt item "ttlSeconds" 0
                                yield { Name = name; Command = command; TtlSeconds = ttl } ]
                else []

            { HookTimeoutMs    = globalTimeout
              OnError          = onError
              SessionStart     = parseHooks "SessionStart"
              SessionEnd       = parseHooks "SessionEnd"
              PreToolUse       = parseHooks "PreToolUse"
              PostToolUse      = parseHooksPostToolUse "PostToolUse"
              UserPromptSubmit = parseHooks "UserPromptSubmit"
              ContextProviders = parseContextProviders () }
        with ex ->
            eprintfn $"[hooks] failed to load {path}: {ex.Message}"
            defaultConfig

// ── Execution ─────────────────────────────────────────────────────────────────

/// Expand ~ in command path (AOT-safe).
let private expandHome (cmd: string) : string =
    if cmd.StartsWith("~/", StringComparison.Ordinal) then
        let home =
            match Environment.GetEnvironmentVariable "HOME" |> Option.ofObj with
            | Some h when h <> "" -> h
            | _ ->
                match Environment.GetEnvironmentVariable "USERPROFILE" |> Option.ofObj with
                | Some h -> h
                | None -> ""
        if home = "" then cmd
        else Path.Combine(home, cmd.Substring(2))
    else cmd

/// Spawn the hook process and write the payload to its stdin, then close stdin.
/// Returns the Process object (already started).
///
/// Wraps the user-supplied command in a shell so that paths containing
/// spaces (`/home/my user/hook.sh`), shell metacharacters (`|`, `&&`, env
/// vars), and inline arguments all work without bespoke quoting.
/// On Windows uses `pwsh -EncodedCommand` (UTF-16-LE base64 — same pattern
/// as BashTool); on Unix uses `/bin/sh -c`.
let private spawnHook (command: string) (payloadJson: string) : Process =
    let expanded = expandHome command
    let psi =
        if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
            let encoded =
                expanded
                |> Encoding.Unicode.GetBytes
                |> Convert.ToBase64String
            ProcessStartInfo("pwsh", $"-NoProfile -NoLogo -EncodedCommand {encoded}")
        else
            ProcessStartInfo("/bin/sh", "-c \"" + expanded.Replace("\"", "\\\"") + "\"")
    psi.UseShellExecute        <- false
    psi.RedirectStandardInput  <- true
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError  <- true
    psi.CreateNoWindow         <- true
    let proc =
        match Process.Start(psi) with
        | null -> failwith $"[hooks] Process.Start returned null for: {expanded}"
        | p    -> p
    // Write payload to stdin, close so the hook sees EOF.
    proc.StandardInput.WriteLine(payloadJson)
    proc.StandardInput.Close()
    proc

/// Run a single sync hook: wait up to timeout, drain stderr to eprintfn, return exit code.
let private runSyncHook (def: HookDef) (payloadJson: string) (globalTimeout: int) : int =
    let timeout = def.TimeoutMs |> Option.defaultValue globalTimeout
    let proc = spawnHook def.Command payloadJson
    use _ = proc
    // Drain stderr concurrently to prevent deadlock on full pipe buffers.
    let stderrTask = Task.Run(fun () -> proc.StandardError.ReadToEnd())
    let stdoutTask = Task.Run(fun () -> proc.StandardOutput.ReadToEnd() |> ignore)
    let completed = proc.WaitForExit(timeout)
    if not completed then
        try proc.Kill(entireProcessTree = true) with _ -> ()
        stderrTask.Wait()
        stdoutTask.Wait()
        -1   // sentinel for timeout
    else
        let stderr = stderrTask.Result
        stdoutTask.Wait()
        if not (String.IsNullOrWhiteSpace stderr) then
            eprintfn $"[hooks] {def.Command} stderr: {stderr.TrimEnd()}"
        proc.ExitCode

/// Run a single sync hook and capture stdout. Drains stdout AND stderr concurrently
/// to prevent deadlock on full pipe buffers (>4 KB). Returns (exitCode, stdout).
let internal runSyncHookWithStdout (def: HookDef) (payloadJson: string) (globalTimeout: int) : int * string =
    let timeout = def.TimeoutMs |> Option.defaultValue globalTimeout
    let proc = spawnHook def.Command payloadJson
    use _ = proc
    // Drain both streams concurrently — never read sequentially after WaitForExit.
    let stderrTask = Task.Run(fun () -> proc.StandardError.ReadToEnd())
    let stdoutTask = Task.Run(fun () -> proc.StandardOutput.ReadToEnd())
    let completed = proc.WaitForExit(timeout)
    if not completed then
        try proc.Kill(entireProcessTree = true) with _ -> ()
        stderrTask.Wait()
        stdoutTask.Wait()
        -1, ""   // sentinel for timeout
    else
        let stderr = stderrTask.Result
        let stdout = stdoutTask.Result
        if not (String.IsNullOrWhiteSpace stderr) then
            eprintfn $"[hooks] {def.Command} stderr: {stderr.TrimEnd()}"
        proc.ExitCode, stdout

/// Fire-and-forget async hook: spawn, write stdin, abandon.
let private fireAsyncHook (def: HookDef) (payloadJson: string) : unit =
    try
        let proc = spawnHook def.Command payloadJson
        // Drain stdout/stderr in background to prevent pipe buffer full / zombie.
        Task.Run(fun () ->
            try
                proc.StandardOutput.ReadToEnd() |> ignore
                proc.StandardError.ReadToEnd()  |> ignore
                proc.WaitForExit()
                proc.Dispose()
            with _ -> ()
        ) |> ignore
    with ex ->
        eprintfn $"[hooks] async hook spawn failed ({def.Command}): {ex.Message}"

/// Run all hooks for a lifecycle event.
/// payloadJson — pre-serialized JSON to pass via stdin.
let runLifecycle (event: HookEvent) (payloadJson: string) (config: HooksConfig) : Task<unit> =
    let hooks =
        match event with
        | SessionStart -> config.SessionStart
        | SessionEnd   -> config.SessionEnd
        | PreToolUse | PostToolUse | UserPromptSubmit -> []  // handled by dedicated run* functions
    if hooks.IsEmpty then Task.FromResult(())
    else task {
        for def in hooks do
            if def.Async then
                fireAsyncHook def payloadJson
            else
                let exitCode =
                    try runSyncHook def payloadJson config.HookTimeoutMs
                    with ex ->
                        eprintfn $"[hooks] hook failed ({def.Command}): {ex.Message}"
                        if config.OnError = LogBlock then reraise ()
                        -2
                let failed = exitCode <> 0
                if failed then
                    let reason =
                        if exitCode = -1 then $"timed out after {def.TimeoutMs |> Option.defaultValue config.HookTimeoutMs} ms"
                        else $"exited with code {exitCode}"
                    if config.OnError = LogBlock then
                        failwith $"[hooks] hook {def.Command} {reason}"
                    else
                        eprintfn $"[hooks] hook {def.Command} {reason} (continuing)"
    }

// ── Matcher ───────────────────────────────────────────────────────────────────

/// True if the hook's matcher applies to the given toolName.
let private matchesTool (def: HookDef) (toolName: string) : bool =
    match def.Matcher with
    | None | Some "*" | Some "" -> true
    | Some m -> m = toolName

// ── runPreToolUse / runPostToolUse ───────────────────────────────────────────

/// Parse hook stdout JSON and accumulate into the running PreToolResult.
/// block:true overrides everything; modified_args accumulates from all hooks.
let private parsePreResult (stdout: string) (current: PreToolResult) : PreToolResult =
    if String.IsNullOrWhiteSpace stdout then current
    else
        try
            use doc = JsonDocument.Parse(stdout)
            let root = doc.RootElement
            let mutable blockEl = Unchecked.defaultof<JsonElement>
            if root.TryGetProperty("block", &blockEl) && blockEl.ValueKind = JsonValueKind.True then
                let mutable reasonEl = Unchecked.defaultof<JsonElement>
                let reason =
                    if root.TryGetProperty("reason", &reasonEl) && reasonEl.ValueKind = JsonValueKind.String then
                        reasonEl.GetString() |> Option.ofObj |> Option.defaultValue "blocked by hook"
                    else "blocked by hook"
                Block reason
            else
                // Accumulate modified_args
                let mutable modEl = Unchecked.defaultof<JsonElement>
                if root.TryGetProperty("modified_args", &modEl) && modEl.ValueKind = JsonValueKind.Object then
                    let newChanges = [ for prop in modEl.EnumerateObject() -> prop.Name, prop.Value.GetRawText() ]
                    match current with
                    | Block _ -> current   // block takes precedence
                    | ModifyArgs existing -> ModifyArgs (existing @ newChanges)
                    | Proceed -> if newChanges.IsEmpty then Proceed else ModifyArgs newChanges
                else current
        with ex ->
            eprintfn $"[hooks] failed to parse PreToolUse hook stdout: {ex.Message}"
            current

/// Run all matching PreToolUse hooks synchronously (they can block/modify args).
/// argsJson — args already serialized by the caller.
let runPreToolUse (toolName: string) (argsJson: string) (sessionId: string) (config: HooksConfig) : Task<PreToolResult> =
    let matchingHooks = config.PreToolUse |> List.filter (matchesTool >> (|>) toolName)
    if matchingHooks.IsEmpty then Task.FromResult(Proceed)
    else task {
        let payload = preToolUsePayload toolName argsJson sessionId
        let mutable result = Proceed
        for def in matchingHooks do
            match result with
            | Block _ -> ()   // short-circuit: already blocked
            | _ ->
                let exitCode, stdout =
                    try runSyncHookWithStdout def payload config.HookTimeoutMs
                    with ex ->
                        eprintfn $"[hooks] PreToolUse hook failed ({def.Command}): {ex.Message}"
                        0, ""  // treat exceptions as proceed (LogContinue)
                if exitCode = -1 then
                    eprintfn $"[hooks] PreToolUse hook timed out ({def.Command}) — proceeding"
                else
                    result <- parsePreResult stdout result
        return result
    }

/// Fire all matching PostToolUse hooks as fire-and-forget (cannot block).
/// argsJson — args already serialized by the caller.
let runPostToolUse (toolName: string) (argsJson: string) (result: string) (isError: bool) (sessionId: string) (config: HooksConfig) : unit =
    let matchingHooks = config.PostToolUse |> List.filter (matchesTool >> (|>) toolName)
    let payload = postToolUsePayload toolName argsJson result isError sessionId
    for def in matchingHooks do
        fireAsyncHook def payload

// ── runUserPromptSubmit ──────────────────────────────────────────────────────

/// Parse a single hook's stdout into the accumulated UserPromptResult.
/// Priority: BlockPrompt > Replace > first Append wins; empty stdout → unchanged.
let private parseUserPromptResult (stdout: string) (current: UserPromptResult) : UserPromptResult =
    if String.IsNullOrWhiteSpace stdout then current
    else
        try
            use doc = JsonDocument.Parse(stdout)
            let root = doc.RootElement
            let mutable blockEl = Unchecked.defaultof<JsonElement>
            if root.TryGetProperty("block", &blockEl) && blockEl.ValueKind = JsonValueKind.True then
                let mutable reasonEl = Unchecked.defaultof<JsonElement>
                let reason =
                    if root.TryGetProperty("reason", &reasonEl) && reasonEl.ValueKind = JsonValueKind.String then
                        reasonEl.GetString() |> Option.ofObj |> Option.defaultValue "blocked by hook"
                    else "blocked by hook"
                BlockPrompt reason
            else
                match current with
                | BlockPrompt _ -> current  // already blocked; short-circuit
                | _ ->
                    let mutable replaceEl = Unchecked.defaultof<JsonElement>
                    let mutable appendEl  = Unchecked.defaultof<JsonElement>
                    if root.TryGetProperty("replace", &replaceEl) && replaceEl.ValueKind = JsonValueKind.String then
                        let s = replaceEl.GetString() |> Option.ofObj |> Option.defaultValue ""
                        Replace s
                    elif root.TryGetProperty("append", &appendEl) && appendEl.ValueKind = JsonValueKind.String then
                        let extra = appendEl.GetString() |> Option.ofObj |> Option.defaultValue ""
                        match current with
                        | Append existing -> Append (existing + "\n" + extra)
                        | _              -> Append extra
                    else current
        with ex ->
            eprintfn $"[hooks] failed to parse UserPromptSubmit hook stdout: {ex.Message}"
            current

/// Run all UserPromptSubmit hooks synchronously (user is waiting; no fire-and-forget).
/// Accumulates results: first Block wins, first Replace wins over Append, Appends concatenate.
let runUserPromptSubmit (prompt: string) (sessionId: string) (config: HooksConfig) : Task<UserPromptResult> =
    if config.UserPromptSubmit.IsEmpty then Task.FromResult PassThrough
    else task {
        let payload = userPromptSubmitPayload prompt sessionId
        let mutable result = PassThrough
        for def in config.UserPromptSubmit do
            match result with
            | BlockPrompt _ -> ()  // short-circuit: already blocked
            | _ ->
                let exitCode, stdout =
                    try runSyncHookWithStdout def payload config.HookTimeoutMs
                    with ex ->
                        eprintfn $"[hooks] UserPromptSubmit hook failed ({def.Command}): {ex.Message}"
                        0, ""
                if exitCode = -1 then
                    eprintfn $"[hooks] UserPromptSubmit hook timed out ({def.Command}) — proceeding"
                else
                    result <- parseUserPromptResult stdout result
        return result
    }
