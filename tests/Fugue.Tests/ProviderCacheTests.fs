module Fugue.Tests.ProviderCacheTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit
open FsUnit.Xunit
open Fugue.Core.Hooks
open Fugue.Core.ProviderCache

// ── Helpers ───────────────────────────────────────────────────────────────────

/// Create a temp shell script with the given body. Returns the script path.
let private makeScript (body: string) : string =
    let tmp = Path.GetTempFileName()
    let path = Path.ChangeExtension(tmp, ".sh") |> Option.ofObj |> Option.defaultValue (tmp + ".sh")
    File.WriteAllText(path, $"#!/bin/sh\n{body}\n")
    let psi = ProcessStartInfo("chmod", $"+x \"{path}\"")
    psi.UseShellExecute <- false
    match Process.Start(psi) with
    | null -> ()
    | p    -> use _ = p in p.WaitForExit()
    path

/// Build a ContextProvider with a given TTL (seconds).
let private makeProvider (name: string) (command: string) (ttlSeconds: int) : ContextProvider =
    { Name = name; Command = command; TtlSeconds = ttlSeconds }

/// Set FUGUE_CACHE_DIR for the duration of the test; returns a disposable that restores it.
let private useTmpCacheDir () : string * IDisposable =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    let saved = Environment.GetEnvironmentVariable "FUGUE_CACHE_DIR"
    Environment.SetEnvironmentVariable("FUGUE_CACHE_DIR", tmp)
    let restore =
        { new IDisposable with
            member _.Dispose() =
                Environment.SetEnvironmentVariable("FUGUE_CACHE_DIR",
                    match saved with null -> "" | s -> s)
                try Directory.Delete(tmp, true) with _ -> () }
    tmp, restore

// ── Test 1: cache miss → fetch → write → second call hits cache ───────────────

[<Fact>]
let ``getCached returns stdout on hit, skips re-execution on second call`` () =
    let tmpCacheDir, cleanup = useTmpCacheDir ()
    use _ = cleanup
    let script = makeScript "echo hello"
    try
        let provider = makeProvider "test1" script 3600
        // First call: cache miss → executes script, writes cache
        let result1 = getCached provider |> Async.RunSynchronously
        // getCached alone doesn't run the command; we need fetchProvider path
        // so run assembleWithProviders instead, which calls fetchProvider internally
        let assembled = assembleWithProviders [ provider ] 5000 |> Async.RunSynchronously
        assembled |> should haveSubstring "[Context: test1]"
        assembled |> should haveSubstring "hello"
        // Second call: cache should be hit (script still exists, but result comes from cache)
        let result2 = getCached provider |> Async.RunSynchronously
        result2 |> should not' (equal None)
        result2.Value |> should haveSubstring "hello"
    finally
        try File.Delete script with _ -> ()

// ── Test 2: TTL expired → getCached returns None ──────────────────────────────

[<Fact>]
let ``getCached returns None when cache file is older than TTL`` () =
    let tmpCacheDir, cleanup = useTmpCacheDir ()
    use _ = cleanup
    let provider = makeProvider "test2" "echo world" 60
    // Write a stale cache file manually (fetchedAt = 2 hours ago)
    let twoHoursAgo = DateTimeOffset.UtcNow.AddHours(-2.0).ToString("o")
    let dir = tmpCacheDir
    Directory.CreateDirectory(dir) |> ignore
    // We need the cache file path — compute the same way as ProviderCache
    do writeCache provider "stale content" |> Async.RunSynchronously
    // Now overwrite with old timestamp
    let cacheFiles = Directory.GetFiles(dir, "provider-test2-*.json")
    cacheFiles.Length |> should be (greaterThan 0)
    let path = cacheFiles.[0]
    use stream = new IO.MemoryStream()
    use writer = new Utf8JsonWriter(stream)
    writer.WriteStartObject()
    writer.WriteNumber("ttlSeconds", 60)
    writer.WriteString("fetchedAt", twoHoursAgo)
    writer.WriteString("stdout", "stale content")
    writer.WriteEndObject()
    writer.Flush()
    File.WriteAllBytes(path, stream.ToArray())
    // Now getCached should see it as expired
    let result = getCached provider |> Async.RunSynchronously
    result |> should equal None

// ── Test 3: provider exits non-zero → returns None, no cache written ──────────

[<Fact>]
let ``assembleWithProviders returns empty when provider exits non-zero`` () =
    let tmpCacheDir, cleanup = useTmpCacheDir ()
    use _ = cleanup
    let script = makeScript "exit 2"
    try
        let provider = makeProvider "test3" script 3600
        let assembled = assembleWithProviders [ provider ] 5000 |> Async.RunSynchronously
        assembled |> should equal ""
        // No cache file should have been written
        let cacheFiles = Directory.GetFiles(tmpCacheDir, "provider-test3-*.json")
        cacheFiles.Length |> should equal 0
    finally
        try File.Delete script with _ -> ()

// ── Test 4: provider times out → returns None ────────────────────────────────

[<Fact>]
let ``assembleWithProviders returns empty when provider times out`` () =
    let tmpCacheDir, cleanup = useTmpCacheDir ()
    use _ = cleanup
    let script = makeScript "sleep 30"
    try
        let provider = makeProvider "test4" script 3600
        let sw = Diagnostics.Stopwatch.StartNew()
        let assembled = assembleWithProviders [ provider ] 100 |> Async.RunSynchronously
        sw.Stop()
        assembled |> should equal ""
        sw.ElapsedMilliseconds |> should be (lessThan 3000L)
    finally
        try File.Delete script with _ -> ()

// ── Test 5: assembleWithProviders includes [Context: foo] block ───────────────

[<Fact>]
let ``assembleWithProviders includes Context block header for successful provider`` () =
    let _tmpCacheDir, cleanup = useTmpCacheDir ()
    use _ = cleanup
    let script = makeScript "echo project context data"
    try
        let provider = makeProvider "foo" script 0  // ttl=0 → no cache
        let assembled = assembleWithProviders [ provider ] 5000 |> Async.RunSynchronously
        assembled |> should haveSubstring "[Context: foo]"
        assembled |> should haveSubstring "project context data"
    finally
        try File.Delete script with _ -> ()
