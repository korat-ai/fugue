module Fugue.Cli.ReadLine

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

/// Internal mutable line state. Public only because tests construct it directly.
type S = {
    mutable Buffer:        ResizeArray<char>
    mutable Cursor:        int
    mutable LinesRendered: int
    PromptText:            string
    PromptVisLen:          int
    Width:                 int
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
        s.Buffer.Clear()
        s.Cursor <- 0
        s.LinesRendered <- 0
        Wipe
    elif isCtrl k ConsoleKey.D then
        if s.Buffer.Count = 0 then Quit
        else
            // delete char at cursor (bash behaviour)
            if s.Cursor < s.Buffer.Count then
                s.Buffer.RemoveAt s.Cursor
            Continue
    elif isShift k ConsoleKey.Enter then
        s.Buffer.Insert(s.Cursor, '\n')
        s.Cursor <- s.Cursor + 1
        Continue
    elif k.Key = ConsoleKey.Enter then
        Submit (String(s.Buffer.ToArray()))
    elif k.Key = ConsoleKey.Backspace then
        if s.Cursor > 0 then
            s.Buffer.RemoveAt(s.Cursor - 1)
            s.Cursor <- s.Cursor - 1
        Continue
    elif k.Key = ConsoleKey.Delete then
        if s.Cursor < s.Buffer.Count then
            s.Buffer.RemoveAt s.Cursor
        Continue
    elif k.Key = ConsoleKey.LeftArrow then
        if s.Cursor > 0 then s.Cursor <- s.Cursor - 1
        Continue
    elif k.Key = ConsoleKey.RightArrow then
        if s.Cursor < s.Buffer.Count then s.Cursor <- s.Cursor + 1
        Continue
    elif k.Key = ConsoleKey.Home then
        // beginning of current visual line (= last \n before cursor + 1, or 0)
        let mutable i = s.Cursor - 1
        while i >= 0 && s.Buffer.[i] <> '\n' do i <- i - 1
        s.Cursor <- i + 1
        Continue
    elif k.Key = ConsoleKey.End then
        // end of current visual line
        let mutable i = s.Cursor
        while i < s.Buffer.Count && s.Buffer.[i] <> '\n' do i <- i + 1
        s.Cursor <- i
        Continue
    elif not (Char.IsControl k.KeyChar) then
        s.Buffer.Insert(s.Cursor, k.KeyChar)
        s.Cursor <- s.Cursor + 1
        Continue
    else
        Continue

let private writeRaw (s: string) =
    Console.Out.Write s
    Console.Out.Flush()

let private redraw (st: S) =
    // 1. cursor up to start of our previous render, clear from there to screen end
    if st.LinesRendered > 0 then
        if st.LinesRendered > 1 then
            writeRaw (sprintf "\x1b[%dA" (st.LinesRendered - 1))
        writeRaw "\r\x1b[J"
    else
        writeRaw "\r"
    // 2. emit prompt + buffer
    writeRaw st.PromptText
    let bufStr = String(st.Buffer.ToArray())
    writeRaw bufStr
    // 3. count rendered terminal lines (= 1 + number of '\n' in buffer)
    let newlines = bufStr |> Seq.filter (fun c -> c = '\n') |> Seq.length
    st.LinesRendered <- 1 + newlines
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
    // We're currently positioned at end of buffer (last row, last col).
    // Move from there to (row, col): up by (lastRow - row), then to absolute column.
    let lastRow = st.LinesRendered - 1
    let upBy = lastRow - row
    if upBy > 0 then writeRaw (sprintf "\x1b[%dA" upBy)
    writeRaw (sprintf "\r\x1b[%dC" col)

let readAsync (prompt: string) (ct: CancellationToken) : Task<string option> = task {
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
    let st : S =
        { Buffer        = ResizeArray<char>()
          Cursor        = 0
          LinesRendered = 0
          PromptText    = prompt
          PromptVisLen  = visLen
          Width         = max 40 Console.WindowWidth }

    Console.TreatControlCAsInput <- true
    try
        redraw st
        let mutable result : string option voption = ValueNone
        while result.IsNone && not ct.IsCancellationRequested do
            // Console.ReadKey is blocking; we accept that — cancellation in this
            // path is not expected in normal flow (Ctrl+C while reading is a wipe).
            let k = Console.ReadKey(intercept = true)
            match applyKey k st with
            | Continue -> redraw st
            | Wipe     -> redraw st
            | Submit s ->
                writeRaw "\n"   // end the input line cleanly
                result <- ValueSome (Some s)
            | Quit ->
                writeRaw "\n"
                result <- ValueSome None
        return
            match result with
            | ValueSome v -> v
            | ValueNone   -> None
    finally
        Console.TreatControlCAsInput <- false
}
