module Fugue.Cli.ProgramArgs

// Phase 2a of the AOT/JIT split (#930 → this).  Migrates the simplest
// self-contained subcommands from Program.fs's hand-rolled argv-elif
// chain onto the typed F# DSL in Fugue.Core.CliArgs.
//
// In scope here: `doctor`, `reindex`, `init`, `version`.  These are leaf
// commands without shared top-level options, so they're the lowest-risk
// migration target — and crucially they prove that CliArgs survives AOT
// publish at runtime (closes the validation gap from PR #930: until
// something actually CALLS CliArgs from a [<EntryPoint>], the trimmer
// strips it as dead code and we have no AOT-runtime evidence).
//
// Out of scope (deferred to Phase 2b/2c):
//   - `man`, `aliases`, `env`, `config <verb>` (more surface, --install flags)
//   - The default REPL / `--print` flow with `--profile` / `--template` /
//     `--low-bandwidth` / `--offline` / `--dry-run` / `--mode` (state-heavy)
//   - Custom `--help` text, custom root behaviour
//
// Routing: `Program.main` argv[0] pre-check decides between this DSL path
// and the legacy elif chain.  Until 2c finishes, both paths coexist.

open System
open System.Diagnostics.CodeAnalysis
open Fugue.Core.CliArgs
open Fugue.Core.Config

// --------------------------------------------------------------------------
// `doctor`
// --------------------------------------------------------------------------

let private doctorCmd : Cmd =
    command "doctor" "Run environment diagnostics" [] (fun _ ->
        let cwd = Environment.CurrentDirectory
        if Doctor.run cwd then 0 else 1)

// --------------------------------------------------------------------------
// `reindex`
// --------------------------------------------------------------------------

let private reindexCmd : Cmd =
    command "reindex" "Rebuild FTS5 search index from all session JSONL files" [] (fun _ ->
        match Fugue.Core.SearchIndex.reindexFromJsonl () with
        | Ok n    -> Console.Out.WriteLine $"Reindexed {n} session(s)."; 0
        | Error e -> Console.Error.WriteLine $"Reindex failed: {e}"; 1)

// --------------------------------------------------------------------------
// `init` — bootstrap FUGUE.md in cwd
// --------------------------------------------------------------------------

[<RequiresUnreferencedCode("Calls Directory.GetFiles which may use trimmed reflection paths in some runtimes")>]
let private runInit () : int =
    let cwd = Environment.CurrentDirectory
    let fugueMd = System.IO.Path.Combine(cwd, "FUGUE.md")
    if System.IO.File.Exists fugueMd then
        Console.Error.WriteLine "FUGUE.md already exists. Edit it directly or delete it first."
        1
    else
        let hasFile pat =
            try System.IO.Directory.GetFiles(cwd, pat, System.IO.SearchOption.TopDirectoryOnly).Length > 0
            with _ -> false
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
            $"# Project context\n\n## Stack\n{stack}\n\n## Purpose\n<!-- Describe what this project does -->\n\n## Key constraints\n<!-- Performance, security, platform, team rules -->\n\n## Common tasks\n<!-- What does the AI help you with most often? -->\n"
        System.IO.File.WriteAllText(fugueMd, template)
        Console.WriteLine $"Detected: {stack}"
        Console.WriteLine $"✓ Created FUGUE.md ({System.IO.File.ReadAllBytes(fugueMd).Length} bytes)"
        let gitignore = System.IO.Path.Combine(cwd, ".gitignore")
        if System.IO.File.Exists gitignore then
            let content = System.IO.File.ReadAllText gitignore
            if not (content.Contains "FUGUE.md") then
                System.IO.File.AppendAllText(gitignore, "\nFUGUE.md\n")
                Console.WriteLine "✓ Added FUGUE.md to .gitignore"
        let hooksDir = System.IO.Path.Combine(cwd, ".fugue", "hooks")
        System.IO.Directory.CreateDirectory hooksDir |> ignore
        Console.WriteLine "✓ Created .fugue/hooks/ directory"
        Console.WriteLine "Edit FUGUE.md to add project-specific context, then run: fugue"
        0

let private initCmd : Cmd =
    command "init" "Bootstrap FUGUE.md in the current project directory" [] (fun _ -> runInit ())

// --------------------------------------------------------------------------
// `version` — with `--verbose` showing RID, runtime, paths
// --------------------------------------------------------------------------

let private verboseOpt : Opt<bool> =
    opt<bool> "--verbose" "Print runtime details (RID, .NET version, paths)"
    |> withDefault false

[<RequiresUnreferencedCode("Reads AssemblyInformationalVersionAttribute via reflection")>]
let private runVersion (verbose: bool) : int =
    let asm = System.Reflection.Assembly.GetExecutingAssembly()
    let ver =
        let attr = asm.GetCustomAttributes(typeof<System.Reflection.AssemblyInformationalVersionAttribute>, false)
        if attr.Length > 0 then
            (attr.[0] :?> System.Reflection.AssemblyInformationalVersionAttribute).InformationalVersion
        else
            match asm.GetName().Version |> Option.ofObj with
            | Some v -> $"{v.Major}.{v.Minor}.{v.Build}"
            | None   -> "0.0.0"
    if verbose then
        let rid     = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier
        let rtVer   = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
        let asmPath = System.AppContext.BaseDirectory
        let cfgPath = Fugue.Core.Config.configFilePath ()
        let home    = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        let profDir = System.IO.Path.Combine(home, ".fugue", "profiles")
        let tmplDir = System.IO.Path.Combine(home, ".fugue", "templates")
        Console.WriteLine $"fugue {ver}"
        Console.WriteLine $"  Target RID:    {rid}"
        Console.WriteLine $"  .NET runtime:  {rtVer}"
        let binaryDisplay = if asmPath = "" then "(AOT native)" else asmPath
        Console.WriteLine $"  Binary:        {binaryDisplay}"
        Console.WriteLine $"  Config file:   {cfgPath}"
        Console.WriteLine $"  Profiles dir:  {profDir}"
        Console.WriteLine $"  Templates dir: {tmplDir}"
    else
        Console.WriteLine $"fugue {ver}"
    0

let private versionCmd : Cmd =
    command "version" "Show version (use --verbose for runtime details)" [ optOf verboseOpt ] (fun ctx ->
        runVersion (ctx.Get verboseOpt))

// --------------------------------------------------------------------------
// `man` — manual page (display via PAGER, or install to /usr/local/share/man)
// --------------------------------------------------------------------------

let private manContent = """fugue(1)                    Fugue Manual                    fugue(1)

NAME
       fugue - lean F# CLI coding assistant

SYNOPSIS
       fugue [--profile <name>] [--template <name>] [--low-bandwidth]
             [--offline] [--print "prompt"]
       fugue doctor
       fugue init
       fugue aliases [--install]
       fugue man [--install]

DESCRIPTION
       fugue is a fast, native-AOT terminal coding agent for polyglot
       engineers. Cold-starts in ~10ms warm / 50ms cold cache, uses ~37MB
       peak RSS, and works with Anthropic, OpenAI-compatible, and Ollama
       providers.

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
       version    Show version (--verbose for RID, runtime, paths).
       reindex    Rebuild FTS5 search index from session JSONL files.
       env        Show fugue-relevant environment variables.
       config     Manage ~/.fugue/config.json (validate | list | get | set).

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

let private runMan (install: bool) : int =
    if install then
        let manDir = "/usr/local/share/man/man1"
        let manFile = System.IO.Path.Combine(manDir, "fugue.1")
        try
            System.IO.Directory.CreateDirectory manDir |> ignore
            System.IO.File.WriteAllText(manFile, manContent)
            Console.WriteLine $"✓ Installed to {manFile}"
            Console.WriteLine "  Run: man fugue"
            0
        with ex ->
            Console.Error.WriteLine $"Failed to install man page (try sudo): {ex.Message}"
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
            | None      -> Console.Write manContent
        with _ ->
            Console.Write manContent
        try System.IO.File.Delete tmpFile with _ -> ()
        0

let private manInstallOpt : Opt<bool> =
    opt<bool> "--install" "Install to /usr/local/share/man/man1/fugue.1 (may need sudo)"
    |> withDefault false

let private manCmd : Cmd =
    command "man" "Display the fugue(1) manual page" [ optOf manInstallOpt ] (fun ctx ->
        runMan (ctx.Get manInstallOpt))

// --------------------------------------------------------------------------
// `aliases` — print recommended shell aliases, optional --install
// --------------------------------------------------------------------------

let private aliasLines : string list =
    [ "# Fugue shell aliases"
      "alias fask='fugue --print'"
      "alias fnew='fugue --new-session'"
      "alias fdoctor='fugue doctor'"
      "alias faliases='fugue aliases'" ]

let private runAliases (install: bool) : int =
    if install then
        let zshrc  = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zshrc")
        let bashrc = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bashrc")
        let shellRc = if System.IO.File.Exists zshrc then zshrc else bashrc
        let block = "\n" + (aliasLines |> String.concat "\n") + "\n"
        System.IO.File.AppendAllText(shellRc, block)
        Console.WriteLine $"✓ Appended {aliasLines.Length - 1} aliases to {shellRc}"
        Console.WriteLine $"  Reload with: source {shellRc}"
    else
        for l in aliasLines do Console.WriteLine l
        Console.WriteLine ()
        Console.WriteLine "Run 'fugue aliases --install' to append these to ~/.zshrc automatically."
    0

let private aliasesInstallOpt : Opt<bool> =
    opt<bool> "--install" "Append the aliases to ~/.zshrc (or ~/.bashrc)"
    |> withDefault false

let private aliasesCmd : Cmd =
    command "aliases" "Print recommended shell aliases" [ optOf aliasesInstallOpt ] (fun ctx ->
        runAliases (ctx.Get aliasesInstallOpt))

// --------------------------------------------------------------------------
// `env` — show fugue-relevant environment variables (with key masking)
// --------------------------------------------------------------------------

let private envVarNames : string list =
    [ "FUGUE_PROVIDER";   "FUGUE_MODEL";       "FUGUE_CA_BUNDLE"
      "FUGUE_NO_COLOR";   "ANTHROPIC_API_KEY"; "OPENAI_API_KEY"
      "ANTHROPIC_BASE_URL"; "OPENAI_BASE_URL"; "OLLAMA_ENDPOINT"
      "TERM";              "NO_COLOR";          "PAGER" ]

let private maskKey (v: string) : string =
    if v.Length <= 8 then String.replicate v.Length "•"
    else v.[..3] + String.replicate (min 16 (v.Length - 8)) "•" + v.[v.Length - 4..]

[<RequiresUnreferencedCode("Calls Fugue.Core.Config.load which reads JSON via STJ reflection")>]
[<RequiresDynamicCode("Calls Fugue.Core.Config.load which reads JSON via STJ reflection")>]
let private runEnv () : int =
    let header = String.Format("{0,-30} {1,-35} {2}", "Variable", "Value", "Status")
    Console.WriteLine header
    Console.WriteLine (String.replicate 75 "─")
    let cfg0 = Fugue.Core.Config.load [||]
    for name in envVarNames do
        let v = Environment.GetEnvironmentVariable name |> Option.ofObj
        let isKey = name.EndsWith "_KEY"
        let display =
            match v with
            | None   -> "(not set)"
            | Some s -> if isKey then maskKey s else s
        let status =
            match v with
            | None -> "—"
            | Some _ ->
                match cfg0, name with
                | Ok c, "FUGUE_PROVIDER" ->
                    let cfgProv =
                        match c.Provider with
                        | Anthropic _ -> "anthropic"
                        | OpenAI _    -> "openai"
                        | Ollama _    -> "ollama"
                    $"env (config: {cfgProv})"
                | _ -> "env"
        Console.WriteLine $"{name,-30} {display,-35} {status}"
    0

let private envCmd : Cmd =
    command "env" "Show fugue-relevant environment variables (with API-key masking)" [] (fun _ ->
        runEnv ())

// --------------------------------------------------------------------------
// `config` — manage ~/.fugue/config.json (validate | list | get | set)
// --------------------------------------------------------------------------

let private cfgPath () : string = Fugue.Core.Config.configFilePath ()

let private readDoc () : System.Text.Json.JsonDocument option =
    let p = cfgPath ()
    if System.IO.File.Exists p then
        try Some (System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText p))
        with _ -> None
    else None

let private getValByDottedKey (doc: System.Text.Json.JsonDocument) (key: string) : string option =
    let parts = key.Split('.')
    let mutable el = doc.RootElement
    let mutable found = true
    for p in parts do
        match el.TryGetProperty p with
        | true, v -> el <- v
        | _       -> found <- false
    if found then Some (el.ToString()) else None

[<RequiresUnreferencedCode("Calls Fugue.Core.Config.load which reads JSON via STJ reflection")>]
[<RequiresDynamicCode("Calls Fugue.Core.Config.load which reads JSON via STJ reflection")>]
let private runConfigValidate () : int =
    let p = cfgPath ()
    if not (System.IO.File.Exists p) then
        Console.Error.WriteLine $"No config file at {p}"
        1
    else
        match Fugue.Core.Config.load [||] with
        | Ok cfg ->
            let prov =
                match cfg.Provider with
                | Anthropic _ -> "anthropic"
                | OpenAI _    -> "openai"
                | Ollama _    -> "ollama"
            let model =
                match cfg.Provider with
                | Anthropic(_, m) | OpenAI(_, m) | Ollama(_, m) -> m
            Console.WriteLine "✓ config.json is valid"
            Console.WriteLine $"  provider:  {prov}"
            Console.WriteLine $"  model:     {Fugue.Core.Config.modelDisplayName model}"
            Console.WriteLine $"  path:      {p}"
            0
        | Error (Fugue.Core.Config.InvalidConfig reason) ->
            try
                use doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText p)
                let root = doc.RootElement
                let mutable errors : string list = []
                let str (k: string) =
                    match root.TryGetProperty k with
                    | true, v when v.ValueKind = System.Text.Json.JsonValueKind.String ->
                        v.GetString() |> Option.ofObj
                    | _ -> None
                match str "provider" with
                | None -> errors <- errors @ ["[provider] missing — set to \"anthropic\", \"openai\", or \"ollama\""]
                | Some pp when pp <> "anthropic" && pp <> "openai" && pp <> "ollama" ->
                    errors <- errors @ [$"[provider] \"{pp}\" is not valid — use: anthropic, openai, ollama"]
                | _ -> ()
                match str "model" with
                | None -> errors <- errors @ ["[model] missing — set to a model ID (e.g. claude-opus-4-7)"]
                | _ -> ()
                if errors.IsEmpty then errors <- [reason]
                Console.Error.WriteLine $"✗ config.json has {errors.Length} error(s):"
                for e in errors do Console.Error.WriteLine $"  {e}"
                1
            with _ ->
                Console.Error.WriteLine $"✗ {reason}"
                1
        | Error (Fugue.Core.Config.NoConfigFound _) ->
            Console.Error.WriteLine "✗ No config found"
            1

let private runConfigList () : int =
    match readDoc () with
    | None -> Console.Error.WriteLine "No config file found."; 1
    | Some doc ->
        use _ = doc
        Console.WriteLine (System.Text.Json.JsonSerializer.Serialize(doc.RootElement,
            System.Text.Json.JsonSerializerOptions(WriteIndented = true)))
        0

let private runConfigGet (key: string) : int =
    match readDoc () with
    | None -> Console.Error.WriteLine "No config file found."; 1
    | Some doc ->
        use _ = doc
        match getValByDottedKey doc key with
        | Some v -> Console.WriteLine v; 0
        | None   -> Console.Error.WriteLine $"Key not found: {key}"; 1

let private runConfigSet (key: string) (value: string) : int =
    let p = cfgPath ()
    let json = if System.IO.File.Exists p then System.IO.File.ReadAllText p else "{}"
    try
        let jsonVal =
            match System.Int32.TryParse value with
            | true, n -> $"{n}"
            | _ ->
                match System.Boolean.TryParse value with
                | true, b -> (if b then "true" else "false")
                | _ ->
                    let escaped = value.Replace("\"", "\\\"")
                    $"\"{escaped}\""
        let escapedKey = System.Text.RegularExpressions.Regex.Escape key
        let pattern    = $"\"({escapedKey})\"\\s*:\\s*(?:\"[^\"]*\"|[^,}}\\s]+)"
        let newPair    = $"\"{key}\": {jsonVal}"
        let newJson =
            if System.Text.RegularExpressions.Regex.IsMatch(json, pattern) then
                System.Text.RegularExpressions.Regex.Replace(json, pattern, newPair)
            else
                let trimmed = json.TrimEnd()
                if trimmed.EndsWith '}' then
                    let body = trimmed.[..trimmed.Length - 2].TrimEnd()
                    let sep = if body.TrimEnd().EndsWith ',' || body.TrimEnd() = "{" then "" else ","
                    $"{body}{sep}\n  {newPair}\n}}"
                else json
        System.IO.File.WriteAllText(p, newJson)
        Console.WriteLine $"Set {key} = {value}"
        0
    with ex ->
        Console.Error.WriteLine $"Failed to set {key}: {ex.Message}"
        1

let private configValidateCmd : Cmd =
    command "validate" "Validate ~/.fugue/config.json schema" [] (fun _ -> runConfigValidate ())

let private configListCmd : Cmd =
    command "list" "Print full ~/.fugue/config.json (pretty-printed JSON)" [] (fun _ -> runConfigList ())

let private configGetCmd : Cmd =
    let keyArg = arg<string> "key" "Dotted key path (e.g. \"provider\" or \"ui.theme\")"
    command "get" "Read a value from ~/.fugue/config.json by dotted key" [ argOf keyArg ] (fun ctx ->
        runConfigGet (ctx.Get keyArg))

let private configSetCmd : Cmd =
    let keyArg   = arg<string> "key"   "Top-level config key (e.g. \"model\")"
    let valueArg = arg<string> "value" "Value (auto-typed: int / bool / string)"
    command "set" "Set a value in ~/.fugue/config.json (top-level keys only)"
        [ argOf keyArg; argOf valueArg ]
        (fun ctx ->
            runConfigSet (ctx.Get keyArg) (ctx.Get valueArg))

let private configCmd : Cmd =
    command "config" "Manage ~/.fugue/config.json"
        [ subOf configValidateCmd; subOf configListCmd; subOf configGetCmd; subOf configSetCmd ]
        (fun _ ->
            Console.Error.WriteLine "fugue config: missing subcommand. Use: validate | list | get <key> | set <key> <value>"
            1)

// --------------------------------------------------------------------------
// Top-level options for the default REPL / --print flow (Phase 2c)
//
// These are NOT recursive — they only attach to the root command and apply
// to the no-subcommand default flow.  Putting them on a subcommand (e.g.
// `fugue doctor --low-bandwidth`) is a parse error.  Today's legacy code
// silently ignored that combination; the new behaviour is "explicit reject
// > silent ignore" (intentional improvement, see PR body).
// --------------------------------------------------------------------------

let private profileOpt : Opt<string> =
    opt<string> "--profile" "Load ~/.fugue/profiles/<name>.md as a system-prompt prefix"
    |> withDefault ""

let private templateOpt : Opt<string> =
    opt<string> "--template" "Load ~/.fugue/templates/<name>.md as a session template"
    |> withDefault ""

let private lowBandwidthOpt : Opt<bool> =
    opt<bool> "--low-bandwidth" "Skip session summary, cap tool output to 500 lines"
    |> withDefault false

let private offlineOpt : Opt<bool> =
    opt<bool> "--offline" "Require a local provider (Ollama). Reject remote API calls."
    |> withDefault false

let private dryRunOpt : Opt<bool> =
    opt<bool> "--dry-run" "Mock all mutating tools; don't touch the filesystem or run shell"
    |> withDefault false

let private modeOpt : Opt<string> =
    opt<string> "--mode" "Initial approval mode: plan | default | auto-edit | yolo (Shift+Tab cycles)"
    |> withDefault "default"

let private printOpt : Opt<string> =
    opt<string> "--print" "Non-interactive: send one prompt, stream to stdout, exit"
    |> alias "-p"
    |> withDefault ""

/// Strongly-typed bag of root options, handed to the default-flow callback.
/// Empty-string fields mean "not provided" (DSL default).
type RootOptions = {
    Profile      : string
    Template     : string
    LowBandwidth : bool
    Offline      : bool
    DryRun       : bool
    Mode         : string
    Print        : string
}

// --------------------------------------------------------------------------
// Custom usage text — replaces System.CommandLine's auto-generated --help.
// We bypass SC's help by detecting --help / -h in main() before dispatch.
// --------------------------------------------------------------------------

let usage : string = """Usage: fugue [--profile <name>] [--template <name>] [--low-bandwidth] [--offline]
             [--dry-run] [--mode <m>] [--print "prompt"]
       fugue doctor | init | aliases | man | reindex | version | env | config

Subcommands:
  doctor    Run environment diagnostics
  init      Bootstrap FUGUE.md in current directory
  aliases   Print/install shell aliases
  man       Display full manual page
  reindex   Rebuild FTS5 search index from all session JSONL files
  version   Show version (use --verbose for runtime details)
  env       Show fugue-relevant environment variables (with API-key masking)
  config    Manage ~/.fugue/config.json (validate | list | get <key> | set <key> <value>)

Options:
  --profile <name>    Load ~/.fugue/profiles/<name>.md as system-prompt prefix
  --template <name>   Load ~/.fugue/templates/<name>.md as session template
  --low-bandwidth     Skip session summary, cap tool output to 500 lines
  --offline           Require a local provider (Ollama)
  --dry-run           Mock mutating tools; don't touch filesystem or run shell
  --mode <m>          Initial approval mode: plan, default, auto-edit, yolo
                      (Shift+Tab cycles in REPL)
  --print "prompt"    Non-interactive: send one prompt, stream to stdout, exit
                      Reads stdin if piped: git diff | fugue --print "review this"
  -p "prompt"         Alias for --print
  --version           Print version and exit
  --help, -h          Show this help and exit

Run `fugue man` for the full manual.
"""

let printUsage () : unit = Console.Write usage

/// True if argv contains a help flag — caller should print usage and exit 0
/// without delegating to System.CommandLine.
let isHelp (argv: string[]) : bool =
    argv |> Array.exists (fun a -> a = "--help" || a = "-h")

/// True if argv contains the bare `--version` FLAG (the `version` subcommand
/// is handled by `isMigrated`).  Caller should dispatch to the version path.
let isVersionFlag (argv: string[]) : bool =
    argv |> Array.contains "--version"
    && not (argv |> Array.contains "version")  // 'version' subcommand wins

// --------------------------------------------------------------------------
// Root tree + dispatch
// --------------------------------------------------------------------------

/// argv[0] values that route through the DSL subcommand path (`dispatch`)
/// instead of the default-flow path (`dispatchDefault`).
/// Keep in sync with the subcommands attached to `subcommandRootCmd` below.
let migratedSubcommands : string[] =
    [| "doctor"; "reindex"; "init"; "version"; "man"; "aliases"; "env"; "config" |]

/// Returns true when argv begins with one of the migrated subcommand names —
/// the caller should route through `dispatch` instead of `dispatchDefault`.
let isMigrated (argv: string[]) : bool =
    argv.Length > 0 && Array.contains argv.[0] migratedSubcommands

/// Subcommand-only root: used by `dispatch` for argv that begins with a
/// migrated subcommand.  Top-level options NOT attached here (they only
/// apply to the default flow).
let private subcommandRootCmd : Cmd =
    root "Fugue — terminal coding agent (subcommand path)"
        [ subOf doctorCmd
          subOf reindexCmd
          subOf initCmd
          subOf versionCmd
          subOf manCmd
          subOf aliasesCmd
          subOf envCmd
          subOf configCmd ]
        (fun _ ->
            // Root handler unreachable on the subcommand path — `dispatch`
            // only enters when argv[0] is a migrated subcommand name.
            Console.Error.WriteLine "fugue: internal — ProgramArgs subcommand-root handler reached unexpectedly"
            2)

[<RequiresUnreferencedCode("Routes to subcommand handlers that call STJ-reflection / Doctor.run")>]
[<RequiresDynamicCode("Routes to subcommand handlers that call STJ-reflection / Doctor.run")>]
let dispatch (argv: string[]) : int =
    run argv subcommandRootCmd

/// Default-flow dispatch — for `fugue [options]` / `fugue --print "..."`.
///
/// Builds a root command with ONLY the top-level options attached (no
/// subcommands — those go through `dispatch`).  The handler reads the
/// parsed options into a `RootOptions` record and hands off to the
/// caller-supplied `runDefault` callback.
///
/// This split keeps ProgramArgs free of REPL-specific knowledge —
/// `runDefault` lives in Program.fs (or, after #928, in Fugue.Cli.Aot's
/// own Program.fs which has its own headless-only runDefault).
[<RequiresUnreferencedCode("runDefault callback typically reflects over STJ for config")>]
[<RequiresDynamicCode("runDefault callback typically reflects over STJ for config")>]
let dispatchDefault (argv: string[]) (runDefault: RootOptions -> int) : int =
    let cmd =
        root "Fugue — terminal coding agent (REPL or one-shot)"
            [ optOf profileOpt
              optOf templateOpt
              optOf lowBandwidthOpt
              optOf offlineOpt
              optOf dryRunOpt
              optOf modeOpt
              optOf printOpt ]
            (fun ctx ->
                let opts : RootOptions = {
                    Profile      = ctx.Get profileOpt
                    Template     = ctx.Get templateOpt
                    LowBandwidth = ctx.Get lowBandwidthOpt
                    Offline      = ctx.Get offlineOpt
                    DryRun       = ctx.Get dryRunOpt
                    Mode         = ctx.Get modeOpt
                    Print        = ctx.Get printOpt
                }
                runDefault opts)
    run argv cmd
