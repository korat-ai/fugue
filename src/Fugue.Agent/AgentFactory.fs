module Fugue.Agent.AgentFactory

open System
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open Fugue.Core.Config

// Bring Anthropic extension methods into scope via fully-qualified open
open global.Anthropic

/// Build an AIAgent (with system prompt and tools wired in) for the given provider.
/// `configBaseUrl` is from ~/.fugue/config.json; env vars win if both are set.
/// `maxTokens` caps completion tokens per request (None = provider default).
let create (provider: ProviderConfig) (configBaseUrl: string option) (maxTokens: int option) (systemPrompt: string) (tools: AIFunction list) : AIAgent =
    let toolList : System.Collections.Generic.IList<AITool> =
        ResizeArray<AITool>(tools |> List.map (fun t -> t :> AITool))

    let resolvedBase (envVar: string) =
        match Environment.GetEnvironmentVariable envVar with
        | null | "" -> configBaseUrl
        | url       -> Some url

    // Build ChatClientAgentOptions with optional MaxOutputTokens for providers that use IChatClient.AsAIAgent.
    let makeAgentOpts () : ChatClientAgentOptions =
        let chatOpts = ChatOptions(Instructions = systemPrompt, Tools = toolList)
        maxTokens |> Option.iter (fun t -> chatOpts.MaxOutputTokens <- Nullable t)
        ChatClientAgentOptions(Name = "Fugue", ChatOptions = chatOpts)

    match provider with
    | Anthropic(apiKey, model) ->
        // ANTHROPIC_BASE_URL (env) or config baseUrl allows pointing at local Anthropic-compat servers.
        let opts =
            match resolvedBase "ANTHROPIC_BASE_URL" with
            | None     -> Core.ClientOptions(ApiKey = apiKey)
            | Some url -> Core.ClientOptions(ApiKey = apiKey, BaseUrl = url)
        let client = new AnthropicClient(opts)
        client.AsAIAgent(
            model = model,
            instructions = systemPrompt,
            name = "Fugue",
            description = null,
            tools = toolList,
            defaultMaxTokens = (maxTokens |> Option.map Nullable |> Option.defaultValue (Nullable())))

    | OpenAI(apiKey, model) ->
        // OPENAI_BASE_URL (env) or config baseUrl allows pointing at OpenAI-compat servers.
        let cred = new System.ClientModel.ApiKeyCredential(apiKey)
        let openAi =
            match resolvedBase "OPENAI_BASE_URL" with
            | None     -> new OpenAI.OpenAIClient(cred)
            | Some url -> new OpenAI.OpenAIClient(cred, OpenAI.OpenAIClientOptions(Endpoint = Uri url))
        openAi.GetChatClient(model).AsIChatClient().AsAIAgent(makeAgentOpts ())

    | Ollama(endpoint, model) ->
        let ollama = new OllamaSharp.OllamaApiClient(endpoint, model)
        (ollama :> IChatClient).AsAIAgent(makeAgentOpts ())
