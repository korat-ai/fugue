module Fugue.Cli.Aot.Program

// `fugue-aot` — headless Native AOT distribution.
//
// Scope (Phase 3.1, this commit):
//   - `--version` / `version` — print fugue-aot version
//   - `--help`                — print usage
//   - default flow            — stub message, exit 2 (Phase 3.2 adds --print)
//
// Out of scope here (lands in 3.2+):
//   - `--print <prompt>` headless agent driver  (extracts Repl.runPrint into a
//     shared place that doesn't depend on Fugue.Cli)
//   - stdin pipe consumption (when `!Console.IsInputRedirected = false`)
//   - `doctor`, `config validate` (Doctor lives in Fugue.Cli today; needs a
//     plain-Console variant before it can move here)
//
// Architectural intent: this binary references only Fugue.Core +
// Fugue.Agent + Fugue.Tools.  No Surface, no Spectre, no Markdig.  The
// trimmer therefore has zero entry points into REPL code, which gives a
// real binary-size delta vs the JIT-bound `fugue` (currently ~46 MB).

open System
open Fugue.Core.CliArgs

let private versionString =
    let asm = System.Reflection.Assembly.GetExecutingAssembly()
    let info =
        asm.GetCustomAttributes(typeof<System.Reflection.AssemblyInformationalVersionAttribute>, false)
        |> Array.tryHead
        |> Option.map (fun a -> (a :?> System.Reflection.AssemblyInformationalVersionAttribute).InformationalVersion)
    info |> Option.defaultWith (fun () ->
        match asm.GetName().Version with
        | null -> "0.0.0"
        | v -> v.ToString())

let private printVersion () =
    Console.Out.WriteLine $"fugue-aot {versionString}"
    0

let private printUsage () =
    let lines = [
        "fugue-aot — headless coding agent (Native AOT)"
        ""
        "Usage:"
        "  fugue-aot [--version] [--help] <command> [args...]"
        ""
        "Commands:"
        "  version                Print version and exit"
        ""
        "Flags:"
        "  --version              Print version and exit"
        "  --help, -h             Show this help"
        ""
        "Headless mode (Phase 3.2, not yet implemented):"
        "  fugue-aot --print \"prompt\"     One-shot agent run, plain-text output"
        "  cat input | fugue-aot           Pipe stdin as the prompt"
        ""
        "For the interactive REPL, use `fugue` (JIT distribution)."
    ]
    for l in lines do Console.Out.WriteLine l
    0

let private versionCmd : Cmd =
    command "version" "Print version and exit" [] (fun _ -> printVersion ())

let private rootCmd : Cmd =
    root "fugue-aot — headless coding agent (Native AOT)"
        [ subOf versionCmd ]
        (fun _ ->
            // No subcommand matched (System.CommandLine 2.0 invokes the
            // root handler when bare).  Show usage and exit 2 for scripts.
            printUsage () |> ignore
            2)

let private isHelp (argv: string[]) : bool =
    argv |> Array.exists (fun a -> a = "--help" || a = "-h")

let private isVersionFlag (argv: string[]) : bool =
    argv |> Array.exists (fun a -> a = "--version")

[<EntryPoint>]
let main argv =
    if argv.Length = 0 then
        // Phase 3.1: no headless agent driver yet.  Exit 2 so scripts can
        // tell "stub" from "ran but produced empty output".
        Console.Error.WriteLine "fugue-aot: no command given. Try `fugue-aot --help`."
        Console.Error.WriteLine "headless agent driver (`--print`, stdin pipe) lands in Phase 3.2."
        2
    elif isHelp argv then
        printUsage ()
    elif isVersionFlag argv then
        printVersion ()
    else
        run argv rootCmd
