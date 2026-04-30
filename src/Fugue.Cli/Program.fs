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

let private buildAgent (cfg: AppConfig) (lastSummary: string option) : AIAgent =
    let cwd = Environment.CurrentDirectory
    let tools = Fugue.Tools.ToolRegistry.buildAll cwd
    let basePrompt =
        match cfg.SystemPrompt with
        | Some s -> s
        | None ->
            let ctx = if cfg.LowBandwidth then None else Fugue.Core.SystemPrompt.loadFugueContext cwd
            Fugue.Core.SystemPrompt.render cwd Fugue.Tools.ToolRegistry.names ctx
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
    let lastSummary = if cfg.LowBandwidth then None else Fugue.Core.SessionSummary.loadLast (Environment.CurrentDirectory)
    let agent = buildAgent cfg lastSummary
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
    let agent = buildAgent cfg lastSummary
    let t =
        if Console.IsInputRedirected then Repl.runHeadless agent cfg cwd
        else Repl.run agent cfg cwd lastSummary
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
    let templateName =
        argv
        |> Array.tryFindIndex ((=) "--template")
        |> Option.bind (fun i -> if i + 1 < argv.Length then Some argv.[i + 1] else None)
    let templateContent = templateName |> Option.bind Fugue.Core.Config.loadTemplate
    let lowBandwidth = argv |> Array.contains "--low-bandwidth"
    let offline      = argv |> Array.contains "--offline"
    let printPrompt =
        argv
        |> Array.tryFindIndex ((=) "--print")
        |> Option.bind (fun i -> if i + 1 < argv.Length then Some argv.[i + 1] else None)
    if argv |> Array.contains "doctor" then
        let cwd = Environment.CurrentDirectory
        let passed = Doctor.run cwd
        if passed then 0 else 1
    elif argv |> Array.contains "init" then
        let cwd = Environment.CurrentDirectory
        let fugueMd = System.IO.Path.Combine(cwd, "FUGUE.md")
        if System.IO.File.Exists fugueMd then
            Console.Error.WriteLine "FUGUE.md already exists. Edit it directly or delete it first."
            1
        else
        // Detect project type from files in cwd
        let hasFile pat =
            try System.IO.Directory.GetFiles(cwd, pat, System.IO.SearchOption.TopDirectoryOnly).Length > 0
            with _ -> false
        let hasDir name = System.IO.Directory.Exists(System.IO.Path.Combine(cwd, name))
        let stack =
            if hasFile "*.fsproj"   then "F# / .NET"
            elif hasFile "*.csproj" then "C# / .NET"
            elif hasFile "package.json" then "Node.js / TypeScript"
            elif hasFile "mix.exs"  then "Elixir / Mix"
            elif hasFile "build.sbt" || hasFile "*.scala" then "Scala / SBT"
            elif hasFile "pom.xml"  || hasFile "build.gradle" then "Java / JVM"
            elif hasFile "Cargo.toml" then "Rust / Cargo"
            elif hasFile "go.mod"   then "Go"
            elif hasFile "pyproject.toml" || hasFile "setup.py" then "Python"
            else "Unknown"
        let template =
            sprintf "# Project context\n\n## Stack\n%s\n\n## Purpose\n<!-- Describe what this project does -->\n\n## Key constraints\n<!-- Performance, security, platform, team rules -->\n\n## Common tasks\n<!-- What does the AI help you with most often? -->\n" stack
        System.IO.File.WriteAllText(fugueMd, template)
        Console.WriteLine(sprintf "Detected: %s" stack)
        Console.WriteLine(sprintf "✓ Created FUGUE.md (%d bytes)" (System.IO.File.ReadAllBytes(fugueMd).Length))
        // Add to .gitignore if it exists and doesn't already include it
        let gitignore = System.IO.Path.Combine(cwd, ".gitignore")
        if System.IO.File.Exists gitignore then
            let content = System.IO.File.ReadAllText gitignore
            if not (content.Contains "FUGUE.md") then
                System.IO.File.AppendAllText(gitignore, "\nFUGUE.md\n")
                Console.WriteLine "✓ Added FUGUE.md to .gitignore"
        // Create .fugue/hooks/ dir
        let hooksDir = System.IO.Path.Combine(cwd, ".fugue", "hooks")
        System.IO.Directory.CreateDirectory hooksDir |> ignore
        Console.WriteLine "✓ Created .fugue/hooks/ directory"
        Console.WriteLine "Edit FUGUE.md to add project-specific context, then run: fugue"
        0
    elif argv |> Array.contains "aliases" then
        let install = argv |> Array.contains "--install"
        let lines =
            [ "# Fugue shell aliases"
              "alias fask='fugue --print'"
              "alias fnew='fugue --new-session'"
              "alias fdoctor='fugue doctor'"
              "alias faliases='fugue aliases'" ]
        if install then
            let shellRc =
                let zshrc = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zshrc")
                let bashrc = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bashrc")
                if System.IO.File.Exists zshrc then zshrc else bashrc
            let block = "\n" + (lines |> String.concat "\n") + "\n"
            System.IO.File.AppendAllText(shellRc, block)
            Console.WriteLine(sprintf "✓ Appended %d aliases to %s" (lines.Length - 1) shellRc)
            Console.WriteLine(sprintf "  Reload with: source %s" shellRc)
        else
            for l in lines do Console.WriteLine l
            Console.WriteLine()
            Console.WriteLine "Run 'fugue aliases --install' to append these to ~/.zshrc automatically."
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
            let cfg = { Provider = provider; SystemPrompt = None; ProfileContent = profileContent; TemplateContent = templateContent; TemplateName = templateName; MaxIterations = 30; MaxTokens = None; Ui = Fugue.Core.Config.defaultUi (); BaseUrl = None; LowBandwidth = lowBandwidth; Offline = offline }
            Fugue.Core.Config.saveToFile cfg
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
        let fullCfg = { cfg with ProfileContent = profileContent; TemplateContent = templateContent; TemplateName = templateName; LowBandwidth = lowBandwidth; Offline = offline }
        match printPrompt with
        | Some p -> runWithPrint fullCfg p
        | None   -> runWithCfg fullCfg
