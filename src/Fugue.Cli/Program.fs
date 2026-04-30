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
    let rawTools = Fugue.Tools.ToolRegistry.buildAll cwd
    let tools = if cfg.DryRun then DryRun.wrapTools rawTools else rawTools
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
    CaBundle.install () |> ignore
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
    let printPrompt =
        argv
        |> Array.tryFindIndex ((=) "--print")
        |> Option.bind (fun i -> if i + 1 < argv.Length then Some argv.[i + 1] else None)
    if argv |> Array.contains "doctor" then
        let cwd = Environment.CurrentDirectory
        let passed = Doctor.run cwd
        if passed then 0 else 1
    elif argv |> Array.contains "man" then
        let manContent = """fugue(1)                    Fugue Manual                    fugue(1)

NAME
       fugue - lean AOT-native CLI coding assistant

SYNOPSIS
       fugue [--profile <name>] [--template <name>] [--low-bandwidth]
             [--offline] [--print "prompt"]
       fugue doctor
       fugue init
       fugue aliases [--install]
       fugue man [--install]

DESCRIPTION
       fugue is a fast, single-binary terminal coding agent for polyglot
       engineers. Cold-starts in ~40ms, uses ~47MB RSS, and works with
       Anthropic, OpenAI-compatible, and Ollama providers.

OPTIONS
       --profile <name>
              Load ~/.fugue/profiles/<name>.md as a system-prompt prefix.

       --template <name>
              Load ~/.fugue/templates/<name>.md as a session template.

       --low-bandwidth
              Skip session summary, cap tool output to 500 lines.

       --offline
              Require a local provider (Ollama). Reject remote API calls.

       --print "prompt"
              Non-interactive: send one prompt, stream to stdout, exit.
              Reads stdin if piped: git diff | fugue --print "review this"

SUBCOMMANDS
       doctor     Run environment diagnostics.
       init       Bootstrap FUGUE.md in the current project directory.
       aliases    Print recommended shell aliases; --install appends to shell rc.
       man        Display this manual; --install places it in man path.

ENVIRONMENT
       ANTHROPIC_API_KEY      Anthropic API key.
       OPENAI_API_KEY         OpenAI API key.
       ANTHROPIC_BASE_URL     Override Anthropic API endpoint.
       OPENAI_BASE_URL        Override OpenAI API endpoint.
       FUGUE_CA_BUNDLE        Path to PEM CA bundle for corporate TLS proxies.
       FUGUE_NO_COLOR         Disable ANSI colour output.
       NO_COLOR               Standard no-colour flag (https://no-color.org).

SLASH COMMANDS (REPL)
       /help       List all commands.
       /new        Start a new session.
       /clear      Clear the screen.
       /rename     Set a human-readable session title.
       /env        Manage session-scoped environment variables.
       /gen        Generate UUID, ULID, or NanoID.
       /cron       Convert natural-language schedule to cron expression.
       /regex      Test a regex pattern against sample input.
       /diff       Show current git diff.
       /summary    Summarise the current session.
       /tools      List available tools.
       /exit       Exit fugue.

FILES
       ~/.fugue/config.json   Main configuration file.
       ~/.fugue/FUGUE.md      Global project context (injected on start).
       ~/.fugue/profiles/     Named system-prompt profiles.
       ~/.fugue/templates/    Session templates.
       ./FUGUE.md             Project-local context (injected on start).
       .fugue/hooks/          Pre/post tool hooks (future).

SEE ALSO
       https://github.com/korat-ai/fugue

"""
        let install = argv |> Array.contains "--install"
        if install then
            let manDir = "/usr/local/share/man/man1"
            let manFile = System.IO.Path.Combine(manDir, "fugue.1")
            try
                System.IO.Directory.CreateDirectory manDir |> ignore
                System.IO.File.WriteAllText(manFile, manContent)
                Console.WriteLine(sprintf "✓ Installed to %s" manFile)
                Console.WriteLine "  Run: man fugue"
                0
            with ex ->
                Console.Error.WriteLine(sprintf "Failed to install man page (try sudo): %s" ex.Message)
                1
        else
            let pager =
                let p = Environment.GetEnvironmentVariable "PAGER" |> Option.ofObj |> Option.defaultValue ""
                if p <> "" then p else "less"
            let tmpFile = System.IO.Path.GetTempFileName() + ".1"
            System.IO.File.WriteAllText(tmpFile, manContent)
            try
                let psi = System.Diagnostics.ProcessStartInfo(pager, tmpFile)
                psi.UseShellExecute <- true
                match System.Diagnostics.Process.Start psi |> Option.ofObj with
                | Some proc -> proc.WaitForExit()
                | None -> Console.Write manContent
            with _ ->
                Console.Write manContent
            try System.IO.File.Delete tmpFile with _ -> ()
            0
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
    elif argv |> Array.contains "version" || argv |> Array.contains "--version" then
        let asm = System.Reflection.Assembly.GetExecutingAssembly()
        let ver =
            let attr = asm.GetCustomAttributes(typeof<System.Reflection.AssemblyInformationalVersionAttribute>, false)
            if attr.Length > 0 then
                (attr.[0] :?> System.Reflection.AssemblyInformationalVersionAttribute).InformationalVersion
            else
                match asm.GetName().Version |> Option.ofObj with
                | Some v -> sprintf "%d.%d.%d" v.Major v.Minor v.Build
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
            Console.WriteLine(sprintf "fugue %s" ver)
            Console.WriteLine(sprintf "  Target RID:    %s" rid)
            Console.WriteLine(sprintf "  .NET runtime:  %s" rtVer)
            Console.WriteLine(sprintf "  Binary:        %s" (if asmPath = "" then "(AOT native)" else asmPath))
            Console.WriteLine(sprintf "  Config file:   %s" cfgPath)
            Console.WriteLine(sprintf "  Profiles dir:  %s" profDir)
            Console.WriteLine(sprintf "  Templates dir: %s" tmplDir)
        else
            Console.WriteLine(sprintf "fugue %s" ver)
        0
    elif argv |> Array.contains "env" then
        let maskKey (v: string) =
            if v.Length <= 8 then String.replicate v.Length "•"
            else v.[..3] + String.replicate (min 16 (v.Length - 8)) "•" + v.[v.Length - 4..]
        let fuguVars = [
            "FUGUE_PROVIDER";   "FUGUE_MODEL";      "FUGUE_CA_BUNDLE"
            "FUGUE_NO_COLOR";   "ANTHROPIC_API_KEY"; "OPENAI_API_KEY"
            "ANTHROPIC_BASE_URL"; "OPENAI_BASE_URL";  "OLLAMA_ENDPOINT"
            "TERM";              "NO_COLOR";           "PAGER" ]
        let header = sprintf "%-30s %-35s %s" "Variable" "Value" "Status"
        Console.WriteLine header
        Console.WriteLine(String.replicate 75 "─")
        let cfg0 = Fugue.Core.Config.load [||]
        for name in fuguVars do
            let v = Environment.GetEnvironmentVariable name |> Option.ofObj
            let isKey = name.EndsWith "_KEY"
            let display =
                match v with
                | None -> "(not set)"
                | Some s -> if isKey then maskKey s else s
            let status =
                match v with
                | None -> "—"
                | Some _ ->
                    match cfg0, name with
                    | Ok c, "FUGUE_PROVIDER" ->
                        let cfgProv = match c.Provider with Anthropic _ -> "anthropic" | OpenAI _ -> "openai" | Ollama _ -> "ollama"
                        sprintf "env (config: %s)" cfgProv
                    | _ -> "env"
            Console.WriteLine(sprintf "%-30s %-35s %s" name display status)
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
    elif argv.Length >= 2 && argv.[0] = "config" then
        let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        let cfgPath = Fugue.Core.Config.configFilePath ()
        let readDoc () =
            if System.IO.File.Exists cfgPath then
                try Some (System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText cfgPath))
                with _ -> None
            else None
        let getVal (doc: System.Text.Json.JsonDocument) (key: string) =
            let parts = key.Split('.')
            let mutable el = doc.RootElement
            let mutable found = true
            for p in parts do
                match el.TryGetProperty p with
                | true, v -> el <- v
                | _ -> found <- false
            if found then Some (el.ToString()) else None
        match argv.[1] with
        | "validate" ->
            if not (System.IO.File.Exists cfgPath) then
                Console.Error.WriteLine(sprintf "No config file at %s" cfgPath); 1
            else
            match Fugue.Core.Config.load [||] with
            | Ok cfg ->
                let prov = match cfg.Provider with
                           | Anthropic _ -> "anthropic" | OpenAI _ -> "openai" | Ollama _ -> "ollama"
                Console.WriteLine(sprintf "✓ config.json is valid")
                Console.WriteLine(sprintf "  provider:  %s" prov)
                Console.WriteLine(sprintf "  model:     %s" (Fugue.Core.Config.modelDisplayName (match cfg.Provider with Anthropic(_,m)|OpenAI(_,m)|Ollama(_,m)->m)))
                Console.WriteLine(sprintf "  path:      %s" cfgPath)
                0
            | Error (Fugue.Core.Config.InvalidConfig reason) ->
                // Parse the JSON ourselves and give detailed hints.
                try
                    use doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText cfgPath)
                    let root = doc.RootElement
                    let mutable errors = []
                    let str (k: string) = match root.TryGetProperty(k) with true, v when v.ValueKind = System.Text.Json.JsonValueKind.String -> v.GetString() |> Option.ofObj | _ -> None
                    match str "provider" with
                    | None -> errors <- errors @ ["[provider] missing — set to \"anthropic\", \"openai\", or \"ollama\""]
                    | Some p when p <> "anthropic" && p <> "openai" && p <> "ollama" ->
                        errors <- errors @ [sprintf "[provider] \"%s\" is not valid — use: anthropic, openai, ollama" p]
                    | _ -> ()
                    match str "model" with
                    | None -> errors <- errors @ ["[model] missing — set to a model ID (e.g. claude-opus-4-7)"]
                    | _ -> ()
                    if errors.IsEmpty then errors <- [reason]
                    Console.Error.WriteLine(sprintf "✗ config.json has %d error(s):" errors.Length)
                    for e in errors do Console.Error.WriteLine(sprintf "  %s" e)
                    1
                with _ ->
                    Console.Error.WriteLine(sprintf "✗ %s" reason); 1
            | Error (Fugue.Core.Config.NoConfigFound _) ->
                Console.Error.WriteLine "✗ No config found"; 1
        | "list" ->
            match readDoc () with
            | None -> Console.Error.WriteLine "No config file found."; 1
            | Some doc ->
                use _ = doc
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(doc.RootElement,
                    System.Text.Json.JsonSerializerOptions(WriteIndented = true)))
                0
        | "get" when argv.Length >= 3 ->
            match readDoc () with
            | None -> Console.Error.WriteLine "No config file found."; 1
            | Some doc ->
                use _ = doc
                match getVal doc argv.[2] with
                | Some v -> Console.WriteLine v; 0
                | None -> Console.Error.WriteLine(sprintf "Key not found: %s" argv.[2]); 1
        | "set" when argv.Length >= 4 ->
            let key   = argv.[2]
            let value = argv.[3]
            let json  =
                if System.IO.File.Exists cfgPath then System.IO.File.ReadAllText cfgPath
                else "{}"
            // AOT-safe set: only supports top-level and one-level-nested keys (dot-separated).
            // Rebuilds the JSON by loading into AppConfig, mutating, and re-saving.
            // For arbitrary key paths, fall back to Config.load → cfg with → saveToFile.
            try
                let jsonVal =
                    match System.Int32.TryParse value with
                    | true, n -> sprintf "%d" n
                    | _ ->
                        match System.Boolean.TryParse value with
                        | true, b -> (if b then "true" else "false")
                        | _ -> sprintf "\"%s\"" (value.Replace("\"", "\\\""))
                // Simple regex-style replacement: if key exists, replace; else append before closing brace.
                let escapedKey = System.Text.RegularExpressions.Regex.Escape key
                let pattern = sprintf "\"(%s)\"\\s*:\\s*(?:\"[^\"]*\"|[^,}\\s]+)" escapedKey
                let newPair = sprintf "\"%s\": %s" key jsonVal
                let newJson =
                    if System.Text.RegularExpressions.Regex.IsMatch(json, pattern) then
                        System.Text.RegularExpressions.Regex.Replace(json, pattern, newPair)
                    else
                        let trimmed = json.TrimEnd()
                        if trimmed.EndsWith '}' then
                            let body = trimmed.[..trimmed.Length - 2].TrimEnd()
                            let sep = if body.TrimEnd().EndsWith ',' || body.TrimEnd() = "{" then "" else ","
                            body + sep + sprintf "\n  %s\n}" newPair
                        else json
                System.IO.File.WriteAllText(cfgPath, newJson)
                Console.WriteLine(sprintf "Set %s = %s" key value)
                0
            with ex ->
                Console.Error.WriteLine(sprintf "Failed to set %s: %s" key ex.Message); 1
        | sub ->
            Console.Error.WriteLine(sprintf "Unknown config subcommand: %s. Use: fugue config list | get <key> | set <key> <value>" sub)
            1
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
        match printPrompt with
        | Some p -> runWithPrint fullCfg p
        | None   -> runWithCfg fullCfg
