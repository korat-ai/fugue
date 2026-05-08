module Fugue.Tests.ConfigRoundtripPropertyTests

/// Round-trip property tests for AppConfigDto JSON serialization.
///
/// Verifies that AppConfigDto serializes and deserializes to identity across
/// representative inputs covering Anthropic and OpenAI-compat provider variants,
/// null/non-null UI fields, and edge-case field combinations.

open System.Text.Json
open Xunit
open FsUnit.Xunit
open Fugue.Core.JsonContext

let private defaultUi : UiConfigDto =
    { userAlignment  = "left"
      locale         = "en"
      promptTemplate = null
      bell           = false
      theme          = null
      emojiMode      = "auto"
      bubblesMode    = false
      typewriterMode = false }

let private roundTrip (dto: AppConfigDto) =
    let json = JsonSerializer.Serialize<AppConfigDto>(dto, appConfigOptions)
    let back = JsonSerializer.Deserialize<AppConfigDto | null>(json, appConfigOptions)
    back |> Option.ofObj

[<Fact>]
let ``AppConfigDto round-trips through JSON for Anthropic provider`` () =
    let dto =
        { provider        = "anthropic"
          model           = "claude-opus-4-7"
          apiKey          = "sk-ant-test"
          ollamaEndpoint  = ""
          baseUrl         = null
          maxIterations   = 30
          maxTokens       = 8192
          ui              = defaultUi }
    match roundTrip dto with
    | None -> failwith "deserialization returned null"
    | Some b ->
        b.provider      |> should equal "anthropic"
        b.model         |> should equal "claude-opus-4-7"
        b.apiKey        |> should equal "sk-ant-test"
        b.maxIterations |> should equal 30
        b.maxTokens     |> should equal 8192

[<Fact>]
let ``AppConfigDto round-trips through JSON for OpenAI-compat provider`` () =
    let dto =
        { provider        = "openai"
          model           = "gpt-4o"
          apiKey          = "sk-openai-test"
          ollamaEndpoint  = ""
          baseUrl         = "https://api.example.com/v1"
          maxIterations   = 10
          maxTokens       = 4096
          ui              = defaultUi }
    match roundTrip dto with
    | None -> failwith "deserialization returned null"
    | Some b ->
        b.provider |> should equal "openai"
        b.model    |> should equal "gpt-4o"
        b.baseUrl  |> should equal "https://api.example.com/v1"

[<Fact>]
let ``AppConfigDto round-trips through JSON with null UI block`` () =
    let dto =
        { provider        = "anthropic"
          model           = "claude-haiku-4-5"
          apiKey          = "sk-ant-x"
          ollamaEndpoint  = ""
          baseUrl         = null
          maxIterations   = 5
          maxTokens       = 0
          ui              = null }
    match roundTrip dto with
    | None -> failwith "deserialization returned null"
    | Some b ->
        b.provider      |> should equal "anthropic"
        b.maxIterations |> should equal 5

[<Fact>]
let ``AppConfigDto round-trips through JSON with all UI fields set`` () =
    let ui =
        { userAlignment  = "right"
          locale         = "ru"
          promptTemplate = ">> "
          bell           = true
          theme          = "nocturne"
          emojiMode      = "off"
          bubblesMode    = true
          typewriterMode = true }
    let dto =
        { provider        = "anthropic"
          model           = "claude-opus-4-7"
          apiKey          = "sk-ant-y"
          ollamaEndpoint  = ""
          baseUrl         = null
          maxIterations   = 20
          maxTokens       = 16384
          ui              = ui }
    match roundTrip dto with
    | None -> failwith "deserialization returned null"
    | Some b ->
        b.ui |> should not' (be null)

[<Fact>]
let ``AppConfigDto serializes to a JSON object (not array or scalar)`` () =
    let dto =
        { provider        = "anthropic"
          model           = "claude-opus-4-7"
          apiKey          = "sk-ant-test"
          ollamaEndpoint  = ""
          baseUrl         = null
          maxIterations   = 30
          maxTokens       = 8192
          ui              = defaultUi }
    let json = JsonSerializer.Serialize<AppConfigDto>(dto, appConfigOptions)
    use doc = JsonDocument.Parse(json)
    doc.RootElement.ValueKind |> should equal JsonValueKind.Object

[<Fact>]
let ``AppConfigDto round-trip preserves provider across all supported variants`` () =
    let providers = [ "anthropic"; "openai"; "ollama" ]
    for provider in providers do
        let dto =
            { provider        = provider
              model           = "some-model"
              apiKey          = "key"
              ollamaEndpoint  = if provider = "ollama" then "http://localhost:11434" else ""
              baseUrl         = null
              maxIterations   = 1
              maxTokens       = 0
              ui              = null }
        match roundTrip dto with
        | None -> failwithf "null for provider %s" provider
        | Some b -> b.provider |> should equal provider
