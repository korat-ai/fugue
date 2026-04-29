module Fugue.Tests.ReadLineTests

open System
open System.Collections.Generic
open Xunit
open FsUnit.Xunit
open Fugue.Cli.ReadLine

let private mkState (text: string) (cursor: int) : S =
    { Buffer        = ResizeArray<char>(text.ToCharArray())
      Cursor        = cursor
      LinesRendered = 0
      PromptText    = "> "
      PromptVisLen  = 2
      Width         = 80 }

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
