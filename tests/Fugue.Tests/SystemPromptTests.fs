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

[<Fact>]
let ``render injects Blazor platform hint for blazorwasm csproj`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory tmpDir |> ignore
    try
        File.WriteAllText(Path.Combine(tmpDir, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.BlazorWebAssembly\"><PropertyGroup><TargetFramework>net10.0-browser</TargetFramework></PropertyGroup></Project>")
        let result = render tmpDir ["Read"] None
        result |> should haveSubstring "Blazor WebAssembly"
        result |> should haveSubstring "Platform constraints"
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``render injects nanoFramework platform hint for nfproj`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory tmpDir |> ignore
    try
        File.WriteAllText(Path.Combine(tmpDir, "Device.nfproj"), "<Project ToolsVersion=\"15.0\"></Project>")
        let result = render tmpDir ["Read"] None
        result |> should haveSubstring "nanoFramework"
        result |> should haveSubstring "Platform constraints"
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``render injects Unity platform hint when Assets and ProjectSettings dirs exist`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory tmpDir |> ignore
    try
        Directory.CreateDirectory(Path.Combine(tmpDir, "Assets")) |> ignore
        Directory.CreateDirectory(Path.Combine(tmpDir, "ProjectSettings")) |> ignore
        let result = render tmpDir ["Read"] None
        result |> should haveSubstring "Unity"
        result |> should haveSubstring "Platform constraints"
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``render emits no platform section for plain project`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory tmpDir |> ignore
    try
        let result = render tmpDir ["Read"] None
        result |> should not' (haveSubstring "Platform constraints")
    finally
        Directory.Delete(tmpDir, true)
