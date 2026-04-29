module Fugue.Tests.ConfigTests

open System
open Xunit
open FsUnit.Xunit
open Fugue.Core.Config

[<Fact>]
let ``load returns NoConfigFound when env empty and no file`` () =
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null)
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
    Environment.SetEnvironmentVariable("FUGUE_PROVIDER", null)
    let tmpHome = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    let result = load [||]
    match result with
    | Error (NoConfigFound _) -> ()
    | other -> failwithf "expected NoConfigFound, got %A" other

[<Fact>]
let ``load picks Anthropic when ANTHROPIC_API_KEY set and no config file`` () =
    // Isolate HOME so the user's real ~/.fugue/config.json doesn't shadow the
    // implicit-env fallback (file > implicit-env per Config.load priority).
    let tmpHome = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-test")
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
    Environment.SetEnvironmentVariable("FUGUE_PROVIDER", null)
    let result = load [||]
    match result with
    | Ok cfg ->
        match cfg.Provider with
        | Anthropic("sk-ant-test", model) -> model |> should equal "claude-opus-4-7"
        | other -> failwithf "expected Anthropic, got %A" other
    | Error e -> failwithf "expected Ok, got %A" e

[<Fact>]
let ``load picks alignment=Right when ui.userAlignment=right`` () =
    let tmpHome = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let dir = IO.Path.Combine(tmpHome, ".fugue")
    IO.Directory.CreateDirectory dir |> ignore
    let path = IO.Path.Combine(dir, "config.json")
    IO.File.WriteAllText(path, """{
        "provider":"ollama","model":"llama","apiKey":"","ollamaEndpoint":"http://localhost:11434",
        "maxIterations":30,"ui":{"userAlignment":"right","locale":"ru"}
    }""")
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null)
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
    Environment.SetEnvironmentVariable("FUGUE_PROVIDER", null)
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    match load [||] with
    | Ok cfg ->
        cfg.Ui.UserAlignment |> should equal Right
        cfg.Ui.Locale |> should equal "ru"
    | Error e -> failwithf "expected Ok, got %A" e

[<Fact>]
let ``load defaults Ui to Left when ui block missing`` () =
    let tmpHome = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let dir = IO.Path.Combine(tmpHome, ".fugue")
    IO.Directory.CreateDirectory dir |> ignore
    let path = IO.Path.Combine(dir, "config.json")
    IO.File.WriteAllText(path, """{
        "provider":"ollama","model":"llama","apiKey":"","ollamaEndpoint":"http://localhost:11434",
        "maxIterations":30
    }""")
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null)
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
    Environment.SetEnvironmentVariable("FUGUE_PROVIDER", null)
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    match load [||] with
    | Ok cfg -> cfg.Ui.UserAlignment |> should equal Left
    | Error e -> failwithf "expected Ok, got %A" e
