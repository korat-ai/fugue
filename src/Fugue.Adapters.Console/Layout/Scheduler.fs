namespace Fugue.Adapters.Console.Layout

open Fugue.Adapters.Console

// =============================================================================
// LayoutScheduler implementation — MailboxProcessor drain-loop (data-model §7).
// =============================================================================

/// Internal messages processed by the scheduler actor.
[<NoComparison; NoEquality>]
type private SchedulerMsg =
    /// Signal that producer inputs have changed. Sets the dirty flag.
    | Trigger
    /// Periodic tick from the System.Threading.Timer. If dirty, invoke
    /// producer and call onFrame.
    | Tick
    /// Graceful shutdown: cancel timer, reply when actor has exited.
    | Stop of AsyncReplyChannel<unit>

/// Internal actor state (mutable, held inside the MailboxProcessor closure).
[<NoComparison; NoEquality>]
type private SchedulerState =
    { mutable PendingTriggers : int
      mutable LastLayout       : Composition option }

[<Sealed>]
type LayoutScheduler private (agent: MailboxProcessor<SchedulerMsg>, timer: System.Threading.Timer) =

    /// True if the scheduler has been stopped (used to drop post-stop triggers).
    let mutable stopped = false

    /// Post a Trigger message. Non-blocking; safe to call from any thread.
    member _.Trigger() =
        if not stopped then
            agent.Post Trigger

    /// Post a Tick message (called from the timer callback on each interval).
    member _.Tick() =
        if not stopped then
            agent.Post Tick

    /// Block until the scheduler has acknowledged shutdown.
    member _.Stop() =
        if not stopped then
            stopped <- true
            // Dispose the timer first so no further Tick messages fire.
            timer.Dispose()
            // Signal the actor and wait for its acknowledgement.
            agent.PostAndReply(fun ch -> Stop ch)

    static member internal Create
            (producer: unit -> Result<Composition, RenderError>)
            (frameIntervalMs: int)
            (onFrame: Composition -> unit) : LayoutScheduler =

        let state = { PendingTriggers = 0; LastLayout = None }

        // The actor is created before the scheduler wrapper so the timer callback
        // can reference the scheduler's .Tick() method. We hold a ref-cell here
        // and fill it after construction.
        let schedulerRef : LayoutScheduler option ref = ref None

        let agent = MailboxProcessor.Start(fun inbox ->
            let rec loop () = async {
                let! msg = inbox.Receive()
                match msg with
                | Trigger ->
                    state.PendingTriggers <- state.PendingTriggers + 1
                    return! loop ()

                | Tick ->
                    if state.PendingTriggers > 0 then
                        state.PendingTriggers <- 0
                        match producer () with
                        | Error _ ->
                            // Silently skip failed frames — caller sees no render.
                            ()
                        | Ok layout ->
                            // Referential-equality short-circuit: skip if same object.
                            let isNew =
                                match state.LastLayout with
                                | None      -> true
                                | Some prev -> not (obj.ReferenceEquals(layout, prev))
                            if isNew then
                                state.LastLayout <- Some layout
                                onFrame layout
                    return! loop ()

                | Stop reply ->
                    // Drain any remaining Trigger/Tick messages (non-blocking).
                    // The timer was already disposed before Stop is posted.
                    reply.Reply()
                    // Exit the loop — actor terminates.
            }
            loop ())

        // The timer fires Tick into the actor every frameIntervalMs.
        // Use System.Threading.Timer: fires on a ThreadPool thread, posts
        // SchedulerMsg.Tick to the actor's inbox (lock-free).
        // dueTime = frameIntervalMs (first fire after one interval, not immediately).
        let timer =
            new System.Threading.Timer(
                System.Threading.TimerCallback(fun _ ->
                    schedulerRef.Value |> Option.iter (fun s -> s.Tick())),
                null,
                frameIntervalMs,
                frameIntervalMs)

        let scheduler = LayoutScheduler(agent, timer)
        schedulerRef.Value <- Some scheduler
        scheduler

module Scheduler =

    let create
            (producer: unit -> Result<Composition, RenderError>)
            (frameIntervalMs: int)
            (onFrame: Composition -> unit) : LayoutScheduler =
        LayoutScheduler.Create producer frameIntervalMs onFrame

    let trigger (s: LayoutScheduler) : unit = s.Trigger()

    let stop (s: LayoutScheduler) : unit = s.Stop()
