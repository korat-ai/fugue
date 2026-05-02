module Fugue.Cli.StatusBar

open System
open System.Diagnostics
open System.IO
open Fugue.Core.Localization
open Fugue.Core.Config
open Fugue.Surface

// ---------------------------------------------------------------------------
// Pure render types — used by renderStatusBar (no Console dependency)
// ---------------------------------------------------------------------------

/// Annotation counts for thumbs-up / thumbs-down turn ratings.
type AnnotState = { Up: int; Down: int }

/// Snapshot of the streaming meter — all values needed to render the
/// ThinkingLine without touching any global mutable.
type StreamingSnapshot = {
    /// Wall-clock time when streaming started (for elapsed display).
    Since:           DateTimeOffset
    /// Current spinner frame index (caller advances this).
    SpinnerFrame:    int
    /// Ring-buffer of Stopwatch timestamps (last N token arrivals for tok/s).
    TokensInWindow:  int64 array
}

/// Immutable snapshot of everything the status bar needs to render one frame.
/// Populated by captureState () from the 15 module-level mutables;
/// can also be constructed directly in tests without touching global state.
type StatusBarState = {
    Cwd:            string
    Branch:         string
    Cfg:            AppConfig option
    Strings:        Strings
    ActiveTheme:    string
    Streaming:      StreamingSnapshot option
    LowBandwidth:   bool
    TemplateName:   string option
    ScheduleActive: bool
    Annotations:    AnnotState
    SessionWords:   int
    IsSSH:          bool
    Width:          int
    Height:         int
}

// ---------------------------------------------------------------------------
// Pure render function — StatusBarState -> DrawOp list
// ---------------------------------------------------------------------------

/// Musical frames for the streaming spinner — same set as the mutable code.
let private pureMusicalFrames = [| "♩"; "♪"; "♫"; "♬"; "♭"; "♮"; "♯" |]

/// Compute cadence tok/sec from a token timestamp ring-buffer.
/// Pure: no side effects, no Queue mutation.
let private cadenceFromWindow (arr: int64 array) : int =
    if arr.Length < 2 then 0
    else
        let spanSec = float (arr.[arr.Length - 1] - arr.[0]) / float Stopwatch.Frequency
        if spanSec <= 0.0 then 0
        else int (float (arr.Length - 1) / spanSec)

/// Compute a model's approximate context window size.
/// Exposed as `internal` so existing `contextWindowFor` callers compile unchanged;
/// the module-level `contextWindowFor` will delegate here.
let internal contextWindowForPure (model: string) : int =
    let m = model.ToLowerInvariant()
    if   m.Contains "claude"  then 200_000
    elif m.Contains "gpt-4o"  || m.Contains "gpt4o"  then 128_000
    elif m.Contains "gpt-4"   || m.Contains "gpt4"   then 128_000
    elif m.Contains "gpt-3.5" || m.Contains "gpt35"  then  16_000
    elif m.Contains "qwen"                            then  32_000
    elif m.Contains "llama"                           then   8_000
    elif m.Contains "gemma"                           then   8_000
    elif m.Contains "mistral" || m.Contains "mixtral" then  32_000
    elif m.Contains "phi"                             then   4_000
    else                                                   100_000

/// Pure render: given a StatusBarState produce exactly 3 DrawOps — one per
/// reserved bottom row: ThinkingLine, StatusBarLine1, StatusBarLine2.
/// No Console reads, no side effects, no timer, no Queue mutation.
let renderStatusBar (state: StatusBarState) : DrawOp list =
    let dimStyle  =
        if state.ActiveTheme = "nocturne"
        then { Style.normal with Color = Color.Theme "nocturne-dim" }
        else Style.dim
    let dimStyle2 =
        if state.ActiveTheme = "nocturne"
        then { Style.normal with Color = Color.Theme "nocturne-dim2" }
        else Style.dim

    // Helper: map a simple ANSI-annotated string to a DrawOp.Paint with Style.normal
    // The existing code produces pre-coloured ANSI strings; we keep that approach here
    // so the pure render matches the mutable render's visual output exactly.
    // We use Style.normal because the text already carries its own ANSI sequences.
    let paintRaw (region: Region) (text: string) : DrawOp =
        DrawOp.Paint(region, text, Style.normal)

    let dimEsc  = if state.ActiveTheme = "nocturne" then "\x1b[38;5;24m" else "\x1b[90m"
    let dimEsc2 = if state.ActiveTheme = "nocturne" then "\x1b[38;5;67m" else "\x1b[90m"

    // --- Line 1: cwd + branch ---
    let provLabelFromCfg (c: AppConfig) : string =
        let friendlyModel (m: string) =
            let i = m.LastIndexOf '/'
            let id = if i >= 0 && i + 1 < m.Length then m.Substring(i + 1) else m
            modelDisplayName id
        match c.Provider with
        | Anthropic(_, m) -> "anthropic:" + friendlyModel m
        | OpenAI(_, m)    -> "openai:" + friendlyModel m
        | Ollama(_, m)    -> "ollama:" + friendlyModel m

    let line1Text =
        dimEsc + state.Cwd + " " + state.Strings.StatusBarOn + " \x1b[1m" + state.Branch + "\x1b[0m"

    // --- ThinkingLine + elapsed/cadence for Line 2 ---
    let elapsedSuffix, thinkingLineText, cadenceSuffix =
        match state.Streaming with
        | Some snap ->
            let elapsed  = DateTimeOffset.UtcNow - snap.Since
            let frame    = pureMusicalFrames.[snap.SpinnerFrame % pureMusicalFrames.Length]
            let dots     = [| "·"; "··"; "···" |].[(snap.SpinnerFrame / 2) % 3]
            let bpm      = cadenceFromWindow snap.TokensInWindow
            let cad      = if bpm > 0 then $" · ♩={bpm}" else ""
            let elapsedStr = state.Strings.Thinking  // will be extended with elapsed in format below
            let elapsedFmt =
                let t = elapsed
                if t.TotalSeconds < 60.0 then $"{t.TotalSeconds:F1}s"
                else $"{int t.TotalMinutes}m {t.Seconds}s"
            " · " + elapsedFmt,
            dimEsc + frame + " " + state.Strings.Thinking + dots + " · " + elapsedFmt + cad + "\x1b[0m",
            cad
        | None -> "", "", ""

    // --- SSH badge ---
    let sshBadge = if state.IsSSH then " · SSH" else ""

    // --- Context % badge ---
    let ctxBadge =
        match state.Cfg with
        | Some c ->
            let model  = match c.Provider with Anthropic(_, m) | OpenAI(_, m) | Ollama(_, m) -> m
            let window = contextWindowForPure model
            let estTokens = int (float state.SessionWords / 0.75)
            let pct = int (float estTokens / float window * 100.0)
            if pct <= 0 then ""
            else
                let color =
                    if pct >= 80 then "\x1b[31m"
                    elif pct >= 50 then "\x1b[33m"
                    else "\x1b[32m"
                $" · {color}ctx {pct}%%\x1b[0m{dimEsc2}"
        | None -> ""

    // --- Annotation badge ---
    let annotBadge =
        let u = state.Annotations.Up
        let d = state.Annotations.Down
        if u = 0 && d = 0 then ""
        else $" · \x1b[32m↑{u}\x1b[0m{dimEsc2} \x1b[31m↓{d}\x1b[0m{dimEsc2}"

    // --- Low-bandwidth badge ---
    let lowBwBadge = if state.LowBandwidth then " · \x1b[33mlow-bw\x1b[0m" + dimEsc2 else ""

    // --- Template badge ---
    let tmplBadge =
        state.TemplateName
        |> Option.map (fun n -> $" · \x1b[36mtmpl:{n}\x1b[0m{dimEsc2}")
        |> Option.defaultValue ""

    // --- Line 2 ---
    let line2Text =
        match state.Cfg with
        | Some c ->
            let sched = if state.ScheduleActive then " ⏱" else ""
            dimEsc2 + state.Strings.StatusBarApp + " · " + (provLabelFromCfg c) + sched + cadenceSuffix + elapsedSuffix + ctxBadge + annotBadge + tmplBadge + lowBwBadge + sshBadge + "\x1b[0m"
        | None ->
            dimEsc2 + state.Strings.StatusBarApp + cadenceSuffix + elapsedSuffix + annotBadge + tmplBadge + lowBwBadge + sshBadge + "\x1b[0m"

    // Emit exactly 3 ops: ThinkingLine, StatusBarLine1, StatusBarLine2.
    // When not streaming, ThinkingLine gets EraseLine (no spinner artifact).
    [
        match state.Streaming with
        | None    -> DrawOp.EraseLine Region.ThinkingLine
        | Some _  -> paintRaw Region.ThinkingLine thinkingLineText

        paintRaw Region.StatusBarLine1 line1Text
        paintRaw Region.StatusBarLine2 line2Text
    ]

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
/// Delegates to the pure implementation defined above renderStatusBar.
let internal contextWindowFor (model: string) : int = contextWindowForPure model

/// Call once per streamed text chunk to update the cadence meter.
let recordToken () =
    let now = Stopwatch.GetTimestamp()
    tokenQueue.Enqueue now
    if tokenQueue.Count > tokenWindowSize then tokenQueue.Dequeue() |> ignore

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

/// Read the 15 module-level mutables and build an immutable StatusBarState snapshot.
/// Advances spinnerFrame as a side effect when streaming (mirrors old refresh behaviour).
let private captureState () : StatusBarState =
    let strings =
        match cfg with
        | Some c -> Fugue.Core.Localization.pick c.Ui.Locale
        | None   -> Fugue.Core.Localization.en
    let snap =
        match streamingSince with
        | None -> None
        | Some since ->
            let frame = spinnerFrame
            spinnerFrame <- spinnerFrame + 1   // advance spinner — same as old refresh
            Some {
                Since          = DateTimeOffset(since.ToUniversalTime(), TimeSpan.Zero)
                SpinnerFrame   = frame
                TokensInWindow = tokenQueue.ToArray()
            }
    { Cwd           = homeRel cwd
      Branch        = branchOf cwd
      Cfg           = cfg
      Strings       = strings
      ActiveTheme   = activeTheme
      Streaming     = snap
      LowBandwidth  = lowBandwidthMode
      TemplateName  = templateName
      ScheduleActive = scheduleActive
      Annotations   = { Up = annotUpCount; Down = annotDownCount }
      SessionWords  = sessionWords
      IsSSH         = isSSH
      Width         = Console.WindowWidth
      Height        = Console.WindowHeight }

/// Walk a DrawOp list and emit ANSI escapes directly to Console.Out.
/// Synchronous, no MailboxProcessor — PR B bridge.  PR C replaces this
/// with agent.Post(SurfaceMessage.Execute ops).
let private applyOpsDirectly (ops: DrawOp list) : unit =
    let h = Console.WindowHeight
    // Translate Region to 1-based ANSI row (same mapping as RealExecutor.ansiRow).
    let ansiRow (region: Region) : int option =
        match region with
        | Region.ThinkingLine   -> Some (h - 2)
        | Region.StatusBarLine1 -> Some (h - 1)
        | Region.StatusBarLine2 -> Some h
        | Region.InputArea r    -> Some (max 1 (h - 3 - r))
        | Region.ScrollContent  -> None
        | Region.Modal          -> Some 1
    let rec applyOne (op: DrawOp) =
        match op with
        | DrawOp.Paint(region, text, _style) ->
            match ansiRow region with
            | Some row -> writeRaw $"\x1b[{row};1H\x1b[2K{text}"
            | None     -> writeRaw text
        | DrawOp.EraseLine region ->
            match ansiRow region with
            | Some row -> writeRaw $"\x1b[{row};1H\x1b[2K"
            | None     -> writeRaw "\x1b[2K"
        | DrawOp.MoveTo(region, col) ->
            let col1 = col + 1
            match ansiRow region with
            | Some row -> writeRaw $"\x1b[{row};{col1}H"
            | None     -> writeRaw $"\x1b[{col1}G"
        | DrawOp.SaveAndRestore inner ->
            writeRaw "\x1b[s"
            for innerOp in inner do applyOne innerOp
            writeRaw "\x1b[u"
        | DrawOp.Append text ->
            writeRaw text
            writeRaw "\r\n"
        | DrawOp.ResetScrollRegion ->
            let w = Console.WindowWidth
            writeRaw $"\x1b[1;{h}r\x1b[{h};{w}H"
    writeRaw "\x1b[s"
    for op in ops do applyOne op
    writeRaw "\x1b[u"

let refresh () =
    if not active || compactMode || not (Render.isColorEnabled ()) then () else
    let state = captureState ()
    let ops   = renderStatusBar state
    applyOpsDirectly ops

/// Update the config the status bar reads from. Use after `/model set` and
/// other config-mutating commands. `start` early-returns once the bar is
/// active, so it cannot be reused for re-init — this setter exists to keep
/// the rendered model name / provider in sync after a rebuild.
let setCfg (newCfg: AppConfig) : unit =
    cfg <- Some newCfg
    if active then refresh ()

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
