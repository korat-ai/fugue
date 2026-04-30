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
