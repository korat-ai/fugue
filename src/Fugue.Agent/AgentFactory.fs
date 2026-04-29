module Fugue.Agent.AgentFactory

open System
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open Fugue.Core.Config

// Bring Anthropic extension methods into scope via fully-qualified open
open global.Anthropic

/// Build an AIAgent (with system prompt and tools wired in) for the given provider.
let create (provider: ProviderConfig) (systemPrompt: string) (tools: AIFunction list) : AIAgent =
    let toolList : System.Collections.Generic.IList<AITool> =
        ResizeArray<AITool>(tools |> List.map (fun t -> t :> AITool))

    match provider with
    | Anthropic(apiKey, model) ->
        // ANTHROPIC_BASE_URL allows pointing at local Anthropic-compat servers (LM Studio, proxies).
        let opts =
            match Environment.GetEnvironmentVariable "ANTHROPIC_BASE_URL" with
            | null | "" -> Core.ClientOptions(ApiKey = apiKey)
            | url       -> Core.ClientOptions(ApiKey = apiKey, BaseUrl = url)
        let client = new AnthropicClient(opts)
        client.AsAIAgent(
            model = model,
            instructions = systemPrompt,
            name = "Fugue",
            description = null,
            tools = toolList)

    | OpenAI(apiKey, model) ->
        let openAi = new OpenAI.OpenAIClient(apiKey)
        let chatClient = openAi.GetChatClient(model).AsIChatClient()
        chatClient.AsAIAgent(
            instructions = systemPrompt,
            name = "Fugue",
            description = null,
            tools = toolList)

    | Ollama(endpoint, model) ->
        let ollama = new OllamaSharp.OllamaApiClient(endpoint, model)
        (ollama :> IChatClient).AsAIAgent(
            instructions = systemPrompt,
            name = "Fugue",
            description = null,
            tools = toolList)
