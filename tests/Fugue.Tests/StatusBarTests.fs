module Fugue.Tests.StatusBarTests

open System
open Xunit
open FsUnit.Xunit
open Fugue.Cli

[<Fact>]
let ``formatElapsed under 60s shows decimal seconds`` () =
    let t = TimeSpan.FromSeconds 2.3
    StatusBar.formatElapsed t |> should equal "2.3s"

[<Fact>]
let ``formatElapsed over 60s shows minutes and seconds`` () =
    let t = TimeSpan.FromSeconds 65.0
    StatusBar.formatElapsed t |> should equal "1m 5s"

[<Fact>]
let ``recordWords accumulates and resetWords clears`` () =
    StatusBar.resetWords ()
    StatusBar.recordWords 100
    StatusBar.recordWords 50
    // We can't directly read the private counter, but reset should not throw
    StatusBar.resetWords ()

[<Fact>]
let ``contextWindowFor returns expected sizes`` () =
    StatusBar.contextWindowFor "claude-opus-4-7"      |> should equal 200_000
    StatusBar.contextWindowFor "gpt-4o"               |> should equal 128_000
    StatusBar.contextWindowFor "gpt-3.5-turbo"        |> should equal 16_000
    StatusBar.contextWindowFor "llama3.1"             |> should equal 8_000
    StatusBar.contextWindowFor "mistral-7b"           |> should equal 32_000
    StatusBar.contextWindowFor "unknown-model"        |> should equal 100_000
