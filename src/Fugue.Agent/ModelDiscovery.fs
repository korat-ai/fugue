module Fugue.Agent.ModelDiscovery

open System
open System.Net.Http
open System.Text.Json.Nodes
open System.Threading.Tasks
open Fugue.Core.Config

/// Hardcoded — Anthropic does not expose /v1/models without an account-scoped header.
let private anthropicKnownModels =
    [ "claude-opus-4-7"; "claude-sonnet-4-6"; "claude-haiku-4-5-20251001" ]

let private timeoutMs = 1500

let private parseOpenAi (body: string) : string list =
    try
        match Option.ofObj (JsonNode.Parse body) with
        | None -> []
        | Some root ->
            match Option.ofObj root.["data"] with
            | None -> []
            | Some dataNode ->
                match (dataNode :> obj) with
                | :? JsonArray as arr ->
                    arr
                    |> Seq.cast<JsonNode | null>
                    |> Seq.choose Option.ofObj
                    |> Seq.choose (fun item ->
                        Option.ofObj item.["id"]
                        |> Option.map (fun n -> n.GetValue<string>()))
                    |> List.ofSeq
                | _ -> []
    with _ -> []

let private parseOllama (body: string) : string list =
    try
        match Option.ofObj (JsonNode.Parse body) with
        | None -> []
        | Some root ->
            match Option.ofObj root.["models"] with
            | None -> []
            | Some modelsNode ->
                match (modelsNode :> obj) with
                | :? JsonArray as arr ->
                    arr
                    |> Seq.cast<JsonNode | null>
                    |> Seq.choose Option.ofObj
                    |> Seq.choose (fun item ->
                        Option.ofObj item.["name"]
                        |> Option.map (fun n -> n.GetValue<string>()))
                    |> List.ofSeq
                | _ -> []
    with _ -> []

let private fetchOpenAi (apiKey: string) (baseUrl: string option) : Task<string list> =
    task {
        try
            use http = new HttpClient(Timeout = TimeSpan.FromMilliseconds(float timeoutMs))
            let root =
                baseUrl
                |> Option.map (fun u -> u.TrimEnd('/'))
                |> Option.defaultValue "https://api.openai.com/v1"
            let url = $"{root}/models"
            use req = new HttpRequestMessage(HttpMethod.Get, url)
            req.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", apiKey)
            let! resp = http.SendAsync req
            if not resp.IsSuccessStatusCode then return []
            else
                let! body = resp.Content.ReadAsStringAsync()
                return parseOpenAi body
        with _ -> return []
    }

let private fetchOllama (endpoint: Uri) : Task<string list> =
    task {
        try
            use http = new HttpClient(Timeout = TimeSpan.FromMilliseconds(float timeoutMs))
            let url = Uri(endpoint, "/api/tags")
            let! resp = http.GetAsync url
            if not resp.IsSuccessStatusCode then return []
            else
                let! body = resp.Content.ReadAsStringAsync()
                return parseOllama body
        with _ -> return []
    }

/// Fetch available model names for the given provider. Returns [] on any failure.
/// HTTP timeout 1500ms — must not block the REPL.
let getModels (provider: ProviderConfig) (baseUrl: string option) : Task<string list> =
    task {
        match provider with
        | Anthropic _ -> return anthropicKnownModels
        | OpenAI(apiKey, _) -> return! fetchOpenAi apiKey baseUrl
        | Ollama(endpoint, _) -> return! fetchOllama endpoint
    }
