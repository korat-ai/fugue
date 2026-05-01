module Fugue.Cli.ReadLine

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Fugue.Core.Localization

// Bracketed paste mode escape sequences.
let private pasteBeginSeq = "\x1b[200~"
let private pasteEndSeq   = "\x1b[201~"

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
let slashCommands : string[] =
    [| "/help"; "/clear"; "/summary"; "/short"; "/long"; "/clear-history"; "/tools"; "/new"; "/init"; "/exit"; "/quit"; "/diff"; "/diff --staged"; "/doctor"; "/summarize"; "/todo"; "/rate"; "/annotate"; "/gen uuid"; "/gen ulid"; "/gen nanoid"; "/rename"; "/env list"; "/env set"; "/env unset"; "/cron"; "/regex" |]

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

type ReadLineCallbacks =
    { OnClearScreen:  unit -> unit
      OnAnnotateUp:   unit -> unit
      OnAnnotateDown: unit -> unit }
let defaultCallbacks : ReadLineCallbacks =
    { OnClearScreen = ignore; OnAnnotateUp = ignore; OnAnnotateDown = ignore }

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
    Placeholder:             string
    ColorEnabled:            bool
    Width:                   int
    SlashHelp:               (string * string) list  // (name, dim description) pairs
}

/// Action returned by the pure key-handler. The caller (read-loop) decides what to do.
type Action =
    | Continue
    | Submit of string
    | Quit
    | Wipe
    | ClearScreen
    | AnnotateUp
    | AnnotateDown

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
    elif isCtrl k ConsoleKey.UpArrow then
        s.ExitArmed <- false
        AnnotateUp
    elif isCtrl k ConsoleKey.DownArrow then
        s.ExitArmed <- false
        AnnotateDown
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
    elif isCtrl k ConsoleKey.L then
        s.ExitArmed <- false
        ClearScreen
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

/// Bracketed paste state machine — tracks where we are in the \x1b[200~…\x1b[201~ sequence.
/// States progress: Normal → EscSeen → BracketSeen → Digit2 → Digit0x → Digit0y → Tilde200
///                  then PasteActive → EscInPaste → BracketInPaste → Digit2P → Digit0xP → Digit1P → (end)
/// On any mismatch the accumulated prefix chars are flushed as normal input.
type PasteState =
    | Normal
    | EscSeen           // saw \x1b
    | BracketSeen       // saw \x1b[
    | Digit2            // saw \x1b[2
    | Digit0            // saw \x1b[20
    | Digit0v2          // saw \x1b[200
    | PasteActive       // inside paste; collecting content
    | EscInPaste        // saw \x1b while in PasteActive
    | BracketInPaste    // saw \x1b[ while in PasteActive
    | Digit2InPaste     // saw \x1b[2 while in PasteActive
    | Digit0InPaste     // saw \x1b[20 while in PasteActive
    | Digit1InPaste     // saw \x1b[201 while in PasteActive

/// Pure helper: set s.Buffer and s.Cursor to the pasted content (trim single trailing \n if any).
let applyPastedText (text: string) (s: S) : unit =
    let normalized = if text.EndsWith "\n" then text.[0 .. text.Length - 2] else text
    s.Buffer.Clear()
    s.Buffer.AddRange(normalized.ToCharArray())
    s.Cursor <- s.Buffer.Count

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
    // 2b. if buffer is empty and color is on, write dim placeholder then move cursor back
    if st.Buffer.Count = 0 && st.Placeholder <> "" && st.ColorEnabled then
        writeRaw ("\x1b[2m" + st.Placeholder + "\x1b[0m")
        // move cursor back to right after the prompt (col = PromptVisLen)
        writeRaw ("\r\x1b[" + string st.PromptVisLen + "C")
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
        // Show inline slash command suggestions, capped at maxSuggestions to keep
        // the rendered area bounded (prevents terminal scroll from corrupting
        // cursor-tracking in eraseLines on the next redraw).
        let prefix = String(st.Buffer.ToArray())
        let matches = st.SlashHelp |> List.filter (fun (name, _) -> name.StartsWith(prefix))
        let maxSuggestions = 8
        for (name, desc) in matches |> List.truncate maxSuggestions do
            writeRaw ("\n\x1b[2m  " + name + "  " + desc + "\x1b[0m")
            st.LinesRendered <- st.LinesRendered + 1
        if matches.Length > maxSuggestions then
            writeRaw $"\n\x1b[2m  … {matches.Length - maxSuggestions} more (keep typing)\x1b[0m"
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

let readAsync (prompt: string) (strings: Strings) (callbacks: ReadLineCallbacks) (colorEnabled: bool) (ct: CancellationToken) : Task<string option> = task {
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
        [ // Core
          "/help",         strings.CmdHelpDesc
          "/clear",        strings.CmdClearDesc
          "/clear-history", strings.CmdClearHistoryDesc
          "/new",          strings.CmdNewDesc
          "/init",         strings.CmdInitDesc
          "/exit",         strings.CmdExitDesc
          "/quit",         strings.CmdExitDesc
          // Verbosity & summarization
          "/short",        strings.CmdShortDesc
          "/long",         strings.CmdLongDesc
          "/summary",      strings.CmdSummaryDesc
          "/summarize",    strings.CmdSummarizeDesc
          "/compress",     "summarise session history and reset context window"
          // Inspection
          "/tools",        strings.CmdToolsDesc
          "/diff",         strings.CmdDiffDesc
          "/diff --staged", strings.CmdDiffDesc
          "/git-log",      strings.CmdGitLogDesc
          "/doctor",       "run environment diagnostics"
          "/history",      strings.CmdHistoryDesc
          "/onboard",      strings.CmdOnboardDesc
          // Model
          "/model",        "interactive model picker (or: /model set <name>, /model suggest)"
          "/model set",    "switch model on current provider"
          "/model suggest", "ask agent for model recommendation"
          // Session metadata
          "/rename",       "set a human-readable session title"
          "/rate",         strings.CmdRateDesc
          "/annotate",     strings.CmdAnnotateDesc
          "/note",         strings.CmdNoteDesc
          "/notes",        strings.CmdNotesDesc
          "/bookmark",     strings.CmdBookmarkDesc
          "/bookmarks",    strings.CmdBookmarksDesc
          "/snippet",      strings.CmdSnippetDesc
          "/find",         strings.CmdFindDesc
          "/undo",         strings.CmdUndoDesc
          "/todo",         strings.CmdTodoDesc
          "/zen",          strings.CmdZenDesc
          "/theme",        strings.CmdThemeDesc
          "/templates",    "list available session templates"
          // Workflow
          "/ask",          strings.CmdAskDesc
          "/document",     strings.CmdDocumentDesc
          "/activity",     strings.CmdActivityDesc
          "/alias",        strings.CmdAliasDesc
          "/scratch",      strings.CmdScratchDesc
          "/squash",       strings.CmdSquashDesc
          "/macro",        strings.CmdMacroDesc
          "/bench",        strings.CmdBenchDesc
          "/compat",       strings.CmdCompatDesc
          "/issue",        strings.CmdIssueDesc
          "/watch",        strings.CmdWatchDesc
          "/review pr",    strings.CmdReviewPrDesc
          // Generation / scaffolding (PROMPT)
          "/scaffold du",       "generate F# DU with match examples and JSON attrs"
          "/scaffold cqrs",     "generate CQRS Command/Handler/Event/Test slice"
          "/scaffold actor",    "generate typed actor with command DU and supervision"
          "/derive codec",      "generate AOT-safe STJ JsonSerializerContext"
          "/migrate oop-to-fp", "convert OOP class hierarchy to F# DUs"
          "/refactor pipeline", "rewrite F# let-bindings as |> pipe chain"
          "/translate comments","translate code comments to English"
          "/port",              "idiomatic cross-language code translation"
          "/idiomatic",         "suggest idiomatic style improvements for the language"
          "/incremental",       "add a new function in-place without rewriting files"
          "/breaking-changes",  "detect breaking API changes and list call sites"
          "/complexity",        "cyclomatic complexity analysis and refactoring hints"
          "/infer-type",        "infer and explain F# type signature of an expression"
          "/pointfree",         "offer point-free rewrite of an F# function"
          "/monad",             "generate F# CE / Scala for-comprehension from pseudocode"
          "/rop",               "generate ROP Result/Either chain from plain-English"
          "/check exhaustive",  "find FS0025 incomplete-pattern warnings and fix"
          "/check tail-rec",    "detect non-tail-recursive functions and offer rewrites"
          "/check effects",     "verify ZIO/Cats Effect/Async type consistency"
          // Utility
          "/gen uuid",     "generate a UUID"
          "/gen ulid",     "generate a ULID"
          "/gen nanoid",   "generate a NanoID"
          "/env list",     "list session environment variables"
          "/env set",      "set a session environment variable"
          "/env unset",    "unset a session environment variable"
          "/cron",         "convert NL schedule to cron expression"
          "/regex",        "test a regex pattern against sample input"
          "/http",         "inline HTTP client (HTTPie-style)"
          "/report-bug",   "capture last failed turn as a GitHub issue draft" ]
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
          Placeholder     = strings.InputPlaceholder
          ColorEnabled    = colorEnabled
          Width           = max 40 Console.WindowWidth
          SlashHelp       = slashHelp }

    Console.TreatControlCAsInput <- true
    // Tab-completion cycle state — resets on any non-Tab keypress.
    let mutable tabMatches : string[] = [||]
    let mutable tabIndex   = -1
    try
        redraw st
        let mutable result      : string option voption = ValueNone
        let mutable pasteState  : PasteState             = Normal
        let mutable pasteBuf    : System.Text.StringBuilder = System.Text.StringBuilder()
        // Prefix chars accumulated while tentatively matching the begin/end bracket sequence.
        // On mismatch they are flushed as ordinary input.
        let mutable escapeBuf   : ResizeArray<char>      = ResizeArray<char>()

        let eraseRendered () =
            if st.RowsBelowCursor > 0 then
                writeRaw ("\x1b[" + string st.RowsBelowCursor + "B")
            eraseLines st.LinesRendered
            st.RowsBelowCursor <- 0

        // Flush accumulated escape prefix as normal key events (no-op chars go through applyKey).
        let flushEscape () =
            for ch in escapeBuf do
                let ki = ConsoleKeyInfo(ch, ConsoleKey.A, false, false, false)
                match applyKey ki st with
                | Continue | Wipe | ClearScreen | AnnotateUp | AnnotateDown -> ()
                | Submit _ | Quit -> ()   // rare; ignore — these chars won't normally Submit/Quit
            escapeBuf.Clear()
            pasteState <- Normal

        while result.IsNone && not ct.IsCancellationRequested do
            // Console.ReadKey is blocking; we accept that — cancellation in this
            // path is not expected in normal flow (Ctrl+C while reading is a wipe).
            let k = Console.ReadKey(intercept = true)
            if k.Key = ConsoleKey.Tab then
                let currentInput = String(st.Buffer.ToArray())
                if currentInput.StartsWith "/" then
                    let needsReset =
                        tabMatches.Length = 0 ||
                        (tabIndex >= 0 && tabIndex < tabMatches.Length &&
                         currentInput <> tabMatches.[tabIndex])
                    if needsReset then
                        tabMatches <- slashCommands |> Array.filter (fun c -> c.StartsWith currentInput)
                        tabIndex   <- -1
                    if tabMatches.Length > 0 then
                        tabIndex <- (tabIndex + 1) % tabMatches.Length
                        let completed = tabMatches.[tabIndex]
                        st.Buffer.Clear()
                        st.Buffer.AddRange(completed.ToCharArray())
                        st.Cursor <- st.Buffer.Count
                        redraw st
                // Tab on empty / non-slash input: ignore.
            else
                // Any non-Tab key resets the completion cycle.
                tabMatches <- [||]
                tabIndex   <- -1
                let c = k.KeyChar

                // ── Paste state machine ───────────────────────────────────────
                match pasteState with
                | Normal ->
                    if c = '\x1b' then
                        escapeBuf.Clear()
                        escapeBuf.Add c
                        pasteState <- EscSeen
                    else
                        match applyKey k st with
                        | Continue -> redraw st
                        | Wipe     -> redraw st
                        | ClearScreen ->
                            eraseRendered ()
                            writeRaw "\x1b[2J\x1b[H"
                            st.LinesRendered <- 0
                            st.RowsBelowCursor <- 0
                            callbacks.OnClearScreen ()
                            redraw st
                        | AnnotateUp ->
                            callbacks.OnAnnotateUp ()
                            redraw st
                        | AnnotateDown ->
                            callbacks.OnAnnotateDown ()
                            redraw st
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

                | EscSeen ->
                    if c = '[' then
                        escapeBuf.Add c
                        pasteState <- BracketSeen
                    else
                        flushEscape ()
                        let ki2 = ConsoleKeyInfo(c, k.Key, false, false, false)
                        match applyKey ki2 st with
                        | Continue | Wipe | ClearScreen | AnnotateUp | AnnotateDown -> redraw st
                        | Submit _ | Quit -> ()

                | BracketSeen ->
                    if c = '2' then
                        escapeBuf.Add c
                        pasteState <- Digit2
                    else
                        flushEscape ()
                        let ki2 = ConsoleKeyInfo(c, k.Key, false, false, false)
                        match applyKey ki2 st with
                        | Continue | Wipe | ClearScreen | AnnotateUp | AnnotateDown -> redraw st
                        | Submit _ | Quit -> ()

                | Digit2 ->
                    if c = '0' then
                        escapeBuf.Add c
                        pasteState <- Digit0
                    else
                        flushEscape ()
                        let ki2 = ConsoleKeyInfo(c, k.Key, false, false, false)
                        match applyKey ki2 st with
                        | Continue | Wipe | ClearScreen | AnnotateUp | AnnotateDown -> redraw st
                        | Submit _ | Quit -> ()

                | Digit0 ->
                    if c = '0' then
                        escapeBuf.Add c
                        pasteState <- Digit0v2
                    else
                        flushEscape ()
                        let ki2 = ConsoleKeyInfo(c, k.Key, false, false, false)
                        match applyKey ki2 st with
                        | Continue | Wipe | ClearScreen | AnnotateUp | AnnotateDown -> redraw st
                        | Submit _ | Quit -> ()

                | Digit0v2 ->
                    if c = '~' then
                        // Confirmed \x1b[200~ — enter paste collection mode
                        escapeBuf.Clear()
                        pasteBuf.Clear() |> ignore
                        pasteState <- PasteActive
                    else
                        flushEscape ()
                        let ki2 = ConsoleKeyInfo(c, k.Key, false, false, false)
                        match applyKey ki2 st with
                        | Continue | Wipe | ClearScreen | AnnotateUp | AnnotateDown -> redraw st
                        | Submit _ | Quit -> ()

                | PasteActive ->
                    if c = '\x1b' then
                        escapeBuf.Clear()
                        escapeBuf.Add c
                        pasteState <- EscInPaste
                    else
                        pasteBuf.Append c |> ignore

                | EscInPaste ->
                    if c = '[' then
                        escapeBuf.Add c
                        pasteState <- BracketInPaste
                    else
                        // Not an end bracket — flush escape to pasteBuf and continue collecting
                        for ch in escapeBuf do pasteBuf.Append ch |> ignore
                        escapeBuf.Clear()
                        pasteBuf.Append c |> ignore
                        pasteState <- PasteActive

                | BracketInPaste ->
                    if c = '2' then
                        escapeBuf.Add c
                        pasteState <- Digit2InPaste
                    else
                        for ch in escapeBuf do pasteBuf.Append ch |> ignore
                        escapeBuf.Clear()
                        pasteBuf.Append c |> ignore
                        pasteState <- PasteActive

                | Digit2InPaste ->
                    if c = '0' then
                        escapeBuf.Add c
                        pasteState <- Digit0InPaste
                    else
                        for ch in escapeBuf do pasteBuf.Append ch |> ignore
                        escapeBuf.Clear()
                        pasteBuf.Append c |> ignore
                        pasteState <- PasteActive

                | Digit0InPaste ->
                    if c = '1' then
                        escapeBuf.Add c
                        pasteState <- Digit1InPaste
                    else
                        for ch in escapeBuf do pasteBuf.Append ch |> ignore
                        escapeBuf.Clear()
                        pasteBuf.Append c |> ignore
                        pasteState <- PasteActive

                | Digit1InPaste ->
                    if c = '~' then
                        // Confirmed \x1b[201~ — paste complete
                        escapeBuf.Clear()
                        applyPastedText (pasteBuf.ToString()) st
                        pasteState <- Normal
                        redraw st
                    else
                        for ch in escapeBuf do pasteBuf.Append ch |> ignore
                        escapeBuf.Clear()
                        pasteBuf.Append c |> ignore
                        pasteState <- PasteActive

        return
            match result with
            | ValueSome v -> v
            | ValueNone   -> None
    finally
        Console.TreatControlCAsInput <- false
}
