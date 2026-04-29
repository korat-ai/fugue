module Fugue.Cli.ReadLine

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Fugue.Core.Localization

// Hardcoded — small list, easier than passing through state.
let slashCommands : string list = [ "/help"; "/clear"; "/exit" ]

/// Internal mutable line state. Public only because tests construct it directly.
type S = {
    mutable Buffer:        ResizeArray<char>
    mutable Cursor:        int
    mutable LinesRendered: int
    mutable ExitArmed:     bool
    PromptText:            string
    PromptVisLen:          int
    HintWhenArmed:         string
    Width:                 int
    SlashHelp:             (string * string) list  // (name, dim description) pairs
}

/// Action returned by the pure key-handler. The caller (read-loop) decides what to do.
type Action =
    | Continue
    | Submit of string
    | Quit
    | Wipe

let private isCtrl (k: ConsoleKeyInfo) (key: ConsoleKey) : bool =
    k.Modifiers.HasFlag ConsoleModifiers.Control && k.Key = key

let private isShift (k: ConsoleKeyInfo) (key: ConsoleKey) : bool =
    k.Modifiers.HasFlag ConsoleModifiers.Shift && k.Key = key

/// Pure key dispatch. Mutates `s`; returns Action telling the loop what's next.
let applyKey (k: ConsoleKeyInfo) (s: S) : Action =
    if isCtrl k ConsoleKey.C then
        if s.Buffer.Count = 0 && s.ExitArmed then
            Quit
        elif s.Buffer.Count = 0 then
            s.ExitArmed <- true
            Wipe
        else
            s.Buffer.Clear()
            s.Cursor <- 0
            s.ExitArmed <- false
            Wipe
    elif isCtrl k ConsoleKey.D then
        if s.Buffer.Count = 0 then Quit
        else
            s.ExitArmed <- false
            // delete char at cursor (bash behaviour)
            if s.Cursor < s.Buffer.Count then
                s.Buffer.RemoveAt s.Cursor
            Continue
    elif isShift k ConsoleKey.Enter then
        s.ExitArmed <- false
        s.Buffer.Insert(s.Cursor, '\n')
        s.Cursor <- s.Cursor + 1
        Continue
    elif k.Key = ConsoleKey.Enter then
        s.ExitArmed <- false
        Submit (String(s.Buffer.ToArray()))
    elif k.Key = ConsoleKey.Backspace then
        s.ExitArmed <- false
        if s.Cursor > 0 then
            s.Buffer.RemoveAt(s.Cursor - 1)
            s.Cursor <- s.Cursor - 1
        Continue
    elif k.Key = ConsoleKey.Delete then
        s.ExitArmed <- false
        if s.Cursor < s.Buffer.Count then
            s.Buffer.RemoveAt s.Cursor
        Continue
    elif k.Key = ConsoleKey.LeftArrow then
        s.ExitArmed <- false
        if s.Cursor > 0 then s.Cursor <- s.Cursor - 1
        Continue
    elif k.Key = ConsoleKey.RightArrow then
        s.ExitArmed <- false
        if s.Cursor < s.Buffer.Count then s.Cursor <- s.Cursor + 1
        Continue
    elif k.Key = ConsoleKey.Home then
        s.ExitArmed <- false
        // beginning of current visual line (= last \n before cursor + 1, or 0)
        let mutable i = s.Cursor - 1
        while i >= 0 && s.Buffer.[i] <> '\n' do i <- i - 1
        s.Cursor <- i + 1
        Continue
    elif k.Key = ConsoleKey.End then
        s.ExitArmed <- false
        // end of current visual line
        let mutable i = s.Cursor
        while i < s.Buffer.Count && s.Buffer.[i] <> '\n' do i <- i + 1
        s.Cursor <- i
        Continue
    elif not (Char.IsControl k.KeyChar) then
        s.ExitArmed <- false
        s.Buffer.Insert(s.Cursor, k.KeyChar)
        s.Cursor <- s.Cursor + 1
        Continue
    else
        Continue

let private writeRaw (s: string) =
    Console.Out.Write s
    Console.Out.Flush()

/// Clear `count` rows starting at the cursor's current row and moving downward.
/// Cursor is left at column 0 of the top row of the cleared area.
/// Uses per-line \x1b[K so it does NOT cross the scroll region (status bar stays).
let private eraseLines (count: int) : unit =
    if count > 0 then
        // Move up to top of the rendered area.
        if count > 1 then writeRaw ("\x1b[" + string (count - 1) + "A")
        writeRaw "\r"
        // Clear each rendered line, walking down.
        for i in 0 .. count - 1 do
            writeRaw "\x1b[K"
            if i < count - 1 then writeRaw "\x1b[B"
        // Walk back up to the top so caller can re-render from there.
        if count > 1 then writeRaw ("\x1b[" + string (count - 1) + "A")
        writeRaw "\r"

let private redraw (st: S) =
    // 1. cursor up to start of our previous render, clear per-line (not full screen end)
    eraseLines st.LinesRendered
    // 2. emit prompt + buffer
    if st.LinesRendered = 0 then writeRaw "\r"
    writeRaw st.PromptText
    let bufStr = String(st.Buffer.ToArray())
    writeRaw bufStr
    // 3. count rendered terminal lines (= 1 + number of '\n' in buffer)
    let newlines = bufStr |> Seq.filter (fun c -> c = '\n') |> Seq.length
    st.LinesRendered <- 1 + newlines
    // 3b. if exit is armed, emit dim hint on the line below the buffer;
    //     elif buffer starts with '/', emit matching slash command suggestions.
    //     The two branches are mutually exclusive.
    if st.ExitArmed && st.HintWhenArmed <> "" then
        writeRaw ("\n\x1b[2m" + st.HintWhenArmed + "\x1b[0m")
        st.LinesRendered <- st.LinesRendered + 1
    elif st.Buffer.Count > 0 && st.Buffer.[0] = '/' then
        let prefix = String(st.Buffer.ToArray())
        let matches = st.SlashHelp |> List.filter (fun (name, _) -> name.StartsWith(prefix))
        for (name, desc) in matches do
            writeRaw ("\n\x1b[2m  " + name + "  " + desc + "\x1b[0m")
            st.LinesRendered <- st.LinesRendered + 1
    // 4. position cursor at st.Cursor inside buffer.
    //    Compute (row, col) from start of prompt.
    let mutable row = 0
    let mutable col = st.PromptVisLen
    for i in 0 .. st.Cursor - 1 do
        if st.Buffer.[i] = '\n' then
            row <- row + 1
            col <- 0
        else
            col <- col + 1
    // We're currently positioned at end of buffer (last row of buffer, or hint line if armed).
    // Move from there to (row, col): up by (lastRow - row), then to absolute column.
    // lastRow is the last buffer row (LinesRendered - 1, or LinesRendered - 2 if hint shown).
    let lastRow = st.LinesRendered - 1
    let upBy = lastRow - row
    if upBy > 0 then writeRaw ("\x1b[" + string upBy + "A")
    if col > 0 then writeRaw ("\r\x1b[" + string col + "C")
    else writeRaw "\r"

let readAsync (prompt: string) (strings: Strings) (ct: CancellationToken) : Task<string option> = task {
    // Strip ANSI escapes for visible-length calc (rough — assumes only \x1b[...m sequences).
    let visLen =
        let mutable n = 0
        let mutable i = 0
        while i < prompt.Length do
            if prompt.[i] = '\x1b' then
                while i < prompt.Length && prompt.[i] <> 'm' do i <- i + 1
                if i < prompt.Length then i <- i + 1
            else
                n <- n + 1
                i <- i + 1
        n
    let slashHelp =
        [ "/help",  strings.CmdHelpDesc
          "/clear", strings.CmdClearDesc
          "/exit",  strings.CmdExitDesc ]
    let st : S =
        { Buffer        = ResizeArray<char>()
          Cursor        = 0
          LinesRendered = 0
          ExitArmed     = false
          PromptText    = prompt
          PromptVisLen  = visLen
          HintWhenArmed = strings.ExitHint
          Width         = max 40 Console.WindowWidth
          SlashHelp     = slashHelp }

    Console.TreatControlCAsInput <- true
    try
        redraw st
        let mutable result : string option voption = ValueNone
        while result.IsNone && not ct.IsCancellationRequested do
            // Console.ReadKey is blocking; we accept that — cancellation in this
            // path is not expected in normal flow (Ctrl+C while reading is a wipe).
            let k = Console.ReadKey(intercept = true)
            let eraseRendered () = eraseLines st.LinesRendered
            match applyKey k st with
            | Continue -> redraw st
            | Wipe     -> redraw st
            | Submit s ->
                eraseRendered ()
                result <- ValueSome (Some s)
            | Quit ->
                eraseRendered ()
                writeRaw "\n"
                result <- ValueSome None
        return
            match result with
            | ValueSome v -> v
            | ValueNone   -> None
    finally
        Console.TreatControlCAsInput <- false
}
