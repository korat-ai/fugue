module Fugue.Cli.Doctor

open System
open System.IO
open System.Diagnostics
open System.Diagnostics.CodeAnalysis
open Spectre.Console
open Fugue.Core.Config

type CheckResult = Pass of string | Info of string | Fail of string

let private checkShell () =
    let shell = Environment.GetEnvironmentVariable "SHELL" |> Option.ofObj |> Option.defaultValue ""
    if shell = "" then Fail "SHELL not set"
    elif File.Exists shell then Pass shell
    else Fail $"{shell} not found"

let private checkGit () =
    let psi = ProcessStartInfo("git", "--version")
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.CreateNoWindow <- true
    try
        match Process.Start psi with
        | null -> Fail "git not found in PATH"
        | proc ->
            use _ = proc
            let v = proc.StandardOutput.ReadToEnd().Trim()
            proc.WaitForExit()
            if proc.ExitCode = 0 then Pass v else Fail "git exited non-zero"
    with _ -> Fail "git not found in PATH"

let private checkCwd (cwd: string) =
    if Directory.Exists cwd then Pass $"{cwd} (readable)"
    else Fail $"directory not found: {cwd}"

let private checkFugueMd (cwd: string) =
    if File.Exists(Path.Combine(cwd, "FUGUE.md")) then Info "found"
    else Info "not found (run /init to create one)"

let private providerSummary (cfg: AppConfig) =
    let home = Environment.GetEnvironmentVariable "HOME" |> Option.ofObj |> Option.defaultValue "~"
    let rawPath =
        Path.Combine(home, ".fugue", "config.json")
    let displayPath = rawPath.Replace(home, "~")
    let providerDesc =
        match cfg.Provider with
        | Anthropic(_, model) ->
            let baseUrl = cfg.BaseUrl |> Option.defaultValue "https://api.anthropic.com"
            $"anthropic model={model} at {baseUrl}"
        | OpenAI(_, model) ->
            let baseUrl = cfg.BaseUrl |> Option.defaultValue "https://api.openai.com"
            $"openai-compat model={model} at {baseUrl}"
        | Ollama(ep, model) ->
            $"ollama model={model} at {ep}"
    $"{displayPath} ({providerDesc})"

[<RequiresUnreferencedCode("Calls Config.load which uses STJ reflection; preserved via TrimmerRootAssembly")>]
[<RequiresDynamicCode("Calls Config.load which uses STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
let private checkConfig () =
    let home = Environment.GetEnvironmentVariable "HOME" |> Option.ofObj |> Option.defaultValue "~"
    let configPath = Path.Combine(home, ".fugue", "config.json")
    let displayPath = configPath.Replace(home, "~")
    match load [||] with
    | Ok cfg ->
        Pass (providerSummary cfg)
    | Error (NoConfigFound _) ->
        Fail $"{displayPath} not found"
    | Error (InvalidConfig reason) ->
        Fail $"invalid: {reason}"

[<RequiresUnreferencedCode("Calls Config.load which uses STJ reflection; preserved via TrimmerRootAssembly")>]
[<RequiresDynamicCode("Calls Config.load which uses STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
let run (cwd: string) =
    Surface.markupLine "[bold]fugue doctor[/]"
    Surface.lineBreak ()

    let checks =
        [ "Config",           checkConfig ()
          "Shell",            checkShell ()
          "Git",              checkGit ()
          "Working directory", checkCwd cwd
          "FUGUE.md",        checkFugueMd cwd ]

    let mutable allPass = true
    for (name, result) in checks do
        match result with
        | Pass msg ->
            Surface.markupLine
                $"[green]✓[/] [bold]{Markup.Escape name}:[/] {Markup.Escape msg}"
        | Info msg ->
            Surface.markupLine
                $"[blue]ℹ[/] [bold]{Markup.Escape name}:[/] {Markup.Escape msg}"
        | Fail msg ->
            Surface.markupLine
                $"[red]✗[/] [bold]{Markup.Escape name}:[/] {Markup.Escape msg}"
            allPass <- false
    allPass
