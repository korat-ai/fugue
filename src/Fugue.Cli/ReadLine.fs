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

/// Stub for the IO-driven read; will be filled in Task 3.3.
let readAsync (prompt: string) (ct: CancellationToken) : Task<string option> =
    Task.FromResult(None)
