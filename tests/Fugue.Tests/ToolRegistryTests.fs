module Fugue.Tests.ToolRegistryTests

open Xunit
open FsUnit.Xunit
open Fugue.Tools

[<Fact>]
let ``buildAll returns 6 functions`` () =
    let fns = ToolRegistry.buildAll "/tmp"
    fns |> List.length |> should equal 6

[<Fact>]
let ``buildAll names match the names list`` () =
    let fns = ToolRegistry.buildAll "/tmp"
    let actualNames = fns |> List.map (fun f -> f.Name) |> List.sort
    let expectedNames = ToolRegistry.names |> List.sort
    actualNames |> should equal expectedNames

[<Fact>]
let ``every tool has a valid object schema with required field`` () =
    let fns = ToolRegistry.buildAll "/tmp"
    for fn in fns do
        fn.JsonSchema.GetProperty("type").GetString() |> should equal "object"
        fn.JsonSchema.TryGetProperty("required") |> fst |> should equal true
