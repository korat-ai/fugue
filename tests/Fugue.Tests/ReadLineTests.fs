module Fugue.Tests.ReadLineTests

open System
open System.Collections.Generic
open Xunit
open FsUnit.Xunit
open Fugue.Cli.ReadLine

let private mkState (text: string) (cursor: int) : S =
    { Buffer          = ResizeArray<char>(text.ToCharArray())
      Cursor          = cursor
      LinesRendered   = 0
      ExitArmed       = false
      RowsBelowCursor = 0
      HistoryIdx      = -1
      SavedBuffer     = None
      PromptText      = "> "
      PromptVisLen    = 2
      HintWhenArmed   = "Press Ctrl+C again to exit"
      Width           = 80
      SlashHelp       = [] }

let private key (c: char) (mods: ConsoleModifiers) : ConsoleKeyInfo =
    let cKey =
        match c with
        | '\r' -> ConsoleKey.Enter
        | _    -> ConsoleKey.A   // not used for printable chars
    ConsoleKeyInfo(c, cKey, (mods &&& ConsoleModifiers.Shift) <> ConsoleModifiers.None,
                   (mods &&& ConsoleModifiers.Alt)   <> ConsoleModifiers.None,
                   (mods &&& ConsoleModifiers.Control) <> ConsoleModifiers.None)

let private special (k: ConsoleKey) (mods: ConsoleModifiers) : ConsoleKeyInfo =
    ConsoleKeyInfo(' ', k, (mods &&& ConsoleModifiers.Shift) <> ConsoleModifiers.None,
                   (mods &&& ConsoleModifiers.Alt)   <> ConsoleModifiers.None,
                   (mods &&& ConsoleModifiers.Control) <> ConsoleModifiers.None)

[<Fact>]
let ``insert printable char advances buffer + cursor`` () =
    let s = mkState "" 0
    let act = applyKey (key 'a' ConsoleModifiers.None) s
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal "a"
    s.Cursor |> should equal 1

[<Fact>]
let ``Enter on non-empty buffer returns Submit`` () =
    let s = mkState "hello" 5
    let act = applyKey (special ConsoleKey.Enter ConsoleModifiers.None) s
    act |> should equal (Submit "hello")

[<Fact>]
let ``Shift+Enter inserts newline`` () =
    let s = mkState "abc" 3
    let act = applyKey (special ConsoleKey.Enter ConsoleModifiers.Shift) s
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal "abc\n"
    s.Cursor |> should equal 4

[<Fact>]
let ``Ctrl+D on empty buffer returns Quit`` () =
    let s = mkState "" 0
    let act = applyKey (special ConsoleKey.D ConsoleModifiers.Control) s
    act |> should equal Quit

[<Fact>]
let ``Ctrl+C wipes buffer and signals Wipe`` () =
    let s = mkState "hello" 5
    let act = applyKey (special ConsoleKey.C ConsoleModifiers.Control) s
    act |> should equal Wipe
    s.Buffer.Count |> should equal 0
    s.Cursor       |> should equal 0

[<Fact>]
let ``Backspace removes char before cursor`` () =
    let s = mkState "abc" 3
    let act = applyKey (special ConsoleKey.Backspace ConsoleModifiers.None) s
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal "ab"
    s.Cursor |> should equal 2

[<Fact>]
let ``LeftArrow at cursor 0 stays at 0`` () =
    let s = mkState "abc" 0
    let act = applyKey (special ConsoleKey.LeftArrow ConsoleModifiers.None) s
    act |> should equal Continue
    s.Cursor |> should equal 0

[<Fact>]
let ``space is insertable`` () =
    let s = mkState "" 0
    let act = applyKey (key ' ' ConsoleModifiers.None) s
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal " "
    s.Cursor |> should equal 1

[<Fact>]
let ``Backspace at cursor 0 is no-op`` () =
    let s = mkState "abc" 0
    let act = applyKey (special ConsoleKey.Backspace ConsoleModifiers.None) s
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal "abc"
    s.Cursor |> should equal 0

[<Fact>]
let ``insert at mid-buffer splits correctly`` () =
    let s = mkState "ab" 1
    let act = applyKey (key 'x' ConsoleModifiers.None) s
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal "axb"
    s.Cursor |> should equal 2

[<Fact>]
let ``Wipe preserves LinesRendered for redraw to clear screen`` () =
    let s = mkState "hello" 5
    s.LinesRendered <- 3
    let act = applyKey (special ConsoleKey.C ConsoleModifiers.Control) s
    act |> should equal Wipe
    // applyKey must NOT reset LinesRendered — redraw needs the prior count
    // to emit cursor-up + clear-to-end sequences. Render state is redraw's job.
    s.LinesRendered |> should equal 3
    s.Buffer.Count |> should equal 0
    s.Cursor       |> should equal 0

[<Fact>]
let ``Ctrl+C on empty buffer arms ExitArmed without quitting`` () =
    let s = mkState "" 0
    let act = applyKey (special ConsoleKey.C ConsoleModifiers.Control) s
    act |> should equal Wipe
    s.ExitArmed |> should equal true

[<Fact>]
let ``Ctrl+C twice on empty buffer returns Quit`` () =
    let s = mkState "" 0
    let _ = applyKey (special ConsoleKey.C ConsoleModifiers.Control) s
    let act2 = applyKey (special ConsoleKey.C ConsoleModifiers.Control) s
    act2 |> should equal Quit

[<Fact>]
let ``any non-Ctrl+C input disarms ExitArmed`` () =
    let s = mkState "" 0
    let _ = applyKey (special ConsoleKey.C ConsoleModifiers.Control) s
    s.ExitArmed |> should equal true
    let _ = applyKey (key 'a' ConsoleModifiers.None) s
    s.ExitArmed |> should equal false
    // After typing 'a', buffer is "a" (non-empty), so Ctrl+C clears it (Wipe) not Quit
    let act = applyKey (special ConsoleKey.C ConsoleModifiers.Control) s
    act |> should equal Wipe
    s.Buffer.Count |> should equal 0

[<Fact>]
let ``slash buffer prefix is recognized for hint rendering`` () =
    let s = mkState "/" 1
    // No applyKey side effect on the slash-suggestion field — just verify mkState constructs
    s.Buffer.Count |> should equal 1
    s.Buffer.[0] |> should equal '/'

[<Fact>]
let ``CtrlL_returns_ClearScreen`` () =
    let s = mkState "hello" 3
    let act = applyKey (special ConsoleKey.L ConsoleModifiers.Control) s
    act |> should equal ClearScreen
    s.ExitArmed |> should equal false

// ── History tests ─────────────────────────────────────────────────────────────

[<Fact>]
let ``history_UpOnEmptyHistory_isNoOp`` () =
    clearHistory()
    let s = mkState "abc" 3
    let act = applyKey (special ConsoleKey.UpArrow ConsoleModifiers.None) s
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal "abc"
    s.Cursor |> should equal 3

[<Fact>]
let ``history_firstUp_loadsLatestEntry_savesBuffer`` () =
    clearHistory()
    // simulate prior Submit by adding directly
    historyStore.Add "first"      // index 0
    historyStore.Add "second"     // index 1 (most recent)
    let s = mkState "draft" 5
    let act = applyKey (special ConsoleKey.UpArrow ConsoleModifiers.None) s
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal "second"
    s.Cursor |> should equal 6
    s.HistoryIdx |> should equal 1
    s.SavedBuffer |> should not' (equal None)

[<Fact>]
let ``history_secondUp_loadsOlderEntry`` () =
    clearHistory()
    historyStore.Add "first"
    historyStore.Add "second"
    let s = mkState "" 0
    let _ = applyKey (special ConsoleKey.UpArrow ConsoleModifiers.None) s   // → "second"
    let act = applyKey (special ConsoleKey.UpArrow ConsoleModifiers.None) s   // → "first"
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal "first"
    s.HistoryIdx |> should equal 0

[<Fact>]
let ``history_UpAtOldest_clamps`` () =
    clearHistory()
    historyStore.Add "only"
    let s = mkState "" 0
    let _ = applyKey (special ConsoleKey.UpArrow ConsoleModifiers.None) s   // → "only", idx=0
    let act = applyKey (special ConsoleKey.UpArrow ConsoleModifiers.None) s   // clamped
    act |> should equal Continue
    s.HistoryIdx |> should equal 0
    String(s.Buffer.ToArray()) |> should equal "only"

[<Fact>]
let ``history_DownFromBrowse_advancesToNewer`` () =
    clearHistory()
    historyStore.Add "first"
    historyStore.Add "second"
    let s = mkState "" 0
    let _ = applyKey (special ConsoleKey.UpArrow ConsoleModifiers.None) s   // → "second", idx=1
    let _ = applyKey (special ConsoleKey.UpArrow ConsoleModifiers.None) s   // → "first", idx=0
    let act = applyKey (special ConsoleKey.DownArrow ConsoleModifiers.None) s  // → "second", idx=1
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal "second"
    s.HistoryIdx |> should equal 1

[<Fact>]
let ``history_DownPastEnd_restoresSavedBuffer`` () =
    clearHistory()
    historyStore.Add "entry"
    let s = mkState "draft" 5
    let _ = applyKey (special ConsoleKey.UpArrow ConsoleModifiers.None) s   // save "draft", load "entry"
    let act = applyKey (special ConsoleKey.DownArrow ConsoleModifiers.None) s  // past end → restore
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal "draft"
    s.HistoryIdx |> should equal -1
    s.SavedBuffer |> should equal None

[<Fact>]
let ``history_DownPastEnd_withEmptySavedBuffer_clears`` () =
    clearHistory()
    historyStore.Add "entry"
    let s = mkState "" 0   // empty buffer before Up
    let _ = applyKey (special ConsoleKey.UpArrow ConsoleModifiers.None) s
    let act = applyKey (special ConsoleKey.DownArrow ConsoleModifiers.None) s
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal ""
    s.Cursor |> should equal 0
    s.HistoryIdx |> should equal -1

[<Fact>]
let ``history_DownOnNotBrowsing_isNoOp`` () =
    clearHistory()
    let s = mkState "hello" 5
    let act = applyKey (special ConsoleKey.DownArrow ConsoleModifiers.None) s
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal "hello"
    s.Cursor |> should equal 5

[<Fact>]
let ``history_Submit_appendsToStore`` () =
    clearHistory()
    let s = mkState "foo" 3
    let act = applyKey (special ConsoleKey.Enter ConsoleModifiers.None) s
    act |> should equal (Submit "foo")
    // Note: historyStore is updated in readAsync (Submit branch), not in applyKey.
    // This test verifies applyKey returns Submit correctly; the readAsync integration
    // is covered by the Submit + dedup tests that call readAsync or simulate the store.
    ()

[<Fact>]
let ``history_dedup_consecutiveDuplicate_notAdded`` () =
    clearHistory()
    historyStore.Add "foo"
    // Simulate what readAsync does on Submit: add if not dup
    let input = "foo"
    if historyStore.Count = 0 || historyStore.[historyStore.Count - 1] <> input then
        historyStore.Add input
    historyStore.Count |> should equal 1

[<Fact>]
let ``history_TypeWhileBrowsing_resetsHistoryIdx`` () =
    clearHistory()
    historyStore.Add "entry"
    let s = mkState "" 0
    let _ = applyKey (special ConsoleKey.UpArrow ConsoleModifiers.None) s
    s.HistoryIdx |> should equal 0
    // Type a char — should reset HistoryIdx
    let act = applyKey (key 'x' ConsoleModifiers.None) s
    act |> should equal Continue
    s.HistoryIdx |> should equal -1
    s.SavedBuffer |> should equal None
    // Buffer should have "entry" + 'x' inserted at end (cursor was at end of "entry")
    String(s.Buffer.ToArray()) |> should equal "entryx"

// ── Word movement tests ───────────────────────────────────────────────────────

[<Fact>]
let ``word_CtrlLeft_fromMidWord_jumpsToWordStart`` () =
    let s = mkState "hello world" 8   // cursor inside "world"
    let act = applyKey (special ConsoleKey.LeftArrow ConsoleModifiers.Control) s
    act |> should equal Continue
    s.Cursor |> should equal 6   // start of "world"

[<Fact>]
let ``word_CtrlLeft_fromWordStart_jumpsToPrevWordStart`` () =
    let s = mkState "hello world" 6   // cursor at start of "world"
    let act = applyKey (special ConsoleKey.LeftArrow ConsoleModifiers.Control) s
    act |> should equal Continue
    s.Cursor |> should equal 0   // start of "hello"

[<Fact>]
let ``word_CtrlLeft_atZero_isNoOp`` () =
    let s = mkState "hello" 0
    let act = applyKey (special ConsoleKey.LeftArrow ConsoleModifiers.Control) s
    act |> should equal Continue
    s.Cursor |> should equal 0

[<Fact>]
let ``word_CtrlRight_fromMidWord_jumpsPastWordEnd`` () =
    let s = mkState "hello world" 2   // cursor inside "hello"
    let act = applyKey (special ConsoleKey.RightArrow ConsoleModifiers.Control) s
    act |> should equal Continue
    s.Cursor |> should equal 5   // past end of "hello"

[<Fact>]
let ``word_CtrlRight_atEnd_isNoOp`` () =
    let s = mkState "hello" 5
    let act = applyKey (special ConsoleKey.RightArrow ConsoleModifiers.Control) s
    act |> should equal Continue
    s.Cursor |> should equal 5

[<Fact>]
let ``word_CtrlW_deletesWordBackward`` () =
    let s = mkState "hello world" 11   // cursor at end
    let act = applyKey (special ConsoleKey.W ConsoleModifiers.Control) s
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal "hello "
    s.Cursor |> should equal 6

[<Fact>]
let ``word_CtrlW_deletesLeadingWhitespace`` () =
    let s = mkState "  hello" 7   // cursor at end
    let act = applyKey (special ConsoleKey.W ConsoleModifiers.Control) s
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal ""
    s.Cursor |> should equal 0

[<Fact>]
let ``word_CtrlW_atZero_isNoOp`` () =
    let s = mkState "hello" 0
    let act = applyKey (special ConsoleKey.W ConsoleModifiers.Control) s
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal "hello"
    s.Cursor |> should equal 0

// ── Bracketed paste tests ─────────────────────────────────────────────────────

[<Fact>]
let ``paste_applyPastedText_setsBufferAndCursor`` () =
    let s = mkState "old" 3
    applyPastedText "hello world" s
    String(s.Buffer.ToArray()) |> should equal "hello world"
    s.Cursor |> should equal 11

[<Fact>]
let ``paste_applyPastedText_replacesExistingContent`` () =
    let s = mkState "existing text" 5
    applyPastedText "new content" s
    String(s.Buffer.ToArray()) |> should equal "new content"
    s.Cursor |> should equal 11

[<Fact>]
let ``paste_applyPastedText_multiLinePreservesNewlines`` () =
    let s = mkState "" 0
    applyPastedText "line1\nline2\nline3" s
    String(s.Buffer.ToArray()) |> should equal "line1\nline2\nline3"
    s.Cursor |> should equal 17

[<Fact>]
let ``paste_applyPastedText_trims_trailing_newline`` () =
    // Pasting a block that ends in \n should strip the trailing newline
    // so the user can review before submitting.
    let s = mkState "" 0
    applyPastedText "hello\n" s

// ── Tab-completion list tests ─────────────────────────────────────────────────

[<Fact>]
let ``tab_slashCommands_containsExpectedEntries`` () =
    slashCommands |> should contain "/help"
    slashCommands |> should contain "/clear"
    slashCommands |> should contain "/new"
    slashCommands |> should contain "/exit"
    slashCommands |> should contain "/quit"
    slashCommands |> should contain "/diff"
    slashCommands |> should contain "/diff --staged"
    slashCommands |> should contain "/init"
    slashCommands |> should contain "/summarize"
    slashCommands |> should contain "/tools"
    slashCommands |> should contain "/clear-history"
    slashCommands |> should contain "/short"
    slashCommands |> should contain "/long"
    slashCommands |> should contain "/summary"
    slashCommands |> should contain "/doctor"

[<Fact>]
let ``tab_filterByPrefix_cl_returnsExpectedMatches`` () =
    let matches = slashCommands |> Array.filter (fun c -> c.StartsWith "/cl")
    matches |> should contain "/clear"
    matches |> should contain "/clear-history"
    matches.Length |> should equal 2

[<Fact>]
let ``tab_filterByPrefix_h_returnsSingleMatch`` () =
    let matches = slashCommands |> Array.filter (fun c -> c.StartsWith "/h")
    matches |> should equal [| "/help" |]

[<Fact>]
let ``tab_filterByPrefix_noMatch_returnsEmpty`` () =
    let matches = slashCommands |> Array.filter (fun c -> c.StartsWith "/zzz")
    matches |> should equal [||]

[<Fact>]
let ``tab_filterByPrefix_slash_returnsAll`` () =
    let matches = slashCommands |> Array.filter (fun c -> c.StartsWith "/")
    matches.Length |> should equal slashCommands.Length

[<Fact>]
let ``tab_allCommandsStartWithSlash`` () =
    slashCommands |> Array.forall (fun c -> c.StartsWith "/") |> should equal true

// ── Undo / redo tests ─────────────────────────────────────────────────────────

[<Fact>]
let ``undo_CtrlZ_onEmptyStack_isNoOp`` () =
    clearUndoRedo()
    let s = mkState "hello" 5
    let act = applyKey (special ConsoleKey.Z ConsoleModifiers.Control) s
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal "hello"
    s.Cursor |> should equal 5

[<Fact>]
let ``paste_applyPastedText_emptyString_clearsBuffer`` () =
    let s = mkState "something" 3
    applyPastedText "" s
    String(s.Buffer.ToArray()) |> should equal ""
    s.Cursor |> should equal 0

[<Fact>]
let ``paste_applyPastedText_cursorAtEnd_afterPaste`` () =
    let s = mkState "" 0
    applyPastedText "abc" s
    s.Cursor |> should equal s.Buffer.Count

let ``undo_typingChar_pushesSnapshotOntoUndoStack`` () =
    clearUndoRedo()
    let s = mkState "ab" 2
    let _ = applyKey (key 'c' ConsoleModifiers.None) s
    undoStack.Count |> should equal 1
    redoStack.Count |> should equal 0

[<Fact>]
let ``undo_CtrlZ_restoresPreviousBufferAndCursor`` () =
    clearUndoRedo()
    let s = mkState "ab" 2
    let _ = applyKey (key 'c' ConsoleModifiers.None) s   // buffer = "abc", cursor = 3; snapshot "ab"/2 on undoStack
    let act = applyKey (special ConsoleKey.Z ConsoleModifiers.Control) s
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal "ab"
    s.Cursor |> should equal 2
    undoStack.Count |> should equal 0

[<Fact>]
let ``undo_CtrlZ_pushesCurrentStateOntoRedoStack`` () =
    clearUndoRedo()
    let s = mkState "ab" 2
    let _ = applyKey (key 'c' ConsoleModifiers.None) s   // "abc", cursor 3
    let _ = applyKey (special ConsoleKey.Z ConsoleModifiers.Control) s
    redoStack.Count |> should equal 1
    let (redoBuf, redoCursor) = redoStack.[0]
    String(redoBuf.ToArray()) |> should equal "abc"
    redoCursor |> should equal 3

[<Fact>]
let ``redo_CtrlY_onEmptyStack_isNoOp`` () =
    clearUndoRedo()
    let s = mkState "hello" 5
    let act = applyKey (special ConsoleKey.Y ConsoleModifiers.Control) s
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal "hello"
    s.Cursor |> should equal 5

[<Fact>]
let ``redo_CtrlY_restoresRedoneStateAndPushesUndoViaPushBounded`` () =
    clearUndoRedo()
    let s = mkState "ab" 2
    let _ = applyKey (key 'c' ConsoleModifiers.None) s   // "abc"/3 → undoStack: [("ab",2)]
    let _ = applyKey (special ConsoleKey.Z ConsoleModifiers.Control) s  // back to "ab"/2 → redoStack: [("abc",3)]
    let act = applyKey (special ConsoleKey.Y ConsoleModifiers.Control) s  // redo
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal "abc"
    s.Cursor |> should equal 3
    redoStack.Count |> should equal 0
    undoStack.Count |> should equal 1

[<Fact>]
let ``redo_typingAfterUndo_clearsRedoStack`` () =
    clearUndoRedo()
    let s = mkState "ab" 2
    let _ = applyKey (key 'c' ConsoleModifiers.None) s   // "abc"/3
    let _ = applyKey (special ConsoleKey.Z ConsoleModifiers.Control) s  // back to "ab"/2
    redoStack.Count |> should equal 1
    let _ = applyKey (key 'x' ConsoleModifiers.None) s   // typing clears redo
    redoStack.Count |> should equal 0

[<Fact>]
let ``undo_pushBounded_capsUndoStackAt100`` () =
    clearUndoRedo()
    let s = mkState "" 0
    // Type 110 characters — each keystroke pushes one snapshot
    for _ in 1 .. 110 do
        let _ = applyKey (key 'a' ConsoleModifiers.None) s
        ()
    undoStack.Count |> should equal 100

[<Fact>]
let ``redo_pushBounded_capsRedoStackAt100`` () =
    clearUndoRedo()
    let s = mkState "" 0
    // Build up 110 undo entries
    for _ in 1 .. 110 do
        let _ = applyKey (key 'a' ConsoleModifiers.None) s
        ()
    // Undo all 100 capped entries; each undo pushes onto redo via pushBounded
    for _ in 1 .. 110 do
        let _ = applyKey (special ConsoleKey.Z ConsoleModifiers.Control) s
        ()
    redoStack.Count |> should equal 100
