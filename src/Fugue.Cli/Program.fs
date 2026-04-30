module Fugue.Cli.Program

open System
open System.Diagnostics.CodeAnalysis
open Microsoft.Agents.AI
open Fugue.Core.Config
open Fugue.Agent

let private buildAgent (cfg: AppConfig) : AIAgent =
    let cwd = Environment.CurrentDirectory
    let tools = Fugue.Tools.ToolRegistry.buildAll cwd
    let sysPrompt =
        match cfg.SystemPrompt with
        | Some s -> s
        | None -> Fugue.Core.SystemPrompt.render cwd Fugue.Tools.ToolRegistry.names
    AgentFactory.create cfg.Provider cfg.BaseUrl cfg.MaxTokens sysPrompt tools

[<RequiresUnreferencedCode("Calls Repl.run and Config.saveToFile which use STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
[<RequiresDynamicCode("Calls Repl.run and Config.saveToFile which use STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
let private runWithCfg (cfg: AppConfig) : int =
    let agent = buildAgent cfg
    let cwd = Environment.CurrentDirectory
    let t = Repl.run agent cfg cwd
    try t.Wait()
    with
    | :? AggregateException as agg ->
        match agg.InnerException with
        | :? OperationCanceledException -> ()
        | _ -> raise agg
    0

[<EntryPoint>]
let main argv =
    Fugue.Core.Log.session ()
    Fugue.Core.Log.info "main" ("fugue starting, argv=[" + String.concat "; " argv + "]")
    match Fugue.Core.Config.load argv with
    | Error (NoConfigFound help) ->
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
                | Discovery.ClaudeSettings(model, key) -> Anthropic(key, model)
                | Discovery.OllamaLocal(ep, models) ->
                    let m = if List.isEmpty models then "llama3.1" else List.head models
                    Ollama(ep, m)
                | _ -> failwith "unsupported candidate"
            let cfg = { Provider = provider; SystemPrompt = None; MaxIterations = 30; MaxTokens = None; Ui = Fugue.Core.Config.defaultUi (); BaseUrl = None }
            Fugue.Core.Config.saveToFile cfg
            runWithCfg cfg
        | None ->
            Console.Error.WriteLine(help)
            1
    | Error (InvalidConfig reason) ->
        Console.Error.WriteLine("Invalid config: " + reason)
        1
    | Ok cfg ->
        runWithCfg cfg
