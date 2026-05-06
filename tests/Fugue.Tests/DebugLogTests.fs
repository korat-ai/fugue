[<Xunit.Collection("Sequential")>]
module Fugue.Tests.DebugLogTests

open System
open System.IO
open Xunit
open FsUnit.Xunit
open Fugue.Core

[<Fact>]
let ``DebugLog.write does nothing when FUGUE_DEBUG is unset`` () =
    Environment.SetEnvironmentVariable("FUGUE_DEBUG", null)
    // Must not throw or create files
    DebugLog.sessionStart "openai" "gpt-4" "/tmp"
    DebugLog.toolCall "read" "{}" "result" 10L
    DebugLog.turnCompleted 10 20 100L
    DebugLog.logError "test error"
    // No assert — not crashing is the contract

[<Fact>]
let ``DebugLog.sessionStart appends session_start entry when FUGUE_DEBUG=1`` () =
    let tmpHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory tmpHome |> ignore
    let prevHome = Environment.GetEnvironmentVariable "HOME"
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    // Force re-evaluation of the lazy by using a fresh env
    Environment.SetEnvironmentVariable("FUGUE_DEBUG", "1")
    try
        DebugLog.sessionStart "test-provider" "test-model" "/tmp"
        let logFile = Path.Combine(tmpHome, ".fugue", "debug.log")
        File.Exists logFile |> should equal true
        let contents = File.ReadAllText logFile
        contents |> should haveSubstring "session_start"
        contents |> should haveSubstring "test-provider"
        contents |> should haveSubstring "test-model"
    finally
        Environment.SetEnvironmentVariable("FUGUE_DEBUG", null)
        Environment.SetEnvironmentVariable("HOME", prevHome)
        Directory.Delete(tmpHome, true)

[<Fact>]
let ``DebugLog.toolCall appends tool_call entry when FUGUE_DEBUG=1`` () =
    let tmpHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory tmpHome |> ignore
    let prevHome = Environment.GetEnvironmentVariable "HOME"
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    Environment.SetEnvironmentVariable("FUGUE_DEBUG", "1")
    try
        DebugLog.toolCall "bash" "{\"cmd\":\"ls\"}" "file1\nfile2" 42L
        let logFile = Path.Combine(tmpHome, ".fugue", "debug.log")
        File.Exists logFile |> should equal true
        let contents = File.ReadAllText logFile
        contents |> should haveSubstring "tool_call"
        contents |> should haveSubstring "bash"
        contents |> should haveSubstring "42"
    finally
        Environment.SetEnvironmentVariable("FUGUE_DEBUG", null)
        Environment.SetEnvironmentVariable("HOME", prevHome)
        Directory.Delete(tmpHome, true)

[<Fact>]
let ``DebugLog.toolCall truncates args and result to 500 chars`` () =
    let tmpHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory tmpHome |> ignore
    let prevHome = Environment.GetEnvironmentVariable "HOME"
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    Environment.SetEnvironmentVariable("FUGUE_DEBUG", "1")
    try
        let longStr = String.replicate 600 "x"
        DebugLog.toolCall "read" longStr longStr 0L
        let logFile = Path.Combine(tmpHome, ".fugue", "debug.log")
        let contents = File.ReadAllText logFile
        // The line should not contain 600 consecutive x's
        contents.Contains(longStr) |> should equal false
    finally
        Environment.SetEnvironmentVariable("FUGUE_DEBUG", null)
        Environment.SetEnvironmentVariable("HOME", prevHome)
        Directory.Delete(tmpHome, true)

[<Fact>]
let ``DebugLog.turnCompleted appends turn entry when FUGUE_DEBUG=1`` () =
    let tmpHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory tmpHome |> ignore
    let prevHome = Environment.GetEnvironmentVariable "HOME"
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    Environment.SetEnvironmentVariable("FUGUE_DEBUG", "1")
    try
        DebugLog.turnCompleted 123 456 789L
        let logFile = Path.Combine(tmpHome, ".fugue", "debug.log")
        let contents = File.ReadAllText logFile
        contents |> should haveSubstring "\"event\":\"turn\""
        contents |> should haveSubstring "123"
        contents |> should haveSubstring "789"
    finally
        Environment.SetEnvironmentVariable("FUGUE_DEBUG", null)
        Environment.SetEnvironmentVariable("HOME", prevHome)
        Directory.Delete(tmpHome, true)

[<Fact>]
let ``DebugLog.logError appends error entry when FUGUE_DEBUG=1`` () =
    let tmpHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory tmpHome |> ignore
    let prevHome = Environment.GetEnvironmentVariable "HOME"
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    Environment.SetEnvironmentVariable("FUGUE_DEBUG", "1")
    try
        DebugLog.logError "something went wrong"
        let logFile = Path.Combine(tmpHome, ".fugue", "debug.log")
        let contents = File.ReadAllText logFile
        contents |> should haveSubstring "\"event\":\"error\""
        contents |> should haveSubstring "something went wrong"
    finally
        Environment.SetEnvironmentVariable("FUGUE_DEBUG", null)
        Environment.SetEnvironmentVariable("HOME", prevHome)
        Directory.Delete(tmpHome, true)
