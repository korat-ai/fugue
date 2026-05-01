module Fugue.Core.Hooks

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks

// ── Types ────────────────────────────────────────────────────────────────────

type HookEvent =
    | SessionStart
    | SessionEnd

type OnError =
    | LogContinue   // default: log warning and keep going
    | LogBlock      // propagate failure to caller

type HookDef =
    { Command   : string
      Async     : bool
      TimeoutMs : int option }   // None → use global default

type HooksConfig =
    { HookTimeoutMs : int          // global default (ms)
      OnError       : OnError
      SessionStart  : HookDef list
      SessionEnd    : HookDef list }

let defaultConfig : HooksConfig =
    { HookTimeoutMs = 5_000
      OnError       = LogContinue
      SessionStart  = []
      SessionEnd    = [] }

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
                                    yield { Command = cmd; Async = async'; TimeoutMs = perHookTimeout } ]
                else []

            { HookTimeoutMs = globalTimeout
              OnError        = onError
              SessionStart   = parseHooks "SessionStart"
              SessionEnd     = parseHooks "SessionEnd" }
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
let private spawnHook (command: string) (payloadJson: string) : Process =
    let expanded = expandHome command
    // Split on first space to get exe + args (simple split; shell quoting not supported).
    let exe, args =
        let idx = expanded.IndexOf(' ')
        if idx < 0 then expanded, ""
        else expanded.[..idx-1], expanded.[idx+1..]
    let psi = ProcessStartInfo()
    psi.FileName  <- exe
    if args <> "" then psi.Arguments <- args
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
