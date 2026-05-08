module Fugue.Agent.Discovery

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Text.Json.Nodes

type Candidate =
    | EnvKey of provider: string * model: string * keySource: string
    | ClaudeSettings of model: string * apiKey: string
    | OllamaLocal of endpoint: Uri * models: string list

type Sources =
    { ClaudeSettingsPath: string
      OllamaPing: Uri -> Async<string list option> }

let private envCandidate () =
    let env n =
        match Environment.GetEnvironmentVariable n with
        | null | "" -> None
        | v -> Some v
    [ "ANTHROPIC_API_KEY", "anthropic", "claude-opus-4-7"
      "OPENAI_API_KEY", "openai", "gpt-5" ]
    |> List.choose (fun (var, prov, defaultModel) ->
        env var |> Option.map (fun _ -> EnvKey(prov, defaultModel, var)))

let private claudeSettingsCandidate (path: string) =
    if not (File.Exists path) then []
    else
        try
            let json    = File.ReadAllText path
            let nodeOpt = Option.ofObj (JsonNode.Parse json)
            let key =
                nodeOpt
                |> Option.bind  (fun n -> Option.ofObj n["env"])
                |> Option.bind  (fun e -> Option.ofObj e["ANTHROPIC_API_KEY"])
                |> Option.map   (fun kn -> kn.GetValue<string>())
            let model =
                nodeOpt
                |> Option.bind  (fun n -> Option.ofObj n["model"])
                |> Option.map   (fun mn -> mn.GetValue<string>())
                |> Option.defaultValue "claude-opus-4-7"
            match key with
            | Some k -> [ ClaudeSettings(model, k) ]
            | None -> []
        with _ -> []

let httpOllamaPing (timeoutMs: int) (endpoint: Uri) : Async<string list option> =
    async {
        try
            use http = new HttpClient(Timeout = TimeSpan.FromMilliseconds(float timeoutMs))
            let! resp = http.GetAsync(Uri(endpoint, "/api/tags")) |> Async.AwaitTask
            if not resp.IsSuccessStatusCode then return None
            else
                let! body   = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                let nodeOpt = Option.ofObj (JsonNode.Parse body)
                let names =
                    nodeOpt
                    |> Option.map (fun n ->
                        Option.ofObj n["models"]
                        |> Option.bind (fun m ->
                            match box m with
                            | :? JsonArray as arr -> Some arr
                            | _ -> None)
                        |> Option.map (fun arr ->
                            arr
                            |> Seq.cast<JsonNode | null>
                            |> Seq.choose Option.ofObj
                            |> Seq.choose (fun m ->
                                Option.ofObj m["name"]
                                |> Option.map (fun kn -> kn.GetValue<string>()))
                            |> List.ofSeq)
                        |> Option.defaultValue [])
                return names
        with _ -> return None
    }

let discover (sources: Sources) : Candidate list =
    let env = envCandidate ()
    let claude = claudeSettingsCandidate sources.ClaudeSettingsPath
    let ollamaEndpoint = Uri "http://localhost:11434"
    let ollama =
        sources.OllamaPing ollamaEndpoint
        |> Async.RunSynchronously
        |> Option.map (fun models -> [ OllamaLocal(ollamaEndpoint, models) ])
        |> Option.defaultValue []
    env @ claude @ ollama

let defaultSources () : Sources =
    let home =
        match Environment.GetEnvironmentVariable "HOME" with
        | null | "" ->
            match Option.ofObj (Environment.GetEnvironmentVariable "USERPROFILE") with
            | None -> "."
            | Some p -> p
        | h -> h
    { ClaudeSettingsPath = Path.Combine(home, ".claude", "settings.json")
      OllamaPing = httpOllamaPing 200 }

/// Render numeric menu, read user choice from stdin (1-based or 'q').
let prompt (candidates: Candidate list) : Candidate option =
    if List.isEmpty candidates then None
    else
        Console.Error.WriteLine("")
        Console.Error.WriteLine("No ~/.fugue/config.json found. I detected:")
        candidates
        |> List.iteri (fun i c ->
            let label =
                match c with
                | EnvKey(p, m, src) -> p + " — using " + src + " from env (model: " + m + ")"
                | ClaudeSettings(m, _) -> "Anthropic — reuse Claude Code config (model: " + m + ")"
                | OllamaLocal(ep, models) -> "Ollama — " + string ep + " (" + string (List.length models) + " models available)"
            Console.Error.WriteLine("  [" + string (i + 1) + "] " + label))
        Console.Error.Write("\nPick one [1-" + string candidates.Length + "], or 'q' to quit: ")
        let line = Console.ReadLine()
        match line with
        | null | "q" | "Q" -> None
        | s ->
            match Int32.TryParse s with
            | true, n when n >= 1 && n <= candidates.Length -> Some candidates[n - 1]
            | _ -> None
