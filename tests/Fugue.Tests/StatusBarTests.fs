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
