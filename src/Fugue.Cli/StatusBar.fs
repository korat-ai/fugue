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
let mutable private compactMode = false
let mutable private branchCache : string * DateTime = "", DateTime.MinValue
let mutable private streamingSince : DateTime option = None
let mutable private spinnerFrame  = 0
let mutable private spinnerTimer  : System.Threading.Timer option = None
let mutable private onTick        : (unit -> unit) = fun () -> ()

let private musicalFrames = [| "♩"; "♪"; "♫"; "♬"; "♭"; "♮"; "♯" |]

// Cadence — rolling window of token arrival timestamps for tok/s display.
let private tokenQueue = System.Collections.Generic.Queue<int64>()
let private tokenWindowSize = 12

let private isSSH =
    let env name = Environment.GetEnvironmentVariable name |> isNull |> not
    env "SSH_CLIENT" || env "SSH_CONNECTION" || env "SSH_TTY"

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
    let friendlyModel (m: string) =
        let i = m.LastIndexOf '/'
        let id = if i >= 0 && i + 1 < m.Length then m.Substring(i + 1) else m
        modelDisplayName id
    match cfg.Provider with
    | Anthropic(_, m) -> "anthropic:" + friendlyModel m
    | OpenAI(_, m)    -> "openai:" + friendlyModel m
    | Ollama(_, m)    -> "ollama:" + friendlyModel m

let internal formatElapsed (t: TimeSpan) : string =
    if t.TotalSeconds < 60.0 then sprintf "%.1fs" t.TotalSeconds
    else sprintf "%dm %ds" (int t.TotalMinutes) t.Seconds

/// Call once per streamed text chunk to update the cadence meter.
let recordToken () =
    let now = Stopwatch.GetTimestamp()
    tokenQueue.Enqueue now
    if tokenQueue.Count > tokenWindowSize then tokenQueue.Dequeue() |> ignore

let private cadenceTokPerSec () : int =
    if tokenQueue.Count < 2 then 0
    else
        let arr = tokenQueue.ToArray()
        let spanSec = float (arr.[arr.Length - 1] - arr.[0]) / float Stopwatch.Frequency
        if spanSec <= 0.0 then 0
        else int (float (arr.Length - 1) / spanSec)

let startStreaming () =
    streamingSince <- Some DateTime.UtcNow
    tokenQueue.Clear()
    spinnerFrame <- 0
    let t = new System.Threading.Timer(
                System.Threading.TimerCallback(fun _ -> onTick ()),
                null, 300, 300)
    spinnerTimer <- Some t

let stopStreaming () =
    streamingSince <- None
    tokenQueue.Clear()
    match spinnerTimer with
    | Some t -> t.Dispose(); spinnerTimer <- None
    | None   -> ()

let refresh () =
    if not active || compactMode || not (Render.isColorEnabled ()) then () else
    let height = Console.WindowHeight
    let strings =
        match cfg with
        | Some c -> Fugue.Core.Localization.pick c.Ui.Locale
        | None   -> Fugue.Core.Localization.en
    let line1 =
        "\x1b[90m" + (homeRel cwd) + " " + strings.StatusBarOn + " \x1b[1m" + (branchOf cwd) + "\x1b[0m"
    let elapsedSuffix, spinnerPrefix, cadenceSuffix =
        match streamingSince with
        | Some since ->
            let elapsed = DateTime.UtcNow - since
            let frame = musicalFrames.[spinnerFrame % musicalFrames.Length]
            spinnerFrame <- spinnerFrame + 1
            let dots = [| "·"; "··"; "···" |].[(spinnerFrame / 2) % 3]
            let bpm = cadenceTokPerSec ()
            let cad = if bpm > 0 then sprintf " · ♩=%d" bpm else ""
            " · " + formatElapsed elapsed, frame + " " + strings.Thinking + dots + " ", cad
        | None -> "", "", ""
    let sshBadge = if isSSH then " · SSH" else ""
    let line2 =
        match cfg with
        | Some c -> "\x1b[90m" + spinnerPrefix + strings.StatusBarApp + " · " + (providerLabel c) + cadenceSuffix + elapsedSuffix + sshBadge + "\x1b[0m"
        | None   -> "\x1b[90m" + spinnerPrefix + strings.StatusBarApp + cadenceSuffix + elapsedSuffix + sshBadge + "\x1b[0m"
    // save cursor, jump to bottom-1, clear two lines, write, restore
    writeRaw "\x1b[s"
    writeRaw ("\x1b[" + string (height - 1) + ";1H")
    writeRaw "\x1b[2K"
    writeRaw line1
    writeRaw ("\x1b[" + string height + ";1H")
    writeRaw "\x1b[2K"
    writeRaw line2
    writeRaw "\x1b[u"

let start (initialCwd: string) (initialCfg: AppConfig) : unit =
    if active || not (Render.isColorEnabled ()) then () else
    cwd <- initialCwd
    cfg <- Some initialCfg
    active <- true
    onTick <- refresh
    let height = Console.WindowHeight
    let width = Console.WindowWidth
    compactMode <- height < 24 || width < 60
    if not compactMode then
        // reserve two bottom lines: emit two newlines, then set scroll region 1..(h-2)
        Console.WriteLine()
        Console.WriteLine()
        writeRaw ("\x1b[1;" + string (height - 2) + "r")
        writeRaw ("\x1b[" + string (height - 2) + ";1H")
        refresh ()

let stop () : unit =
    if not active || not (Render.isColorEnabled ()) then () else
    if not compactMode then
        let height = Console.WindowHeight
        writeRaw "\x1b[r"          // reset scroll region
        writeRaw ("\x1b[" + string (height - 1) + ";1H")
        writeRaw "\x1b[2K"
        writeRaw ("\x1b[" + string height + ";1H")
        writeRaw "\x1b[2K"
        writeRaw "\x1b[u"
    active <- false
    compactMode <- false
