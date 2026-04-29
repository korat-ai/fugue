module Fugue.Cli.Program

open System
open System.Threading
open Microsoft.Agents.AI
open Fugue.Core.Config
open Fugue.Agent
open Fugue.Cli.Model
open Fugue.Cli.Mvu

let private buildAgent (cfg: AppConfig) : AIAgent =
    let cwd = Environment.CurrentDirectory
    let tools = Fugue.Tools.ToolRegistry.buildAll cwd
    let sysPrompt =
        match cfg.SystemPrompt with
        | Some s -> s
        | None -> Fugue.Core.SystemPrompt.render cwd Fugue.Tools.ToolRegistry.names
    AgentFactory.create cfg.Provider sysPrompt tools

/// Cmd that starts the conversation stream and dispatches events into the MVU loop.
let private streamCmd (agent: AIAgent) (session: AgentSession option) (userInput: string) : Cmd<Msg> =
    [ fun dispatch ->
        let cts = new CancellationTokenSource()
        Fugue.Core.Log.info "stream" (sprintf "streamCmd START, input='%s' (len=%d)" userInput userInput.Length)
        dispatch (StreamStarted cts)
        // Convert F# option to .NET nullable (null = create/continue internal session in MAF).
        let sessionOrNull : AgentSession | null = Option.toObj session
        Async.Start(async {
            try
                let stream = Conversation.run agent sessionOrNull userInput cts.Token
                let enumerator = stream.GetAsyncEnumerator(cts.Token)
                let mutable hasNext = true
                let mutable evIdx = 0
                while hasNext do
                    let! step =
                        enumerator.MoveNextAsync().AsTask() |> Async.AwaitTask
                    if step then
                        evIdx <- evIdx + 1
                        let ev = enumerator.Current
                        let evDesc =
                            match ev with
                            | Conversation.TextChunk s -> sprintf "TextChunk(len=%d)" s.Length
                            | Conversation.ToolStarted(id, n, _) -> sprintf "ToolStarted(%s, %s)" id n
                            | Conversation.ToolCompleted(id, _, e) -> sprintf "ToolCompleted(%s, isErr=%b)" id e
                            | Conversation.Finished -> "Finished"
                            | Conversation.Failed ex -> sprintf "Failed(%s: %s)" (ex.GetType().Name) ex.Message
                        Fugue.Core.Log.info "stream" (sprintf "ev #%d %s, ct.IsCancelled=%b" evIdx evDesc cts.IsCancellationRequested)
                        match ev with
                        | Conversation.TextChunk s          -> dispatch (Msg.TextChunk s)
                        | Conversation.ToolStarted(id, n, a) -> dispatch (Msg.ToolStarted(id, n, a))
                        | Conversation.ToolCompleted(id, o, e) -> dispatch (Msg.ToolCompleted(id, o, e))
                        | Conversation.Finished             -> dispatch Msg.StreamFinished
                        | Conversation.Failed ex            -> dispatch (Msg.StreamFailed ex.Message)
                    else
                        Fugue.Core.Log.info "stream" "MoveNextAsync returned false → loop end"
                        hasNext <- false
                Fugue.Core.Log.info "stream" "streamCmd END (clean exit)"
            with
            | :? OperationCanceledException as oce ->
                Fugue.Core.Log.warn "stream" (sprintf "outer catch: OperationCanceledException: %s" oce.Message)
                dispatch (Msg.StreamFailed "cancelled")
            | ex ->
                Fugue.Core.Log.ex "stream" "outer catch" ex
                dispatch (Msg.StreamFailed ex.Message)
        })
    ]

let private runWithCfg (cfg: AppConfig) : int =
    let agent = buildAgent cfg

    let init () : Model * Cmd<Msg> =
        { Config    = cfg
          Session   = None
          History   = []
          Input     = ""
          Streaming = false
          Cancel    = None
          Status    = "" }, Cmd.none

    let updateWithEffects (msg: Msg) (model: Model) : Model * Cmd<Msg> =
        let m' = Update.update msg model
        match msg with
        | Submit when m'.Streaming ->
            // Update set Streaming=true and appended UserMsg; grab it for the API call.
            let userInput =
                match List.tryLast m'.History with
                | Some (UserMsg s) -> s
                | _ -> ""
            m', streamCmd agent m'.Session userInput
        | _ -> m', Cmd.none

    Mvu.run init updateWithEffects View.view
    0

[<EntryPoint>]
let main argv =
    Fugue.Core.Log.session ()
    Fugue.Core.Log.info "main" (sprintf "fugue starting, argv=[%s]" (String.concat "; " argv))
    match Fugue.Core.Config.load argv with
    | Error (NoConfigFound help) ->
        // Discovery fallback: prompt user to pick a provider interactively.
        let candidates = Discovery.discover (Discovery.defaultSources ())
        match Discovery.prompt candidates with
        | Some chosen ->
            let provider =
                match chosen with
                | Discovery.EnvKey("anthropic", model, _) ->
                    let key = Environment.GetEnvironmentVariable "ANTHROPIC_API_KEY" |> Option.ofObj |> Option.defaultValue ""
                    Anthropic(key, model)
                | Discovery.EnvKey("openai", model, _) ->
                    let key = Environment.GetEnvironmentVariable "OPENAI_API_KEY" |> Option.ofObj |> Option.defaultValue ""
                    OpenAI(key, model)
                | Discovery.ClaudeSettings(model, key) ->
                    Anthropic(key, model)
                | Discovery.OllamaLocal(ep, models) ->
                    let m = if List.isEmpty models then "llama3.1" else List.head models
                    Ollama(ep, m)
                | _ -> failwith "unsupported candidate"
            let cfg = { Provider = provider; SystemPrompt = None; MaxIterations = 30; Ui = Fugue.Core.Config.defaultUi () }
            Fugue.Core.Config.saveToFile cfg
            runWithCfg cfg
        | None ->
            eprintfn "%s" help
            1
    | Error (InvalidConfig reason) ->
        eprintfn "Invalid config: %s" reason
        1
    | Ok cfg ->
        runWithCfg cfg
