[<Xunit.Collection("Sequential")>]
module Fugue.Tests.DiscoveryTests

open System
open System.IO
open Xunit
open FsUnit.Xunit
open Fugue.Agent.Discovery

/// Save and restore env vars around a test body to avoid cross-test contamination.
let private withEnv (vars: string list) (body: unit -> unit) =
    let saved = vars |> List.map (fun k -> k, Environment.GetEnvironmentVariable k)
    try body ()
    finally
        saved |> List.iter (fun (k, v) -> Environment.SetEnvironmentVariable(k, v))

[<Fact>]
let ``discover returns EnvKey when ANTHROPIC_API_KEY set`` () =
    withEnv ["ANTHROPIC_API_KEY"; "OPENAI_API_KEY"] <| fun () ->
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-x")
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
        let candidates = discover { ClaudeSettingsPath = "/nonexistent"; OllamaPing = fun _ -> async.Return None }
        candidates
        |> List.exists (function
            | EnvKey("anthropic", _, _) -> true
            | _ -> false)
        |> should equal true

[<Fact>]
let ``discover reads ~/.claude/settings.json`` () =
    withEnv ["ANTHROPIC_API_KEY"; "OPENAI_API_KEY"] <| fun () ->
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null)
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
        let p = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json")
        File.WriteAllText(p,
            """{ "env": { "ANTHROPIC_API_KEY": "sk-ant-fromclaude" }, "model": "claude-opus-4-7" }""")
        let candidates = discover { ClaudeSettingsPath = p; OllamaPing = fun _ -> async.Return None }
        candidates
        |> List.exists (function
            | ClaudeSettings("claude-opus-4-7", "sk-ant-fromclaude") -> true
            | _ -> false)
        |> should equal true

[<Fact>]
let ``discover detects Ollama via injected ping`` () =
    withEnv ["ANTHROPIC_API_KEY"; "OPENAI_API_KEY"] <| fun () ->
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null)
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
        let candidates =
            discover
                { ClaudeSettingsPath = "/nonexistent"
                  OllamaPing = fun _ -> async.Return (Some [ "llama3.1"; "mistral" ]) }
        candidates
        |> List.exists (function
            | OllamaLocal(_, models) -> List.contains "llama3.1" models
            | _ -> false)
        |> should equal true
