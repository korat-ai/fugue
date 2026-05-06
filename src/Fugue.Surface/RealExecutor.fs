namespace Fugue.Surface

open System

/// Messages processed by the RealExecutor actor.
[<RequireQualifiedAccess>]
type SurfaceMessage =
    /// Normal-priority draw operations (streaming tokens, timer spinner, log lines).
    | Execute             of DrawOp list
    /// High-priority draw (user keystroke echo, modal acknowledgements).
    /// The actor processes these before any pending Execute messages.
    | ExecuteHighPriority of DrawOp list
    /// Synchronous barrier: apply ops, then notify caller via the reply channel.
    | ExecuteAndAck       of DrawOp list * AsyncReplyChannel<unit>
    /// Terminal resize notification.
    /// Phase 0 stub — the actor records the new size but takes no further action.
    /// A future phase will recompute region offsets and trigger a full redraw.
    | Resize              of width: int * height: int
    /// Graceful shutdown: reset scroll region, flush, reply when done.
    | Shutdown            of AsyncReplyChannel<unit>

module RealExecutor =

    // -----------------------------------------------------------------------
    // ANSI helpers
    // -----------------------------------------------------------------------

    /// Original Console.Out captured at module load. Used by `write` so the
    /// actor's own output bypasses any `ActorWriter` redirect that gets
    /// installed later (which would otherwise post the actor's writes back
    /// to itself, growing the mailbox without bound).
    ///
    /// This value is captured the FIRST time the module is touched. Program.fs
    /// must call `RealExecutor.start ()` BEFORE redirecting Console.Out, which
    /// is the natural order anyway (you need the actor before you can build
    /// an ActorWriter that wraps it).
    let private originalStdout : System.IO.TextWriter = Console.Out

    let private write (s: string) =
        originalStdout.Write s
        originalStdout.Flush ()

    let private termWidth  () = Console.WindowWidth
    let private termHeight () = Console.WindowHeight

    /// Translate a Region to a 1-indexed terminal row (ANSI convention).
    let private ansiRow (region: Region) : int option =
        let h = termHeight ()
        match region with
        | Region.ThinkingLine   -> Some (h - 2)
        | Region.StatusBarLine1 -> Some (h - 1)
        | Region.StatusBarLine2 -> Some h
        | Region.InputArea r    -> Some (max 1 (h - 3 - r))
        | Region.ScrollContent  -> None
        | Region.Modal          -> Some 1

    let private styleEscape (s: Style) : string =
        let color =
            match s.Color with
            | Color.Default    -> ""
            | Color.Dim        -> "\x1b[90m"
            | Color.Theme name ->
                // Phase 0: theme color is stubbed; Phase 1 will wire active-theme lookup.
                match name with
                | "accent"  -> "\x1b[36m"
                | "warning" -> "\x1b[33m"
                | "error"   -> "\x1b[31m"
                | _         -> ""
        let bold   = if s.Bold   then "\x1b[1m" else ""
        let italic = if s.Italic then "\x1b[3m" else ""
        bold + italic + color

    let private resetEscape = "\x1b[0m"

    // -----------------------------------------------------------------------
    // Drain-and-coalesce: collect all pending Execute messages from the inbox
    // and merge them, keeping the last op per region (dedup spinner frames).
    // -----------------------------------------------------------------------

    /// Collect all currently queued Execute messages (non-blocking peek).
    /// High-priority and Ack messages are NOT consumed here — they stay queued.
    let private drainExecute (inbox: MailboxProcessor<SurfaceMessage>) : DrawOp list list =
        let rec loop acc =
            if inbox.CurrentQueueLength = 0 then List.rev acc
            else
                // TryReceive(0) = non-blocking; peek only Execute messages
                match inbox.TryReceive 0 |> Async.RunSynchronously with
                | None -> List.rev acc
                | Some (SurfaceMessage.Execute ops) -> loop (ops :: acc)
                | Some other ->
                    // Non-Execute message: post it back so the main loop sees it
                    inbox.Post other
                    List.rev acc
        loop []

    /// Coalesce a list of op-batches: for ops targeting the same region,
    /// keep only the last Paint/EraseLine per region (reduces flicker on
    /// rapid spinner updates). Append/ResetScrollRegion are always kept.
    let private coalesce (batches: DrawOp list list) : DrawOp list =
        let combined = batches |> List.concat
        // Simple pass: build a list preserving order but replacing earlier
        // region-keyed ops with later ones.
        let regionKey (op: DrawOp) =
            match op with
            | DrawOp.Paint(r, _, _) -> Some r
            | DrawOp.EraseLine r    -> Some r
            | DrawOp.MoveTo(r, _)   -> Some r
            | _                     -> None
        // Walk in reverse to find the last op per region key; then reverse back.
        let seen = System.Collections.Generic.HashSet<Region>()
        combined
        |> List.rev
        |> List.filter (fun op ->
            match regionKey op with
            | None   -> true           // always keep region-less ops
            | Some r -> seen.Add r)    // keep only the FIRST in reversed order = LAST in original
        |> List.rev

    // -----------------------------------------------------------------------
    // Apply ops to Console.Out
    // -----------------------------------------------------------------------

    let private applyOps (ops: DrawOp list) : unit =
        let rec applyOne (op: DrawOp) =
            match op with
            | DrawOp.Paint(region, text, style) ->
                let prefix = styleEscape style
                let suffix = if prefix <> "" then resetEscape else ""
                match ansiRow region with
                | Some row ->
                    // Move to row, erase line, write styled text, reset.
                    write $"\x1b[{row};1H\x1b[2K{prefix}{text}{suffix}"
                | None ->
                    // ScrollContent: write inline without repositioning.
                    write $"{prefix}{text}{suffix}"

            | DrawOp.EraseLine region ->
                match ansiRow region with
                | Some row -> write $"\x1b[{row};1H\x1b[2K"
                | None     -> write "\x1b[2K"

            | DrawOp.MoveTo(region, col) ->
                let col1 = col + 1  // convert 0-based to 1-based ANSI col
                match ansiRow region with
                | Some row -> write $"\x1b[{row};{col1}H"
                | None     ->
                    // ScrollContent: move column only (relative)
                    write $"\x1b[{col1}G"

            | DrawOp.SaveAndRestore inner ->
                write "\x1b[s"
                for innerOp in inner do applyOne innerOp
                write "\x1b[u"

            | DrawOp.Append text ->
                write text
                write "\r\n"

            | DrawOp.ResetScrollRegion ->
                let h = termHeight ()
                let w = termWidth  ()
                // Reset scroll region to full screen, move cursor to bottom.
                write $"\x1b[1;{h}r\x1b[{h};{w}H"

            | DrawOp.LineBreak ->
                // Always emit CR+LF so the cursor lands at col 0 — regardless
                // of `onlcr` tty mode, scroll region state, or bracketed-paste.
                write "\r\n"

            | DrawOp.MoveCursorUp rows ->
                if rows > 0 then write $"\x1b[{rows}A"

            | DrawOp.MoveCursorUpToCol0 rows ->
                // CR brings col to 0, then CUU moves up. Single batched write
                // keeps it atomic against any other op the actor might apply
                // (heartbeat / spinner) — those run between `applyOne` calls,
                // not within one.
                if rows > 0 then write $"\r\x1b[{rows}A"
                else write "\r"

            | DrawOp.ClearToEndOfScreen ->
                write "\x1b[J"

            | DrawOp.RawAnsi text ->
                // Caller pre-built a compound ANSI sequence (typically ReadLine
                // redrawing the prompt + buffer + cursor). Write as-is.
                write text
        for op in ops do applyOne op

    // -----------------------------------------------------------------------
    // Actor loop
    // -----------------------------------------------------------------------

    let start () : MailboxProcessor<SurfaceMessage> =
        let mutable _width  = 80
        let mutable _height = 24

        MailboxProcessor.Start(fun inbox ->
            let rec loop () = async {
                let! msg = inbox.Receive()

                match msg with
                | SurfaceMessage.Execute ops ->
                    // Before applying a normal-priority batch, check if any
                    // high-priority message is waiting — if so, process it first.
                    let! hpMsg =
                        inbox.TryScan(
                            (fun m ->
                                match m with
                                | SurfaceMessage.ExecuteHighPriority hp ->
                                    Some (async { return hp })
                                | _ -> None),
                            0)
                    match hpMsg with
                    | Some hpOps ->
                        applyOps hpOps
                    | None -> ()

                    // Now drain any additional pending Execute batches and coalesce.
                    let pending = drainExecute inbox
                    let all     = coalesce (ops :: pending)
                    // try/catch: a Console.Out write throwing (e.g. terminal
                    // detached, IO closed) MUST NOT kill the actor — that would
                    // cause subsequent ExecuteAndAck / Shutdown to hang their
                    // PostAndAsyncReply callers, manifesting as the user
                    // having to SIGINT-kill fugue (#918).
                    try applyOps all with _ -> ()
                    return! loop ()

                | SurfaceMessage.ExecuteHighPriority ops ->
                    try applyOps ops with _ -> ()
                    return! loop ()

                | SurfaceMessage.ExecuteAndAck(ops, reply) ->
                    try applyOps ops with _ -> ()
                    // ALWAYS reply — even on exception. Caller is blocked on
                    // PostAndAsyncReply; never not-replying is not an option.
                    reply.Reply ()
                    return! loop ()

                | SurfaceMessage.Resize(w, h) ->
                    // Phase 0 stub: record size for future region-offset recomputation.
                    // A future phase will trigger a full redraw here.
                    _width  <- w
                    _height <- h
                    return! loop ()

                | SurfaceMessage.Shutdown reply ->
                    try applyOps [ DrawOp.ResetScrollRegion ] with _ -> ()
                    // ALWAYS reply — see comment on ExecuteAndAck above.
                    reply.Reply ()
                    // Loop exits — agent terminates.
            }
            loop ()
        )
