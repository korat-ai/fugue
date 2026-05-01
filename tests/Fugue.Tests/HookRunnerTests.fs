module Fugue.Tests.HookRunnerTests

open System
open System.IO
open System.Threading.Tasks
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
    let def = { Command = cmd; Async = async'; TimeoutMs = timeoutMs }
    let hooks =
        match event with
        | SessionStart -> { defaultConfig with SessionStart = [ def ]; OnError = onErr }
        | SessionEnd   -> { defaultConfig with SessionEnd   = [ def ]; OnError = onErr }
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
