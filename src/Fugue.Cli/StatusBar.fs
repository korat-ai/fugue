module Fugue.Cli.StatusBar

open System
open System.Diagnostics
open System.IO
open Fugue.Core.Localization
open Fugue.Core.Config

let private writeRaw (s: string) =
    Console.Out.Write s
    Console.Out.Flush()

let mutable private cwd: string = "."
let mutable private cfg: AppConfig option = None
let mutable private active = false
let mutable private branchCache : string * DateTime = "", DateTime.MinValue

let private branchOf (cwd: string) : string =
    let now = DateTime.UtcNow
    let cached, cachedAt = branchCache
    if (now - cachedAt).TotalSeconds < 2.0 then cached
    else
        try
            let psi = ProcessStartInfo("git", "rev-parse --abbrev-ref HEAD")
            psi.WorkingDirectory <- cwd
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            match Process.Start psi with
            | null ->
                branchCache <- "(no git)", now
                "(no git)"
            | p ->
                use _ = p
                p.WaitForExit 500 |> ignore
                let out = (p.StandardOutput.ReadToEnd()).Trim()
                let result = if String.IsNullOrEmpty out then "(no git)" else out
                branchCache <- result, now
                result
        with _ ->
            branchCache <- "(no git)", now
            "(no git)"

let private homeRel (path: string) : string =
    let home = Environment.GetEnvironmentVariable "HOME" |> Option.ofObj |> Option.defaultValue ""
    if home <> "" && path.StartsWith home then "~" + path.Substring(home.Length)
    else path

let private providerLabel (cfg: AppConfig) : string =
    match cfg.Provider with
    | Anthropic(_, m) -> sprintf "anthropic:%s" m
    | OpenAI(_, m)    -> sprintf "openai:%s" m
    | Ollama(_, m)    -> sprintf "ollama:%s" m

let refresh () =
    if not active then () else
    let height = Console.WindowHeight
    let strings =
        match cfg with
        | Some c -> Fugue.Core.Localization.pick c.Ui.Locale
        | None   -> Fugue.Core.Localization.en
    let line1 =
        sprintf "\x1b[90m%s %s \x1b[1m%s\x1b[0m"
            (homeRel cwd) strings.StatusBarOn (branchOf cwd)
    let line2 =
        match cfg with
        | Some c -> sprintf "\x1b[90m%s · %s\x1b[0m" strings.StatusBarApp (providerLabel c)
        | None   -> sprintf "\x1b[90m%s\x1b[0m" strings.StatusBarApp
    // save cursor, jump to bottom-1, clear two lines, write, restore
    writeRaw "\x1b[s"
    writeRaw (sprintf "\x1b[%d;1H" (height - 1))
    writeRaw "\x1b[2K"
    writeRaw line1
    writeRaw (sprintf "\x1b[%d;1H" height)
    writeRaw "\x1b[2K"
    writeRaw line2
    writeRaw "\x1b[u"

let start (initialCwd: string) (initialCfg: AppConfig) : unit =
    if active then () else
    cwd <- initialCwd
    cfg <- Some initialCfg
    active <- true
    // reserve two bottom lines: emit two newlines, then set scroll region 1..(h-2)
    let height = Console.WindowHeight
    Console.WriteLine()
    Console.WriteLine()
    writeRaw (sprintf "\x1b[1;%dr" (height - 2))
    writeRaw (sprintf "\x1b[%d;1H" (height - 2))
    refresh ()

let stop () : unit =
    if not active then () else
    let height = Console.WindowHeight
    writeRaw "\x1b[r"          // reset scroll region
    writeRaw (sprintf "\x1b[%d;1H" (height - 1))
    writeRaw "\x1b[2K"
    writeRaw (sprintf "\x1b[%d;1H" height)
    writeRaw "\x1b[2K"
    writeRaw "\x1b[u"
    active <- false
