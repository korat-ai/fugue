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
let mutable private activeTheme = ""

let initTheme (theme: string) = activeTheme <- theme
let mutable private compactMode = false
let mutable private branchCache : string * DateTime = "", DateTime.MinValue
let mutable private streamingSince : DateTime option = None
let mutable private spinnerFrame  = 0
let mutable private sessionWords  = 0   // cumulative word count for ctx% estimation
let mutable private annotUpCount   = 0
let mutable private annotDownCount = 0
let mutable private lowBandwidthMode = false
let mutable private templateName : string option = None
let mutable private scheduleActive = false
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
    if t.TotalSeconds < 60.0 then $"{t.TotalSeconds:F1}s"
    else $"{int t.TotalMinutes}m {t.Seconds}s"

/// Accumulate words from a user input + assistant response pair.
let recordWords (n: int) = sessionWords <- sessionWords + n

/// Reset session word counter (e.g. on /new).
let resetWords () = sessionWords <- 0

let setLowBandwidth (v: bool) = lowBandwidthMode <- v
let setTemplateName (n: string option) = templateName <- n
let setScheduleActive (v: bool) = scheduleActive <- v

let recordAnnotation (rating: Fugue.Core.Annotation.Rating) =
    match rating with
    | Fugue.Core.Annotation.Up   -> annotUpCount   <- annotUpCount   + 1
    | Fugue.Core.Annotation.Down -> annotDownCount <- annotDownCount + 1

let resetAnnotations () =
    annotUpCount   <- 0
    annotDownCount <- 0

/// Approximate context window (tokens) for a given model id.
let internal contextWindowFor (model: string) : int =
    let m = model.ToLowerInvariant()
    if   m.Contains "claude"  then 200_000
    elif m.Contains "gpt-4o"  || m.Contains "gpt4o"  then 128_000
    elif m.Contains "gpt-4"   || m.Contains "gpt4"   then 128_000
    elif m.Contains "gpt-3.5" || m.Contains "gpt35"  then 16_000
    elif m.Contains "qwen"                            then  32_000
    elif m.Contains "llama"                           then   8_000
    elif m.Contains "gemma"                           then   8_000
    elif m.Contains "mistral" || m.Contains "mixtral" then  32_000
    elif m.Contains "phi"                             then   4_000
    else                                                   100_000

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
    let dimEsc = if activeTheme = "nocturne" then "\x1b[38;5;24m" else "\x1b[90m"
    let line1 =
        dimEsc + (homeRel cwd) + " " + strings.StatusBarOn + " \x1b[1m" + (branchOf cwd) + "\x1b[0m"
    let elapsedSuffix, thinkingLine, cadenceSuffix =
        match streamingSince with
        | Some since ->
            let elapsed = DateTime.UtcNow - since
            let frame = musicalFrames.[spinnerFrame % musicalFrames.Length]
            spinnerFrame <- spinnerFrame + 1
            let dots = [| "·"; "··"; "···" |].[(spinnerFrame / 2) % 3]
            let bpm = cadenceTokPerSec ()
            let cad = if bpm > 0 then $" · ♩={bpm}" else ""
            " · " + formatElapsed elapsed,
            dimEsc + frame + " " + strings.Thinking + dots + " · " + formatElapsed elapsed + cad + "\x1b[0m",
            cad
        | None -> "", "", ""
    let sshBadge = if isSSH then " · SSH" else ""
    let dimEsc2 = if activeTheme = "nocturne" then "\x1b[38;5;67m" else "\x1b[90m"
    let ctxBadge =
        match cfg with
        | Some c ->
            let model = match c.Provider with Anthropic(_, m) | OpenAI(_, m) | Ollama(_, m) -> m
            let window = contextWindowFor model
            let estTokens = int (float sessionWords / 0.75)
            let pct = int (float estTokens / float window * 100.0)
            if pct <= 0 then ""
            else
                let color =
                    if pct >= 80 then "\x1b[31m"      // red
                    elif pct >= 50 then "\x1b[33m"    // yellow
                    else "\x1b[32m"                   // green
                $" · {color}ctx {pct}%%\x1b[0m{dimEsc2}"
        | None -> ""
    let annotBadge =
        if annotUpCount = 0 && annotDownCount = 0 then ""
        else $" · \x1b[32m↑{annotUpCount}\x1b[0m{dimEsc2} \x1b[31m↓{annotDownCount}\x1b[0m{dimEsc2}"
    let lowBwBadge = if lowBandwidthMode then " · \x1b[33mlow-bw\x1b[0m" + dimEsc2 else ""
    let tmplBadge = templateName |> Option.map (fun n -> $" · \x1b[36mtmpl:{n}\x1b[0m{dimEsc2}") |> Option.defaultValue ""
    let line2 =
        match cfg with
        | Some c ->
            let sched = if scheduleActive then " ⏱" else ""
            dimEsc2 + strings.StatusBarApp + " · " + (providerLabel c) + sched + cadenceSuffix + elapsedSuffix + ctxBadge + annotBadge + tmplBadge + lowBwBadge + sshBadge + "\x1b[0m"
        | None   -> dimEsc2 + strings.StatusBarApp + cadenceSuffix + elapsedSuffix + annotBadge + tmplBadge + lowBwBadge + sshBadge + "\x1b[0m"
    // save cursor, paint 3 reserved bottom lines: thinking@h-2, status1@h-1, status2@h
    writeRaw "\x1b[s"
    writeRaw ("\x1b[" + string (height - 2) + ";1H")
    writeRaw "\x1b[2K"
    writeRaw thinkingLine
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
        // reserve three bottom lines: thinking indicator + 2 status lines.
        // Scroll region becomes 1..(h-3); cursor parks at the last scrollable line.
        Console.WriteLine()
        Console.WriteLine()
        Console.WriteLine()
        writeRaw ("\x1b[1;" + string (height - 3) + "r")
        writeRaw ("\x1b[" + string (height - 3) + ";1H")
        refresh ()

let stop () : unit =
    if not active || not (Render.isColorEnabled ()) then () else
    if not compactMode then
        let height = Console.WindowHeight
        writeRaw "\x1b[r"          // reset scroll region
        writeRaw ("\x1b[" + string (height - 2) + ";1H")
        writeRaw "\x1b[2K"
        writeRaw ("\x1b[" + string (height - 1) + ";1H")
        writeRaw "\x1b[2K"
        writeRaw ("\x1b[" + string height + ";1H")
        writeRaw "\x1b[2K"
        writeRaw "\x1b[u"
    active <- false
    compactMode <- false
