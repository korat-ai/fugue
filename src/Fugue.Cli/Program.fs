module Fugue.Cli.Program

open System
open System.Diagnostics.CodeAnalysis
open Microsoft.Agents.AI
open Fugue.Core.Config
open Fugue.Agent
open Fugue.Surface

let private noColor () =
    let isSet name = Environment.GetEnvironmentVariable name |> isNull |> not
    isSet "NO_COLOR" || isSet "FUGUE_NO_COLOR"
    || Environment.GetEnvironmentVariable "TERM" = "dumb"
    || Console.IsOutputRedirected
    || Console.IsInputRedirected

[<System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Calls ToolRegistry.buildAll which uses AgentSessionExtensions over STJ state")>]
[<System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Calls ToolRegistry.buildAll which uses AgentSessionExtensions over STJ state")>]
let private buildAgent (cfg: AppConfig) (hooksConfig: Fugue.Core.Hooks.HooksConfig) (providerContext: string option) (lastSummary: string option) (sessionId: string) (sessionRef: (Microsoft.Agents.AI.AgentSession | null) ref) : AIAgent =
    let cwd = Environment.CurrentDirectory
    let getSession () = sessionRef.Value
    let rawTools = Fugue.Tools.ToolRegistry.buildAll cwd hooksConfig sessionId getSession
    let tools = if cfg.DryRun then DryRun.wrapTools rawTools else rawTools
    let basePrompt =
        match cfg.SystemPrompt with
        | Some s -> s
        | None ->
            let ctx = if cfg.LowBandwidth then None else Fugue.Core.SystemPrompt.loadFugueContext cwd
            Fugue.Core.SystemPrompt.render cwd Fugue.Tools.ToolRegistry.names ctx
    let basePrompt =
        match providerContext with
        | Some ctx when ctx <> "" -> ctx + "\n\n" + basePrompt
        | _ -> basePrompt
    let sysPrompt =
        match cfg.ProfileContent with
        | Some profile -> profile + "\n\n---\n\n" + basePrompt
        | None -> basePrompt
    let sysPrompt =
        match cfg.TemplateContent with
        | Some tmpl -> sysPrompt + "\n\n---\n\n# Session template\n\n" + tmpl
        | None -> sysPrompt
    let sysPromptFull =
        match lastSummary with
        | Some s -> sysPrompt + "\n\n<previous-session>\n" + s + "\n</previous-session>"
        | None   -> sysPrompt
    AgentFactory.create cfg.Provider cfg.BaseUrl cfg.MaxTokens sysPromptFull tools

[<RequiresUnreferencedCode("Calls Repl.runPrint which uses STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
[<RequiresDynamicCode("Calls Repl.runPrint which uses STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
let private runWithPrint (cfg: AppConfig) (prompt: string) : int =
    let lastSummary  = if cfg.LowBandwidth then None else Fugue.Core.SessionSummary.loadLast (Environment.CurrentDirectory)
    let hooksConfig  = Fugue.Core.Hooks.load ()
    let providerContext =
        if cfg.LowBandwidth || hooksConfig.ContextProviders.IsEmpty then None
        else
            let assembled =
                Fugue.Core.ProviderCache.assembleWithProviders
                    hooksConfig.ContextProviders hooksConfig.HookTimeoutMs
                |> Async.RunSynchronously
            if assembled = "" then None else Some assembled
    let sessionId = System.Guid.NewGuid().ToString("N")
    let sessionRef : (Microsoft.Agents.AI.AgentSession | null) ref = ref null
    let agent = buildAgent cfg hooksConfig providerContext lastSummary sessionId sessionRef
    let t = Repl.runPrint agent prompt
    try
        t.Wait()
        t.Result
    with
    | :? AggregateException as agg ->
        match agg.InnerException with
        | :? OperationCanceledException -> 0
        | _ -> raise agg

[<RequiresUnreferencedCode("Calls Repl.run and Config.saveToFile which use STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
[<RequiresDynamicCode("Calls Repl.run and Config.saveToFile which use STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
let private runWithCfg (cfg: AppConfig) : int =
    let isRemote = match cfg.Provider with Anthropic _ | OpenAI _ -> true | _ -> false
    if cfg.Offline && isRemote then
        Console.Error.WriteLine("--offline requires a local provider (Ollama). Set FUGUE_PROVIDER=ollama or update ~/.fugue/config.json.")
        1
    else
    // Phase 1.3c v2: every writer in Fugue.Cli posts through this actor via
    // the Surface module. No global Console.SetOut — each call-site is
    // explicit (see Surface.fs). The actor is the single serialised writer
    // to the real terminal; heartbeat / streaming / slash commands /
    // ReadLine / Picker / banners — all funnel through one mailbox queue.
    let surfaceActor =
        if Console.IsInputRedirected then None
        else
            let actor = RealExecutor.start ()
            Surface.setAgent actor
            StatusBar.setAgent actor
            ReadLine.setAgent actor
            Some actor
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
    StatusBar.setLowBandwidth cfg.LowBandwidth
    StatusBar.setTemplateName cfg.TemplateName
    Render.initTypewriter cfg.Ui.TypewriterMode
    Render.initBubbles (cfg.Ui.BubblesMode || cfg.Ui.Theme = "bubbles")
    // Apply model schedule override if configured
    let cfg, scheduleActive =
        match Fugue.Core.Config.loadModelSchedule () with
        | None -> cfg, false
        | Some sched ->
            let scheduledModel = Fugue.Core.ModelSchedule.resolveModel sched DateTimeOffset.Now
            let overriddenProvider =
                match cfg.Provider with
                | Anthropic(k, m) when scheduledModel <> m -> Anthropic(k, scheduledModel)
                | OpenAI(k, m)    when scheduledModel <> m -> OpenAI(k, scheduledModel)
                | Ollama(ep, m)   when scheduledModel <> m -> Ollama(ep, scheduledModel)
                | p -> p
            { cfg with Provider = overriddenProvider }, (overriddenProvider <> cfg.Provider)
    StatusBar.setScheduleActive scheduleActive
    let cwd = Environment.CurrentDirectory
    let lastSummary = if cfg.LowBandwidth then None else Fugue.Core.SessionSummary.loadLast cwd
    let hooksConfig = Fugue.Core.Hooks.load ()
    let providerContext =
        if cfg.LowBandwidth || hooksConfig.ContextProviders.IsEmpty then None
        else
            let assembled =
                Fugue.Core.ProviderCache.assembleWithProviders
                    hooksConfig.ContextProviders hooksConfig.HookTimeoutMs
                |> Async.RunSynchronously
            if assembled = "" then None else Some assembled
    // Stable sessionId for the lifetime of this REPL invocation. Reused on
    // /new and /model set rebuilds so hooks see consistent correlation.
    let sessionId = System.Guid.NewGuid().ToString("N")
    // Single ref shared across the agent lifetime — survives /new and /model set
    // rebuilds because we pass the same ref into every buildAgent call. The
    // GetConversation tool reads through this ref; Repl.run mutates it after
    // CreateSessionAsync and on each /new / /clear-history. Replaces an older
    // module-level mutable in GetConversationFn (#50).
    let sessionRef : (Microsoft.Agents.AI.AgentSession | null) ref = ref null
    let aiAgent = buildAgent cfg hooksConfig providerContext lastSummary sessionId sessionRef
    let t =
        if Console.IsInputRedirected then Repl.runHeadless aiAgent cfg cwd
        else Repl.run aiAgent sessionRef cfg cwd lastSummary (fun newCfg -> buildAgent newCfg hooksConfig providerContext None sessionId sessionRef)
    try t.Wait()
    with
    | :? AggregateException as agg ->
        match agg.InnerException with
        | :? OperationCanceledException -> ()
        | _ -> raise agg
    // Graceful shutdown: StatusBar.stop() already posted ExecuteAndAck for erase ops.
    // Now shut down the actor so any remaining buffered ops flush before process exits.
    match surfaceActor with
    | Some actor ->
        actor.PostAndAsyncReply(fun ch -> SurfaceMessage.Shutdown ch)
        |> Async.RunSynchronously
    | None -> ()
    0

[<EntryPoint>]
let main argv =
    Fugue.Core.Log.session ()
    Fugue.Core.Log.info "main" ("fugue starting, argv=[" + String.concat "; " argv + "]")
    CaBundle.install () |> ignore
    // Phase 2a of #930 / #928: route migrated subcommands through the
    // Fugue.Core.CliArgs DSL.  Today: `doctor`, `reindex`, `init`, `version`.
    // argv[0] pre-check decides — anything else falls through to the legacy
    // elif chain below (which still owns `man`, `aliases`, `env`, `config`,
    // `--help`/`-h`, `--version` flag, default REPL, `--print` flow).
    if ProgramArgs.isMigrated argv then
        ProgramArgs.dispatch argv
    else
    let profileContent =
        argv
        |> Array.tryFindIndex ((=) "--profile")
        |> Option.bind (fun i -> if i + 1 < argv.Length then Some argv.[i + 1] else None)
        |> Option.bind Fugue.Core.Config.loadProfile
    let templateName =
        argv
        |> Array.tryFindIndex ((=) "--template")
        |> Option.bind (fun i -> if i + 1 < argv.Length then Some argv.[i + 1] else None)
    let templateContent = templateName |> Option.bind Fugue.Core.Config.loadTemplate
    let lowBandwidth = argv |> Array.contains "--low-bandwidth"
    let offline      = argv |> Array.contains "--offline"
    let dryRun       = argv |> Array.contains "--dry-run"
    // --mode <plan|default|auto-edit|yolo> — initial approval mode for the
    // session. Defaults to Default if omitted; unknown value falls back to
    // Default with a warning.
    let initialApprovalMode =
        argv
        |> Array.tryFindIndex ((=) "--mode")
        |> Option.bind (fun i -> if i + 1 < argv.Length then Some argv.[i + 1] else None)
        |> Option.bind Fugue.Core.ApprovalMode.tryParse
        |> Option.defaultValue Fugue.Core.ApprovalMode.Default
    let printPrompt =
        argv
        |> Array.tryFindIndex ((=) "--print")
        |> Option.bind (fun i -> if i + 1 < argv.Length then Some argv.[i + 1] else None)
    if argv |> Array.exists (fun a -> a = "--help" || a = "-h") then
        Console.Write """Usage: fugue [--profile <name>] [--template <name>] [--low-bandwidth] [--offline] [--mode <m>] [--print "prompt"]
       fugue doctor | init | aliases | man | reindex

Subcommands:
  doctor    Run environment diagnostics
  init      Bootstrap FUGUE.md in current directory
  aliases   Print/install shell aliases
  man       Display full manual page
  reindex   Rebuild FTS5 search index from all session JSONL files

Options:
  --profile <name>    Load ~/.fugue/profiles/<name>.md as system-prompt prefix
  --template <name>   Load ~/.fugue/templates/<name>.md as session template
  --low-bandwidth     Skip session summary, cap tool output to 500 lines
  --offline           Require a local provider (Ollama)
  --mode <m>          Initial approval mode: plan, default, auto-edit, yolo
                      (Shift+Tab cycles in REPL)
  --print "prompt"    Non-interactive: send one prompt, stream to stdout, exit

Run `fugue man` for the full manual.
"""
        0
    elif argv |> Array.contains "--version" then
        // `version` subcommand is migrated to ProgramArgs (Phase 2a, #930);
        // the `--version` flag form stays here until Phase 2c migrates the
        // top-level options.
        let asm = System.Reflection.Assembly.GetExecutingAssembly()
        let ver =
            let attr = asm.GetCustomAttributes(typeof<System.Reflection.AssemblyInformationalVersionAttribute>, false)
            if attr.Length > 0 then
                (attr.[0] :?> System.Reflection.AssemblyInformationalVersionAttribute).InformationalVersion
            else
                match asm.GetName().Version |> Option.ofObj with
                | Some v -> $"{v.Major}.{v.Minor}.{v.Build}"
                | None   -> "0.0.0"
        let verbose = argv |> Array.contains "--verbose"
        if verbose then
            let rid     = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier
            let rtVer   = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
            let asmPath = System.AppContext.BaseDirectory
            let cfgPath = Fugue.Core.Config.configFilePath ()
            let home    = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            let profDir = System.IO.Path.Combine(home, ".fugue", "profiles")
            let tmplDir = System.IO.Path.Combine(home, ".fugue", "templates")
            Console.WriteLine($"fugue {ver}")
            Console.WriteLine($"  Target RID:    {rid}")
            Console.WriteLine($"  .NET runtime:  {rtVer}")
            let binaryDisplay = if asmPath = "" then "(AOT native)" else asmPath
            Console.WriteLine($"  Binary:        {binaryDisplay}")
            Console.WriteLine($"  Config file:   {cfgPath}")
            Console.WriteLine($"  Profiles dir:  {profDir}")
            Console.WriteLine($"  Templates dir: {tmplDir}")
        else
            Console.WriteLine($"fugue {ver}")
        0
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
            let cfg = { Provider = provider; SystemPrompt = None; ProfileContent = profileContent; TemplateContent = templateContent; TemplateName = templateName; MaxIterations = 30; MaxTokens = None; Ui = Fugue.Core.Config.defaultUi (); BaseUrl = None; LowBandwidth = lowBandwidth; Offline = offline; DryRun = dryRun }
            Fugue.Core.Config.saveToFile cfg
            // Apply --mode to StatusBar before any rendering begins.
            StatusBar.setApprovalMode initialApprovalMode
            // Install approval gate before tools are invoked. Interactive iff stdin
            // is a TTY (matches the Surface-actor allocation rule).
            let interactive = not Console.IsInputRedirected
            Fugue.Tools.AiFunctions.DelegatedFn.setApprovalGate
                (Fugue.Cli.ApprovalPrompt.buildGate StatusBar.getApprovalMode interactive)
            match printPrompt with
            | Some p -> runWithPrint cfg p
            | None   -> runWithCfg cfg
        | None ->
            Console.Error.WriteLine(help)
            1
    | Error (InvalidConfig reason) ->
        Console.Error.WriteLine("Invalid config: " + reason)
        1
    | Ok cfg ->
        let fullCfg = { cfg with ProfileContent = profileContent; TemplateContent = templateContent; TemplateName = templateName; LowBandwidth = lowBandwidth; Offline = offline; DryRun = dryRun }
        // Apply --mode to StatusBar before any rendering begins.
        StatusBar.setApprovalMode initialApprovalMode
        // Install approval gate before tools are invoked. Interactive iff stdin
        // is a TTY (matches the Surface-actor allocation rule).
        let interactive = not Console.IsInputRedirected
        Fugue.Tools.AiFunctions.DelegatedFn.setApprovalGate
            (Fugue.Cli.ApprovalPrompt.buildGate StatusBar.getApprovalMode interactive)
        match printPrompt with
        | Some p -> runWithPrint fullCfg p
        | None   -> runWithCfg fullCfg
