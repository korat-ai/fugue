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

/// Post a raw ANSI/text string into the actor as a RawAnsi DrawOp.
/// Empty / null strings are dropped (no-op). When the actor is not wired
/// (very early boot, --print mode, headless, tests) we fall back to
/// direct Console.Out.Write + Flush — same shape as the writeRaw fallback
/// that ReadLine had pre-1.3c.
let write (s: string) : unit =
    if String.IsNullOrEmpty s then () else
    match agent with
    | Some a -> a.Post(SurfaceMessage.Execute [ DrawOp.RawAnsi s ])
    | None   -> Console.Out.Write s; Console.Out.Flush ()

/// Write text plus a trailing newline.
let writeLine (s: string) : unit =
    write (s + "\n")

/// Just a newline.
let newline () : unit = write "\n"

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
