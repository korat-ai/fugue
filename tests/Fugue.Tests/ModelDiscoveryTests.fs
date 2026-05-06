module Fugue.Tests.ModelDiscoveryTests

open System
open Xunit
open FsUnit.Xunit
open Fugue.Core.Config
open Fugue.Agent.ModelDiscovery

// ── Test 1: Anthropic returns the 3 hardcoded model names ─────────────────────

[<Fact>]
let ``getModels Anthropic returns hardcoded list`` () =
    let provider = Anthropic("sk-ant-fake", "claude-opus-4-7")
    let result = getModels provider None |> Async.AwaitTask |> Async.RunSynchronously
    result |> should equal [ "claude-opus-4-7"; "claude-sonnet-4-6"; "claude-haiku-4-5-20251001" ]

// ── Test 2: OpenAI provider with bogus URL returns [] within timeout ───────────

[<Fact>]
let ``getModels OpenAI bogus URL returns empty list`` () =
    let provider = OpenAI("sk-fake", "gpt-4")
    // Point at a port that's not listening — should time out fast and return []
    let result = getModels provider (Some "http://127.0.0.1:19999") |> Async.AwaitTask |> Async.RunSynchronously
    result |> should equal ([] : string list)

// ── Test 3: Ollama provider with bogus endpoint returns [] ────────────────────

[<Fact>]
let ``getModels Ollama bogus endpoint returns empty list`` () =
    let provider = Ollama(Uri "http://127.0.0.1:19998", "llama3")
    let result = getModels provider None |> Async.AwaitTask |> Async.RunSynchronously
    result |> should equal ([] : string list)

// ── Test 4: parseOpenAi parses known-shape JSON blob ─────────────────────────

[<Fact>]
let ``parseOpenAi extracts ids from data array`` () =
    let json = """{"object":"list","data":[{"id":"gpt-4"},{"id":"gpt-3.5-turbo"},{"id":"text-embedding-ada-002"}]}"""
    let result = parseOpenAi json
    result |> should equal [ "gpt-4"; "gpt-3.5-turbo"; "text-embedding-ada-002" ]

// ── Test 5: parseOpenAi returns [] for malformed JSON ────────────────────────

[<Fact>]
let ``parseOpenAi returns empty list for malformed JSON`` () =
    let result = parseOpenAi "not-json"
    result |> should equal ([] : string list)

// ── Test 6: parseOllama extracts names from models array ─────────────────────

[<Fact>]
let ``parseOllama extracts names from models array`` () =
    let json = """{"models":[{"name":"llama3.1:latest"},{"name":"mistral:7b"},{"name":"qwen3:14b"}]}"""
    let result = parseOllama json
    result |> should equal [ "llama3.1:latest"; "mistral:7b"; "qwen3:14b" ]

// ── Test 7: parseOllama returns [] for malformed JSON ────────────────────────

[<Fact>]
let ``parseOllama returns empty list for malformed JSON`` () =
    let result = parseOllama "not-json"
    result |> should equal ([] : string list)
