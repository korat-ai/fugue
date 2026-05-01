module Fugue.Tests.HookRunnerTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.AI
open Xunit
open FsUnit.Xunit
open Fugue.Core.Hooks

// ── Helpers ───────────────────────────────────────────────────────────────────

/// Create a temp shell script with the given body. Returns the script path.
/// The caller is responsible for deletion (done via finally / IDisposable pattern).
let private makeScript (body: string) : string =
    let tmp = Path.GetTempFileName()
    let path = Path.ChangeExtension(tmp, ".sh") |> Option.ofObj |> Option.defaultValue (tmp + ".sh")
    File.WriteAllText(path, $"#!/bin/sh\n{body}\n")
    // Make executable
    let psi = Diagnostics.ProcessStartInfo("chmod", $"+x \"{path}\"")
    psi.UseShellExecute <- false
    match Diagnostics.Process.Start(psi) with
    | null -> ()
    | p    -> use _ = p in p.WaitForExit()
    path

/// Build a minimal HooksConfig with a single command hook for the given event.
let private cfgWith (event: HookEvent) (cmd: string) (async': bool) (timeoutMs: int option) (onErr: OnError) : HooksConfig =
    let def = { Command = cmd; Async = async'; TimeoutMs = timeoutMs; Matcher = None }
    let hooks =
        match event with
        | SessionStart     -> { defaultConfig with SessionStart     = [ def ]; OnError = onErr }
        | SessionEnd       -> { defaultConfig with SessionEnd       = [ def ]; OnError = onErr }
        | PreToolUse       -> { defaultConfig with PreToolUse       = [ def ]; OnError = onErr }
        | PostToolUse      -> { defaultConfig with PostToolUse      = [ def ]; OnError = onErr }
        | UserPromptSubmit -> { defaultConfig with UserPromptSubmit = [ def ]; OnError = onErr }
    hooks

// ── Test 1: no hooks.json → silent no-op ─────────────────────────────────────

[<Fact>]
let ``runLifecycle is a no-op when config has no hooks`` () =
    // defaultConfig has empty lists; should complete immediately without error.
    let task = runLifecycle SessionStart """{"event":"SessionStart"}""" defaultConfig
    task.Wait()   // must not throw

// ── Test 2: sync hook receives correct JSON payload ───────────────────────────

[<Fact>]
let ``sync SessionStart hook receives the JSON payload via stdin`` () =
    let outFile = Path.GetTempFileName()
    // Script: read all of stdin and write it to outFile
    let script = makeScript $"cat > \"{outFile}\""
    try
        let payload = sessionStartPayload "/tmp/proj" "claude-sonnet-4-6" "anthropic" "SESSION01"
        let cfg = cfgWith SessionStart script false None LogContinue
        (runLifecycle SessionStart payload cfg).Wait()

        let written = File.ReadAllText(outFile).Trim()
        written |> should haveSubstring "\"event\":\"SessionStart\""
        written |> should haveSubstring "\"version\":1"
        written |> should haveSubstring "\"cwd\":\"/tmp/proj\""
        written |> should haveSubstring "\"model\":\"claude-sonnet-4-6\""
        written |> should haveSubstring "\"provider\":\"anthropic\""
        written |> should haveSubstring "\"sessionId\":\"SESSION01\""
    finally
        try File.Delete outFile  with _ -> ()
        try File.Delete script   with _ -> ()

// ── Test 3: async hook fires-and-forgets (exit 1 is swallowed) ────────────────

[<Fact>]
let ``async hook does not throw even when the script exits with code 1`` () =
    let script = makeScript "exit 1"
    try
        let cfg = cfgWith SessionEnd script true None LogContinue
        let payload = sessionEndPayload "/tmp" 1000L 3
        // Should complete without exception
        (runLifecycle SessionEnd payload cfg).Wait()
    finally
        try File.Delete script with _ -> ()

// ── Test 4: sync hook timeout kills process and returns failure ───────────────

[<Fact>]
let ``sync hook timeout kills the process and log-continue swallows the error`` () =
    let script = makeScript "sleep 30"
    try
        // 100 ms timeout — much shorter than sleep 30
        let cfg = cfgWith SessionStart script false (Some 100) LogContinue
        let payload = sessionStartPayload "/tmp" "m" "p" "s"
        // log-continue: must NOT throw
        (runLifecycle SessionStart payload cfg).Wait()
    finally
        try File.Delete script with _ -> ()

// ── Test 5: onError=log-block propagates failure ──────────────────────────────

[<Fact>]
let ``sync hook that exits non-zero with log-block raises`` () =
    let script = makeScript "exit 2"
    try
        let cfg = cfgWith SessionStart script false None LogBlock
        let payload = sessionStartPayload "/tmp" "m" "p" "s"
        let mutable threw = false
        try
            (runLifecycle SessionStart payload cfg).Wait()
        with _ ->
            threw <- true
        threw |> should equal true
    finally
        try File.Delete script with _ -> ()

// ── Test 6: onError=log-continue swallows non-zero exit ──────────────────────

[<Fact>]
let ``sync hook that exits non-zero with log-continue does not raise`` () =
    let script = makeScript "exit 3"
    try
        let cfg = cfgWith SessionStart script false None LogContinue
        let payload = sessionStartPayload "/tmp" "m" "p" "s"
        // Must NOT throw
        (runLifecycle SessionStart payload cfg).Wait()
    finally
        try File.Delete script with _ -> ()

// ── Test 7: payload builders emit correct JSON ────────────────────────────────

[<Fact>]
let ``sessionStartPayload contains all required fields`` () =
    let json = sessionStartPayload "/home/user/proj" "claude-opus-4-7" "anthropic" "ULID123"
    json |> should haveSubstring "\"event\":\"SessionStart\""
    json |> should haveSubstring "\"version\":1"
    json |> should haveSubstring "\"cwd\":\"/home/user/proj\""
    json |> should haveSubstring "\"model\":\"claude-opus-4-7\""
    json |> should haveSubstring "\"provider\":\"anthropic\""
    json |> should haveSubstring "\"sessionId\":\"ULID123\""

[<Fact>]
let ``sessionEndPayload contains all required fields`` () =
    let json = sessionEndPayload "/home/user/proj" 42310L 7
    json |> should haveSubstring "\"event\":\"SessionEnd\""
    json |> should haveSubstring "\"version\":1"
    json |> should haveSubstring "\"cwd\":\"/home/user/proj\""
    json |> should haveSubstring "\"durationMs\":42310"
    json |> should haveSubstring "\"turnCount\":7"

// ── Test 8: config load — absent file yields defaultConfig ────────────────────

[<Fact>]
let ``load returns defaultConfig when hooks.json is absent`` () =
    // Point HOME to a temp dir with no .fugue/hooks.json
    let saved = Environment.GetEnvironmentVariable "HOME"
    let tmpHome = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    try
        Environment.SetEnvironmentVariable("HOME", tmpHome)
        let cfg = load ()
        cfg.SessionStart |> should be Empty
        cfg.SessionEnd   |> should be Empty
        cfg.HookTimeoutMs |> should equal 5_000
    finally
        Environment.SetEnvironmentVariable("HOME", saved)
        try Directory.Delete(tmpHome, true) with _ -> ()

// ── PreToolUse / PostToolUse tests ────────────────────────────────────────────

/// Build a minimal HooksConfig with a single PreToolUse hook.
let private preCfgWith (cmd: string) (matcher: string option) (timeoutMs: int option) : HooksConfig =
    let def = { Command = cmd; Async = false; TimeoutMs = timeoutMs; Matcher = matcher }
    { defaultConfig with PreToolUse = [ def ] }

/// Build a minimal HooksConfig with a single PostToolUse hook.
let private postCfgWith (cmd: string) (matcher: string option) : HooksConfig =
    let def = { Command = cmd; Async = true; TimeoutMs = None; Matcher = matcher }
    { defaultConfig with PostToolUse = [ def ] }

/// Make a JsonElement-boxed string arg for AIFunctionArguments.
let private jsonStrArg (s: string) : obj | null =
    use doc = JsonDocument.Parse(JsonSerializer.Serialize s)
    box (doc.RootElement.Clone())

// ── Test 9: block=true hook returns Block ─────────────────────────────────────

[<Fact>]
let ``PreToolUse block=true returns Block with reason`` () =
    let script = makeScript """echo '{"block":true,"reason":"no rm -rf"}'"""
    try
        let cfg = preCfgWith script None None
        let args = AIFunctionArguments()
        let result = (runPreToolUse "Bash" "{}" "s1" cfg).Result
        match result with
        | Block reason -> reason |> should equal "no rm -rf"
        | other -> failwith $"Expected Block, got {other}"
    finally
        try File.Delete script with _ -> ()

// ── Test 10: modified_args merges into AIFunctionArguments ────────────────────

[<Fact>]
let ``PreToolUse modified_args returns ModifyArgs with new value`` () =
    let script = makeScript """echo '{"modified_args":{"command":"echo safe"}}'"""
    try
        let cfg = preCfgWith script None None
        let args = AIFunctionArguments(dict [ "command", jsonStrArg "echo original" ])
        let result = (runPreToolUse "Bash" """{"command":"echo original"}""" "s2" cfg).Result
        match result with
        | ModifyArgs changes ->
            changes |> should not' (be Empty)
            let cmdChange = changes |> List.tryFind (fun (k, _) -> k = "command")
            cmdChange |> should not' (equal None)
            // Apply the changes and verify the arg was overwritten
            for (k, v) in changes do
                use doc = JsonDocument.Parse(v)
                args[k] <- box (doc.RootElement.Clone())
            match args.TryGetValue "command" with
            | true, (:? JsonElement as el) when el.ValueKind = JsonValueKind.String ->
                el.GetString() |> Option.ofObj |> Option.defaultValue "" |> should equal "echo safe"
            | _ -> failwith "Expected JsonElement string for command"
        | other -> failwith $"Expected ModifyArgs, got {other}"
    finally
        try File.Delete script with _ -> ()

// ── Test 11: matcher filter skips non-matching tool ───────────────────────────

[<Fact>]
let ``PreToolUse matcher Bash skips non-matching toolName Read`` () =
    let outFile = Path.GetTempFileName()
    // Script writes to outFile so we can probe whether it ran
    let script = makeScript $"echo ran > \"{outFile}\""
    try
        let cfg = preCfgWith script (Some "Bash") None
        // toolName = "Read" — should NOT match matcher "Bash"
        let result = (runPreToolUse "Read" "{}" "s3" cfg).Result
        result |> should equal Proceed
        // Script must not have written anything
        let written = File.ReadAllText(outFile).Trim()
        written |> should be Empty
    finally
        try File.Delete outFile with _ -> ()
        try File.Delete script  with _ -> ()

// ── Test 12: PostToolUse is fire-and-forget (returns immediately) ─────────────

[<Fact>]
let ``PostToolUse fire-and-forget returns before slow script finishes`` () =
    let outFile = Path.GetTempFileName()
    // Script sleeps 5s then writes; runPostToolUse should return well before that.
    let script = makeScript $"sleep 5 && echo done > \"{outFile}\""
    try
        let cfg = postCfgWith script None
        let sw = Stopwatch.StartNew()
        runPostToolUse "Write" "{}" "ok" false "s4" cfg
        sw.Stop()
        sw.ElapsedMilliseconds |> should be (lessThan 500L)
        // The file should NOT be written yet (script is sleeping)
        let written = File.ReadAllText(outFile).Trim()
        written |> should be Empty
    finally
        try File.Delete outFile with _ -> ()
        try File.Delete script  with _ -> ()

// ── Test 13: PreToolUse timeout with LogContinue returns Proceed ─────────────

[<Fact>]
let ``PreToolUse hook timeout returns Proceed (LogContinue)`` () =
    let script = makeScript "sleep 30"
    try
        // 100ms timeout — much shorter than sleep 30
        let cfg = preCfgWith script None (Some 100)
        let result = (runPreToolUse "Bash" "{}" "s5" cfg).Result
        // Should proceed (not throw, not block) — timeout is silently swallowed
        result |> should equal Proceed
    finally
        try File.Delete script with _ -> ()

// ── Test 14: no PreToolUse hooks → Proceed immediately ───────────────────────

[<Fact>]
let ``runPreToolUse with no hooks returns Proceed immediately`` () =
    // defaultConfig has empty PreToolUse list — no process should be spawned
    let sw = Stopwatch.StartNew()
    let result = (runPreToolUse "Bash" "{}" "s6" defaultConfig).Result
    sw.Stop()
    result |> should equal Proceed
    sw.ElapsedMilliseconds |> should be (lessThan 100L)

// ── UserPromptSubmit tests ────────────────────────────────────────────────────

/// Build a minimal HooksConfig with a single UserPromptSubmit hook.
let private promptCfgWith (cmd: string) (timeoutMs: int option) : HooksConfig =
    let def = { Command = cmd; Async = false; TimeoutMs = timeoutMs; Matcher = None }
    { defaultConfig with UserPromptSubmit = [ def ] }

// ── Test 15: no hooks → PassThrough immediately ───────────────────────────────

[<Fact>]
let ``runUserPromptSubmit with no hooks returns PassThrough immediately`` () =
    let sw = Stopwatch.StartNew()
    let result = (runUserPromptSubmit "hello" "s7" defaultConfig).Result
    sw.Stop()
    result |> should equal PassThrough
    sw.ElapsedMilliseconds |> should be (lessThan 100L)

// ── Test 16: block=true returns BlockPrompt with reason ───────────────────────

[<Fact>]
let ``UserPromptSubmit block=true returns BlockPrompt with reason`` () =
    let script = makeScript """echo '{"block":true,"reason":"no profanity allowed"}'"""
    try
        let cfg = promptCfgWith script None
        let result = (runUserPromptSubmit "bad word!" "s8" cfg).Result
        match result with
        | BlockPrompt reason -> reason |> should equal "no profanity allowed"
        | other -> failwith $"Expected BlockPrompt, got {other}"
    finally
        try File.Delete script with _ -> ()

// ── Test 17: replace="..." returns Replace with new string ────────────────────

[<Fact>]
let ``UserPromptSubmit replace returns Replace with substituted prompt`` () =
    let script = makeScript """echo '{"replace":"augmented prompt"}'"""
    try
        let cfg = promptCfgWith script None
        let result = (runUserPromptSubmit "original" "s9" cfg).Result
        match result with
        | Replace newPrompt -> newPrompt |> should equal "augmented prompt"
        | other -> failwith $"Expected Replace, got {other}"
    finally
        try File.Delete script with _ -> ()

// ── Test 18: append="..." returns Append fragment ─────────────────────────────

[<Fact>]
let ``UserPromptSubmit append returns Append with extra text`` () =
    let script = makeScript """echo '{"append":"extra context"}'"""
    try
        let cfg = promptCfgWith script None
        let result = (runUserPromptSubmit "base prompt" "s10" cfg).Result
        match result with
        | Append extra -> extra |> should equal "extra context"
        | other -> failwith $"Expected Append, got {other}"
    finally
        try File.Delete script with _ -> ()

// ── Test 19: empty stdout → PassThrough ──────────────────────────────────────

[<Fact>]
let ``UserPromptSubmit empty stdout returns PassThrough`` () =
    let script = makeScript "exit 0"  // exits cleanly with no stdout
    try
        let cfg = promptCfgWith script None
        let result = (runUserPromptSubmit "some prompt" "s11" cfg).Result
        result |> should equal PassThrough
    finally
        try File.Delete script with _ -> ()
