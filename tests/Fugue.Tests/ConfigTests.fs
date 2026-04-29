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
