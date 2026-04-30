module Fugue.Tests.SystemPromptTests

open System
open System.IO
open Xunit
open FsUnit.Xunit
open Fugue.Core.SystemPrompt

[<Fact>]
let ``loadFugueContext returns None when no FUGUE.md exists`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory tmpDir |> ignore
    let origHome = Environment.GetEnvironmentVariable "HOME"
    Environment.SetEnvironmentVariable("HOME", tmpDir)
    try
        loadFugueContext tmpDir |> should equal None
    finally
        Directory.Delete(tmpDir, true)
        Environment.SetEnvironmentVariable("HOME", origHome)

[<Fact>]
let ``loadFugueContext returns Some when FUGUE.md exists in cwd`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory tmpDir |> ignore
    let origHome = Environment.GetEnvironmentVariable "HOME"
    Environment.SetEnvironmentVariable("HOME", tmpDir)
    try
        File.WriteAllText(Path.Combine(tmpDir, "FUGUE.md"), "# Test context\nHello")
        let result = loadFugueContext tmpDir
        result |> should not' (equal None)
        result.Value |> should haveSubstring "Hello"
    finally
        Directory.Delete(tmpDir, true)
        Environment.SetEnvironmentVariable("HOME", origHome)

[<Fact>]
let ``render with None context produces same output as before`` () =
    let result = render "/tmp" ["Read"; "Write"] None
    result |> should haveSubstring "Working directory: /tmp"
    result |> should haveSubstring "Available tools: Read, Write"

[<Fact>]
let ``render with Some context appends project context section`` () =
    let result = render "/tmp" ["Read"] (Some "# My project\nDo X.")
    result |> should haveSubstring "Project context"
    result |> should haveSubstring "Do X."
