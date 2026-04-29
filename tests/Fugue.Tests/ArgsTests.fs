module Fugue.Tests.ArgsTests

open System.Collections.Generic
open System.Text.Json
open Microsoft.Extensions.AI
open Xunit
open FsUnit.Xunit
open Fugue.Tools.AiFunctions

let private mkArgs (pairs: (string * obj | null) list) : AIFunctionArguments =
    AIFunctionArguments(dict pairs)

let private jsonEl (json: string) : JsonElement =
    use doc = JsonDocument.Parse(json)
    doc.RootElement.Clone()

[<Fact>]
let ``getStr returns string from JsonElement`` () =
    let args = mkArgs [ "name", box (jsonEl "\"hi\"") ]
    Args.getStr args "name" |> should equal "hi"

[<Fact>]
let ``getStr returns string from raw string`` () =
    let args = mkArgs [ "name", box "hi" ]
    Args.getStr args "name" |> should equal "hi"

[<Fact>]
let ``getStr throws ArgumentException when missing`` () =
    let args = mkArgs []
    (fun () -> Args.getStr args "name" |> ignore)
    |> should throw typeof<System.ArgumentException>

[<Fact>]
let ``tryGetInt returns Some from JsonElement number`` () =
    let args = mkArgs [ "n", box (jsonEl "42") ]
    Args.tryGetInt args "n" |> should equal (Some 42)

[<Fact>]
let ``tryGetInt returns None when missing`` () =
    let args = mkArgs []
    Args.tryGetInt args "n" |> should equal (None: int option)

[<Fact>]
let ``tryGetBool returns Some from JsonElement true`` () =
    let args = mkArgs [ "b", box (jsonEl "true") ]
    Args.tryGetBool args "b" |> should equal (Some true)

[<Fact>]
let ``tryGetStr returns None for missing key`` () =
    let args = mkArgs []
    Args.tryGetStr args "x" |> should equal (None: string option)
