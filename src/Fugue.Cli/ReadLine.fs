module Fugue.Cli.ReadLine

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Fugue.Core.Localization

module private InputSanitize =

    let private zeroWidthCodePoints =
        [| 0x200B; 0x200C; 0x200D; 0xFEFF; 0x2060; 0x00AD |]

    /// True if the string contains any zero-width character.
    let hasZeroWidth (s: string) =
        zeroWidthCodePoints |> Array.exists (fun cp -> s.IndexOf(char cp) >= 0)

    /// Normalize a pasted/submitted string:
    /// CRLF → LF, standalone CR → LF, Unicode paragraph/line separators → LF.
    let normalize (s: string) : string =
        s.Replace("\r\n", "\n")
         .Replace("\r", "\n")
         .Replace(" ", "\n")   // LINE SEPARATOR
         .Replace(" ", "\n")   // PARAGRAPH SEPARATOR

/// True if the string contains any zero-width character (U+200B/C/D, U+FEFF, U+2060, U+00AD).
let hasZeroWidth (s: string) = InputSanitize.hasZeroWidth s
/// Normalize pasted input (CRLF→LF, standalone CR→LF, U+2028/2029→LF). For testing.
let normalizeInput (s: string) = InputSanitize.normalize s

// Hardcoded — small list, easier than passing through state.
let slashCommands : string list = [ "/help"; "/clear"; "/init"; "/exit"; "/diff"; "/diff --staged" ]

/// Session history (REPL is single-threaded). Not private so tests can seed entries directly.
let historyStore = ResizeArray<string>()

/// For test isolation only.
let clearHistory () = historyStore.Clear()

/// Undo/redo stacks — module-level, REPL is single-threaded.
/// Not private so tests can inspect and seed them directly.
let undoStack = ResizeArray<ResizeArray<char> * int>()
let redoStack = ResizeArray<ResizeArray<char> * int>()

/// For test isolation only.
let clearUndoRedo () =
    undoStack.Clear()
    redoStack.Clear()

/// Push item onto stack, evicting the oldest entry when the cap is exceeded.
let private pushBounded (stack: ResizeArray<_>) item =
    stack.Add item
    if stack.Count > 100 then stack.RemoveAt 0

/// Internal mutable line state. Public only because tests construct it directly.
type S = {
    mutable Buffer:          ResizeArray<char>
    mutable Cursor:          int
    mutable LinesRendered:   int
    mutable ExitArmed:       bool
    mutable RowsBelowCursor: int   // # of rows from cursor's final position down to last rendered row
    mutable HistoryIdx:      int                        // -1 = not browsing; 0..n-1 = browsing
    mutable SavedBuffer:     ResizeArray<char> option   // snapshot before first Up press
    PromptText:              string
    PromptVisLen:            int
    HintWhenArmed:           string
    Width:                   int
    SlashHelp:               (string * string) list  // (name, dim description) pairs
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

let private prevWordStart (buf: ResizeArray<char>) (pos: int) : int =
    let mutable i = pos - 1
    while i >= 0 && (buf.[i] = ' ' || buf.[i] = '\t') do i <- i - 1
    while i >= 0 && buf.[i] <> ' ' && buf.[i] <> '\t' do i <- i - 1
    i + 1

let private nextWordEnd (buf: ResizeArray<char>) (pos: int) : int =
    let mutable i = pos
    while i < buf.Count && (buf.[i] = ' ' || buf.[i] = '\t') do i <- i + 1
    while i < buf.Count && buf.[i] <> ' ' && buf.[i] <> '\t' do i <- i + 1
    i

/// Pure key dispatch. Mutates `s`; returns Action telling the loop what's next.
let applyKey (k: ConsoleKeyInfo) (s: S) : Action =
    let exitHistoryMode () =
        s.HistoryIdx <- -1
        s.SavedBuffer <- None

    if isCtrl k ConsoleKey.C then
        exitHistoryMode ()
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
            exitHistoryMode ()
            s.ExitArmed <- false
            // delete char at cursor (bash behaviour)
            if s.Cursor < s.Buffer.Count then
                s.Buffer.RemoveAt s.Cursor
            Continue
    elif isShift k ConsoleKey.Enter then
        exitHistoryMode ()
        s.ExitArmed <- false
        s.Buffer.Insert(s.Cursor, '\n')
        s.Cursor <- s.Cursor + 1
        Continue
    elif k.Key = ConsoleKey.Enter then
        exitHistoryMode ()
        s.ExitArmed <- false
        Submit (String(s.Buffer.ToArray()))
    elif k.Key = ConsoleKey.Backspace then
        exitHistoryMode ()
        s.ExitArmed <- false
        if s.Cursor > 0 then
            s.Buffer.RemoveAt(s.Cursor - 1)
            s.Cursor <- s.Cursor - 1
        Continue
    elif k.Key = ConsoleKey.Delete then
        exitHistoryMode ()
        s.ExitArmed <- false
        if s.Cursor < s.Buffer.Count then
            s.Buffer.RemoveAt s.Cursor
        Continue
    elif k.Key = ConsoleKey.UpArrow then
        s.ExitArmed <- false
        if historyStore.Count = 0 then
            Continue
        else
            if s.HistoryIdx = -1 then
                s.SavedBuffer <- Some (ResizeArray<char>(s.Buffer))
                s.HistoryIdx <- historyStore.Count - 1
            elif s.HistoryIdx > 0 then
                s.HistoryIdx <- s.HistoryIdx - 1
            s.Buffer.Clear()
            s.Buffer.AddRange(historyStore.[s.HistoryIdx].ToCharArray())
            s.Cursor <- s.Buffer.Count
            Continue
    elif k.Key = ConsoleKey.DownArrow then
        s.ExitArmed <- false
        if s.HistoryIdx = -1 then
            Continue
        else
            s.HistoryIdx <- s.HistoryIdx + 1
            if s.HistoryIdx >= historyStore.Count then
                s.HistoryIdx <- -1
                s.Buffer.Clear()
                match s.SavedBuffer with
                | Some saved -> s.Buffer.AddRange(saved)
                | None -> ()
                s.SavedBuffer <- None
                s.Cursor <- s.Buffer.Count
            else
                s.Buffer.Clear()
                s.Buffer.AddRange(historyStore.[s.HistoryIdx].ToCharArray())
                s.Cursor <- s.Buffer.Count
            Continue
    elif isCtrl k ConsoleKey.LeftArrow then
        exitHistoryMode ()
        s.ExitArmed <- false
        s.Cursor <- prevWordStart s.Buffer s.Cursor
        Continue
    elif isCtrl k ConsoleKey.RightArrow then
        exitHistoryMode ()
        s.ExitArmed <- false
        s.Cursor <- nextWordEnd s.Buffer s.Cursor
        Continue
    elif isCtrl k ConsoleKey.W then
        exitHistoryMode ()
        s.ExitArmed <- false
        if s.Cursor > 0 then
            let mutable p = prevWordStart s.Buffer s.Cursor
            // Divergence from GNU readline unix-word-rubout: when only whitespace precedes the
            // deleted word, also consume it — clears short prompts in one keystroke (matches zsh).
            let mutable j = p - 1
            while j >= 0 && (s.Buffer.[j] = ' ' || s.Buffer.[j] = '\t') do j <- j - 1
            if j < 0 then p <- 0
            s.Buffer.RemoveRange(p, s.Cursor - p)
            s.Cursor <- p
        Continue
    elif k.Key = ConsoleKey.LeftArrow then
        exitHistoryMode ()
        s.ExitArmed <- false
        if s.Cursor > 0 then s.Cursor <- s.Cursor - 1
        Continue
    elif k.Key = ConsoleKey.RightArrow then
        exitHistoryMode ()
        s.ExitArmed <- false
        if s.Cursor < s.Buffer.Count then s.Cursor <- s.Cursor + 1
        Continue
    elif k.Key = ConsoleKey.Home then
        exitHistoryMode ()
        s.ExitArmed <- false
        // beginning of current visual line (= last \n before cursor + 1, or 0)
        let mutable i = s.Cursor - 1
        while i >= 0 && s.Buffer.[i] <> '\n' do i <- i - 1
        s.Cursor <- i + 1
        Continue
    elif k.Key = ConsoleKey.End then
        exitHistoryMode ()
        s.ExitArmed <- false
        // end of current visual line
        let mutable i = s.Cursor
        while i < s.Buffer.Count && s.Buffer.[i] <> '\n' do i <- i + 1
        s.Cursor <- i
        Continue
    elif isCtrl k ConsoleKey.Z then
        exitHistoryMode ()
        s.ExitArmed <- false
        if undoStack.Count > 0 then
            let snapshot = ResizeArray<char>(s.Buffer)
            pushBounded redoStack (snapshot, s.Cursor)
            let (prevBuf, prevCursor) = undoStack.[undoStack.Count - 1]
            undoStack.RemoveAt(undoStack.Count - 1)
            s.Buffer.Clear()
            s.Buffer.AddRange prevBuf
            s.Cursor <- prevCursor
        Continue
    elif isCtrl k ConsoleKey.Y then
        exitHistoryMode ()
        s.ExitArmed <- false
        if redoStack.Count > 0 then
            let snapshot = ResizeArray<char>(s.Buffer)
            pushBounded undoStack (snapshot, s.Cursor)
            let (nextBuf, nextCursor) = redoStack.[redoStack.Count - 1]
            redoStack.RemoveAt(redoStack.Count - 1)
            s.Buffer.Clear()
            s.Buffer.AddRange nextBuf
            s.Cursor <- nextCursor
        Continue
    elif not (Char.IsControl k.KeyChar) then
        exitHistoryMode ()
        s.ExitArmed <- false
        let snapshot = ResizeArray<char>(s.Buffer)
        pushBounded undoStack (snapshot, s.Cursor)
        redoStack.Clear()
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
    // Before erasing, move cursor to the bottom of the previously-rendered area so
    // eraseLines (which walks UP count-1 rows) starts from the correct position.
    if st.RowsBelowCursor > 0 then
        writeRaw ("\x1b[" + string st.RowsBelowCursor + "B")
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
    st.RowsBelowCursor <- upBy   // save for next redraw's erase pass

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
        [ "/help",         strings.CmdHelpDesc
          "/clear",        strings.CmdClearDesc
          "/init",         strings.CmdInitDesc
          "/exit",         strings.CmdExitDesc
          "/diff",         strings.CmdDiffDesc
          "/diff --staged", strings.CmdDiffDesc ]
    let st : S =
        { Buffer          = ResizeArray<char>()
          Cursor          = 0
          LinesRendered   = 0
          ExitArmed       = false
          RowsBelowCursor = 0
          HistoryIdx      = -1
          SavedBuffer     = None
          PromptText      = prompt
          PromptVisLen    = visLen
          HintWhenArmed   = strings.ExitHint
          Width           = max 40 Console.WindowWidth
          SlashHelp       = slashHelp }

    Console.TreatControlCAsInput <- true
    try
        redraw st
        let mutable result : string option voption = ValueNone
        while result.IsNone && not ct.IsCancellationRequested do
            // Console.ReadKey is blocking; we accept that — cancellation in this
            // path is not expected in normal flow (Ctrl+C while reading is a wipe).
            let k = Console.ReadKey(intercept = true)
            let eraseRendered () =
                if st.RowsBelowCursor > 0 then
                    writeRaw ("\x1b[" + string st.RowsBelowCursor + "B")
                eraseLines st.LinesRendered
                st.RowsBelowCursor <- 0
            match applyKey k st with
            | Continue -> redraw st
            | Wipe     -> redraw st
            | Submit s ->
                eraseRendered ()
                let normalized = InputSanitize.normalize s
                if not (String.IsNullOrWhiteSpace normalized) then
                    if historyStore.Count = 0 || historyStore.[historyStore.Count - 1] <> normalized then
                        historyStore.Add normalized
                st.HistoryIdx <- -1
                st.SavedBuffer <- None
                result <- ValueSome (Some normalized)
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
