module Fugue.Tests.ToolRegistryTests

open System.Diagnostics.CodeAnalysis
open Xunit
open FsUnit.Xunit
open Fugue.Tools

[<RequiresUnreferencedCode("Calls ToolRegistry.buildAll which uses AgentSessionExtensions over STJ state")>]
[<RequiresDynamicCode("Calls ToolRegistry.buildAll which uses AgentSessionExtensions over STJ state")>]
[<Fact>]
let ``buildAll returns 9 functions`` () =
    let fns = ToolRegistry.buildAll "/tmp"
    fns |> List.length |> should equal 9

[<RequiresUnreferencedCode("Calls ToolRegistry.buildAll which uses AgentSessionExtensions over STJ state")>]
[<RequiresDynamicCode("Calls ToolRegistry.buildAll which uses AgentSessionExtensions over STJ state")>]
[<Fact>]
let ``buildAll names match the names list`` () =
    let fns = ToolRegistry.buildAll "/tmp"
    let actualNames = fns |> List.map (fun f -> f.Name) |> List.sort
    let expectedNames = ToolRegistry.names |> List.sort
    actualNames |> should equal expectedNames

[<RequiresUnreferencedCode("Calls ToolRegistry.buildAll which uses AgentSessionExtensions over STJ state")>]
[<RequiresDynamicCode("Calls ToolRegistry.buildAll which uses AgentSessionExtensions over STJ state")>]
[<Fact>]
let ``every tool has a valid object schema with required field`` () =
    let fns = ToolRegistry.buildAll "/tmp"
    for fn in fns do
        fn.JsonSchema.GetProperty("type").GetString() |> should equal "object"
        fn.JsonSchema.TryGetProperty("required") |> fst |> should equal true
