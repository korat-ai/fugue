module Fugue.Cli.Surface

open System
open System.IO
open Spectre.Console
open Spectre.Console.Rendering
open Fugue.Surface

// Phase 1.3c v2: every writer in Fugue.Cli funnels through this module so the
// real terminal sees a single, serialised stream of bytes coming from the
// MailboxProcessor actor. No global Console.SetOut — each call-site posts
// explicitly. See feedback memory `feedback_no_global_console_setout` for
// why the global-redirect approach was rejected (#916 → #920 revert).

let mutable private agent : MailboxProcessor<SurfaceMessage> option = None

/// Wire the actor that all writes will be funneled through. Called once at
/// startup from Program.fs runWithCfg, BEFORE any write happens.
let setAgent (a: MailboxProcessor<SurfaceMessage>) : unit =
    agent <- Some a

/// True when the actor has been wired; useful for fallback paths that want
/// to know whether they need to flush themselves.
let isWired () : bool = agent.IsSome

/// Post `ops` through the actor, or run `fallback` against `Console.Out`
/// when no actor is wired (very early boot, --print mode, headless, tests).
/// Centralises the Some/None ceremony so each public helper stays a one-liner.
let inline private postOrFallback (ops: DrawOp list) (fallback: unit -> unit) : unit =
    match agent with
    | Some a -> a.Post(SurfaceMessage.Execute ops)
    | None ->
        fallback ()
        Console.Out.Flush ()

/// Post a raw ANSI/text string. Empty strings drop silently (no zero-length op).
let write (s: string) : unit =
    if String.IsNullOrEmpty s then () else
    postOrFallback [ DrawOp.RawAnsi s ] (fun () -> Console.Out.Write s)

/// Post a single line break — typed `DrawOp.LineBreak`. The executor decides
/// the bytes (today: "\r\n"; tomorrow: whatever the target terminal needs).
let lineBreak () : unit =
    postOrFallback [ DrawOp.LineBreak ] (fun () -> Console.Out.Write "\r\n")

/// Write text plus a trailing line break, atomic in the actor batch.
let writeLine (s: string) : unit =
    if String.IsNullOrEmpty s then lineBreak () else
    postOrFallback
        [ DrawOp.RawAnsi s; DrawOp.LineBreak ]
        (fun () -> Console.Out.Write s; Console.Out.Write "\r\n")

/// Rewind the cursor up `rows` rows AND to column 0 (CR + CUU as one typed op).
/// Used by streaming finalisation to rewind to the start of the streamed
/// region before clearing and re-rendering as Markdown.
let rewindLines (rows: int) : unit =
    let bytes () =
        if rows > 0 then Console.Out.Write $"\r\x1b[{rows}A"
        else Console.Out.Write "\r"
    postOrFallback [ DrawOp.MoveCursorUpToCol0 rows ] bytes

/// Clear from cursor to end of screen (within scroll region if active).
let clearBelow () : unit =
    postOrFallback [ DrawOp.ClearToEndOfScreen ] (fun () -> Console.Out.Write "\x1b[J")

/// Render via Spectre.AnsiConsole into an in-memory buffer, then post the
/// resulting ANSI string through the actor. Each call gets its own
/// AnsiConsole instance (cheap to create) so we don't fight Spectre's static
/// singleton over Console.Out. The instance's Profile.Width is set from the
/// real terminal width so wrapping/tables size correctly.
let spectre (action: IAnsiConsole -> unit) : unit =
    let sw = new StringWriter()
    let settings = AnsiConsoleSettings()
    settings.Out <- AnsiConsoleOutput(sw)
    let console = AnsiConsole.Create settings
    console.Profile.Width <- max 20 Console.WindowWidth
    action console
    write (sw.ToString())

/// Convenience: render Spectre markup as one line ending in \n.
let markupLine (markup: string) : unit =
    spectre (fun ac -> ac.MarkupLine markup)

/// Convenience: write a Spectre IRenderable (panels, padders, gradients)
/// followed by a newline — matches the AnsiConsole.Write + WriteLine pair
/// pattern used in Repl.fs.
let writeRenderableLine (r: IRenderable) : unit =
    spectre (fun ac -> ac.Write r; ac.WriteLine ())

/// Just write a Spectre IRenderable, no trailing newline.
let writeRenderable (r: IRenderable) : unit =
    spectre (fun ac -> ac.Write r)

/// Synchronous barrier: wait until the actor has processed every pending
/// write before returning. Used by code paths that need to be sure visible
/// state has caught up before doing something non-write (e.g. reading
/// keystrokes back). Cheap when the queue is empty.
let flush () : unit =
    match agent with
    | Some a ->
        a.PostAndAsyncReply(fun ch -> SurfaceMessage.ExecuteAndAck([], ch))
        |> Async.RunSynchronously
    | None ->
        Console.Out.Flush ()

/// Post a list of DrawOps as a single Execute message — the entire batch is
/// applied atomically by the executor with no other op interleaved.
/// Use this when a sequence of cursor moves must NOT be split by a spinner /
/// status-bar SaveAndRestore that could land between individual posts (e.g.
/// the streaming-finalise rewind + clear + re-render dance).
let batch (ops: DrawOp list) : unit =
    if List.isEmpty ops then () else
    match agent with
    | Some a -> a.Post(SurfaceMessage.Execute ops)
    | None ->
        // Fallback for headless: render each op directly.  Order matches actor.
        for op in ops do
            match op with
            | DrawOp.RawAnsi s             -> Console.Out.Write s
            | DrawOp.LineBreak             -> Console.Out.Write "\r\n"
            | DrawOp.MoveCursorUp n        -> if n > 0 then Console.Out.Write $"\x1b[{n}A"
            | DrawOp.MoveCursorUpToCol0 n  ->
                if n > 0 then Console.Out.Write $"\r\x1b[{n}A" else Console.Out.Write "\r"
            | DrawOp.ClearToEndOfScreen    -> Console.Out.Write "\x1b[J"
            | _                            -> ()  // structured paint ops irrelevant in fallback
        Console.Out.Flush ()
