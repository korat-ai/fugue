module Fugue.Cli.Surface

open System
open System.IO
open Fugue.Adapters.Console
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

/// Render a Composition via the adapter into an in-memory ANSI byte sequence,
/// then post the resulting string through the actor. Terminal width is probed
/// from the live console on each call (SIGWINCH-aware). Errors are logged to
/// stderr and silently dropped rather than crashing the render pipeline.
let private renderComposition (comp: Composition) : unit =
    let ctx = RenderContext.probe ()
    match Renderer.toRawAnsi ctx comp with
    | Ok ansi -> write ansi
    | Error e ->
        Console.Error.WriteLine $"[surface render error: {e}]"
        Console.Error.Flush ()

/// Convenience: render a markup hint string (Fugue adapter markup syntax) as
/// one line ending in a newline. The markup hint is NOT user-supplied content —
/// call-sites assert it is already balanced markup (FR-011 gate).
let markupLine (markup: string) : unit =
    let comp = Composition.Leaf (Primitive.Markup markup)
    renderComposition comp
    lineBreak ()

/// Convenience: render an opaque adapter Renderable (wrapping a Spectre widget —
/// panels, padders, gradients, tables, etc.) followed by a newline. Callers
/// obtain a Renderable by wrapping the widget via Renderable.fromSpectre.
/// This keeps Spectre types out of Surface.fs and Repl.fs (FR-011 gate).
let writeRenderableLine (r: Renderable) : unit =
    let comp = Composition.ofRenderable r
    renderComposition comp
    lineBreak ()

/// Render an opaque adapter Renderable without a trailing newline. See
/// writeRenderableLine for the Renderable.fromSpectre wrapping convention.
let writeRenderable (r: Renderable) : unit =
    let comp = Composition.ofRenderable r
    renderComposition comp

/// Escape markup characters in a plain string so it can be safely embedded
/// in a markup hint without interpretation. Wraps the string via SafeText.ofUser
/// (which applies Markup.Escape once). Callers in Repl.fs do not need to open
/// any Spectre namespace (FR-011 gate).
let escape (s: string) : string =
    SafeText.ofUser s |> SafeText.unwrap

/// Write a markup hint string without a trailing newline. The hint is NOT
/// user-supplied content — call-sites assert it is already balanced markup.
/// Callers do not need to open any Spectre namespace (FR-011 gate).
let markup (markupStr: string) : unit =
    let comp = Composition.Leaf (Primitive.Markup markupStr)
    renderComposition comp

/// Render a rounded-border Table from markup column-headers and markup rows.
/// Column alignment is specified per header: alignment is "left" | "center" |
/// "right". Delegates to RoundedMarkupTable.toComposition (Fugue.Adapters.Console)
/// so no Spectre types appear in Surface.fs (FR-011 gate).
///
/// columnDefs: list of (markupHeader, alignment). Each row is a string[] of cells.
let roundedTable (columnDefs: (string * string) list) (rows: string[] list) : unit =
    match RoundedMarkupTable.toComposition columnDefs rows with
    | Ok comp  -> renderComposition comp
    | Error e  ->
        Console.Error.WriteLine $"[roundedTable error: {e}]"
        Console.Error.Flush ()

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
