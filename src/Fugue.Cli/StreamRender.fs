module Fugue.Cli.StreamRender

open System
open System.Threading.Tasks
open Spectre.Console.Rendering

// Layer 3 (domain UI): terminal-rendering concerns for assistant streaming
// turns are localised here, isolated from Repl business logic. When a
// rendering bug appears (cursor off-by-one, scroll region misbehaving,
// markdown re-render colliding with raw text), the fix lives in this
// module — Repl.fs stays "did streaming start? did the agent yield text?
// is there a final form?" and never touches DrawOps.
//
// All terminal output flows through the Surface actor (layer 2) which
// posts typed DrawOps (layer 1, RealExecutor.applyOne maps them to
// concrete ANSI escape sequences). No raw "\r\n" / "\x1b[…" strings
// in this module.

/// Compute how many terminal rows of vertical space a piece of streamed
/// raw text occupied, given the current terminal width. Lines wider than
/// `termW` count with their wrap factor.  Trailing CR/LF are trimmed so
/// the returned count matches what's *visible* — not what's in the buffer.
let private occupiedRows (text: string) (termW: int) : int =
    text.TrimEnd([|'\n'; '\r'|]).Split('\n')
    |> Array.sumBy (fun line ->
        let l = line.Length
        if l = 0 then 1 else (l + termW - 1) / termW)

/// Finalise an assistant streaming turn:
///
/// - **`final = None`** (or terminal has no colour): emit a trailing line
///   break so the raw streamed text stays visible. Nothing to rewind.
///
/// - **`final = Some r`**: rewind cursor to the row where streaming started,
///   clear from there to end of screen, then render `r` (typically Markdown
///   built by `Render.assistantFinal text`).
///
/// - **`typewriter = true`**: capture the IRenderable to ANSI lines and
///   write them with a 12ms async delay between rows.  Otherwise render
///   in one shot.
///
/// Sequence (the +1 in the move-up accounts for the line break we just
/// emitted; without it the rewind lands inside the streamed text and the
/// re-render concatenates onto the existing line — the duplicated-text
/// bug we hit before this module existed):
///   LineBreak → MoveCursorUpToCol0 (rawLines + 1) → ClearToEndOfScreen
///   → write final → LineBreak
let finalizeTurn
    (rawText: string)
    (final: IRenderable option)
    (typewriter: bool)
    : Task<unit>
    = task {
        Surface.lineBreak ()
        match final with
        | None -> ()
        | Some r ->
            let termW = max 20 Console.WindowWidth
            let rawLines = occupiedRows rawText termW
            if rawLines > 0 then
                Surface.rewindLines (rawLines + 1)
                Surface.clearBelow ()
            if typewriter then
                let lines = Render.captureLines r
                for line in lines do
                    Surface.writeLine line
                    do! Task.Delay 12
            else
                Surface.writeRenderable r
                Surface.lineBreak ()
    }
