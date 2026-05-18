namespace Fugue.Adapters.Console.Layout

open Fugue.Adapters.Console

// =============================================================================
// LayoutScheduler — frame-coalescing update scheduler (data-model.md §7).
//
// Key decisions:
//   * Opaque sealed type — callers interact only via `Scheduler.*` module.
//   * MailboxProcessor drain-loop: `Trigger` posts set a dirty flag;
//     periodic `Tick` (System.Threading.Timer, default 16 ms ≈ 60 Hz) calls
//     the producer at most once per tick and invokes `onFrame` with the result.
//   * Referential-equality short-circuit: if the producer returns the same
//     `Composition` reference as the previous tick, `onFrame` is NOT called
//     (avoids unnecessary renders for unchanged layout trees).
//   * Error path: if the producer returns `Error e`, the tick is silently
//     skipped; the caller sees no render until the next successful tick.
//   * Thread safety: `trigger` posts to the MailboxProcessor inbox — lock-free
//     and non-blocking. The actor serialises all state access.
//   * No Spectre.* type appears in this .fsi (SC-004 invariant).
// =============================================================================

/// Frame-coalescing layout update scheduler.
///
/// Wraps a producer function `(unit -> Result<Composition, RenderError>)`
/// in a MailboxProcessor drain-loop that coalesces rapid `trigger` calls
/// to at most one producer invocation per `frameIntervalMs` tick.
///
/// Constructed only via `Scheduler.create`.
[<Sealed>]
type LayoutScheduler

module Scheduler =

    /// Create a `LayoutScheduler` for the given producer and frame interval.
    ///
    /// - `producer`: pure function (or closure over caller state) that rebuilds
    ///   the entire layout tree. Called at most once per `frameIntervalMs`.
    /// - `frameIntervalMs`: tick interval in milliseconds. Recommended: `16`
    ///   (≈ 60 Hz). Tests may use a larger value (e.g. `500`) to avoid timing
    ///   sensitivity.
    /// - `onFrame`: callback invoked with each successfully produced `Composition`.
    ///   In REPL use this callback calls `Renderer.toRawAnsi` and posts
    ///   `DrawOp.RawAnsi` to the Surface actor. Not called when `producer` returns
    ///   `Error` or when the produced `Composition` is referentially equal to the
    ///   previous tick's result.
    ///
    /// The scheduler starts immediately. Call `Scheduler.stop` to shut it down.
    val create :
        producer: (unit -> Result<Composition, RenderError>) ->
            frameIntervalMs: int ->
            onFrame: (Composition -> unit) ->
            LayoutScheduler

    /// Signal that the producer's inputs have changed.
    ///
    /// The next `Tick` will invoke the producer and (if successful and
    /// referentially new) call `onFrame`. Multiple `trigger` calls within
    /// one `frameIntervalMs` window are coalesced — only one producer
    /// invocation happens per tick. Thread-safe (posts to the MailboxProcessor
    /// inbox without blocking). After `stop`, triggers are silently ignored.
    val trigger : LayoutScheduler -> unit

    /// Stop the scheduler, cancelling the timer and draining the inbox.
    ///
    /// After `stop`, subsequent `trigger` calls are silently ignored.
    /// `stop` is idempotent — calling it multiple times is safe.
    /// Blocks until the actor has acknowledged shutdown.
    val stop : LayoutScheduler -> unit
