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
// Root tree + dispatch
// --------------------------------------------------------------------------

/// argv[0] values that route through the DSL instead of legacy elif-chain.
/// Keep in sync with the subcommands attached to `rootCmd` below.
let migratedSubcommands : string[] = [| "doctor"; "reindex"; "init"; "version" |]

/// Returns true when argv begins with one of the migrated subcommand names —
/// the caller should route through `dispatch` instead of legacy parsing.
let isMigrated (argv: string[]) : bool =
    argv.Length > 0 && Array.contains argv.[0] migratedSubcommands

let private rootCmd : Cmd =
    root "Fugue — terminal coding agent for polyglot engineers"
        [ subOf doctorCmd; subOf reindexCmd; subOf initCmd; subOf versionCmd ]
        (fun _ ->
            // Root handler is unreachable in the Phase-2a routing model —
            // we only enter `dispatch` when argv[0] is a migrated subcommand.
            // Defensive return code: exit 2 (misuse) if reached unexpectedly.
            Console.Error.WriteLine "fugue: internal — ProgramArgs root handler reached unexpectedly"
            2)

[<RequiresUnreferencedCode("Routes to subcommand handlers that call STJ-reflection / Doctor.run")>]
[<RequiresDynamicCode("Routes to subcommand handlers that call STJ-reflection / Doctor.run")>]
let dispatch (argv: string[]) : int =
    run argv rootCmd
