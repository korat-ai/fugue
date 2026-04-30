module Fugue.Cli.Program

open System
open System.Diagnostics.CodeAnalysis
open Microsoft.Agents.AI
open Fugue.Core.Config
open Fugue.Agent

let private noColor () =
    let isSet name = Environment.GetEnvironmentVariable name |> isNull |> not
    isSet "NO_COLOR" || isSet "FUGUE_NO_COLOR"
    || Environment.GetEnvironmentVariable "TERM" = "dumb"
    || Console.IsOutputRedirected
    || Console.IsInputRedirected

let private buildAgent (cfg: AppConfig) : AIAgent =
    let cwd = Environment.CurrentDirectory
    let tools = Fugue.Tools.ToolRegistry.buildAll cwd
    let basePrompt =
        match cfg.SystemPrompt with
        | Some s -> s
        | None ->
            let ctx = Fugue.Core.SystemPrompt.loadFugueContext cwd
            Fugue.Core.SystemPrompt.render cwd Fugue.Tools.ToolRegistry.names ctx
    let sysPrompt =
        match cfg.ProfileContent with
        | Some profile -> profile + "\n\n---\n\n" + basePrompt
        | None -> basePrompt
    AgentFactory.create cfg.Provider cfg.BaseUrl cfg.MaxTokens sysPrompt tools

[<RequiresUnreferencedCode("Calls Repl.run and Config.saveToFile which use STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
[<RequiresDynamicCode("Calls Repl.run and Config.saveToFile which use STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
let private runWithCfg (cfg: AppConfig) : int =
    let isClassic = cfg.Ui.Theme = "fugue-classic" || cfg.Ui.Theme = "monochrome"
    Render.initColor (not (noColor () || isClassic))
    let emojiEnabled =
        match cfg.Ui.EmojiMode with
        | "always" -> true
        | "never"  -> false
        | _        -> not (Fugue.Core.EmojiMap.terminalSupportsEmoji ())
    MarkdownRender.initEmoji emojiEnabled
    MarkdownRender.initTheme cfg.Ui.Theme
    Render.initTheme cfg.Ui.Theme
    StatusBar.initTheme cfg.Ui.Theme
    Render.initTypewriter cfg.Ui.TypewriterMode
    Render.initBubbles (cfg.Ui.BubblesMode || cfg.Ui.Theme = "bubbles")
    let agent = buildAgent cfg
    let cwd = Environment.CurrentDirectory
    let t =
        if Console.IsInputRedirected then Repl.runHeadless agent cfg cwd
        else Repl.run agent cfg cwd
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
    let profileContent =
        argv
        |> Array.tryFindIndex ((=) "--profile")
        |> Option.bind (fun i -> if i + 1 < argv.Length then Some argv.[i + 1] else None)
        |> Option.bind Fugue.Core.Config.loadProfile
    if argv |> Array.contains "doctor" then
        let cwd = Environment.CurrentDirectory
        let passed = Doctor.run cwd
        if passed then 0 else 1
    else
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
            let cfg = { Provider = provider; SystemPrompt = None; ProfileContent = profileContent; MaxIterations = 30; MaxTokens = None; Ui = Fugue.Core.Config.defaultUi (); BaseUrl = None }
            Fugue.Core.Config.saveToFile cfg
            runWithCfg cfg
        | None ->
            Console.Error.WriteLine(help)
            1
    | Error (InvalidConfig reason) ->
        Console.Error.WriteLine("Invalid config: " + reason)
        1
    | Ok cfg ->
        runWithCfg { cfg with ProfileContent = profileContent }
