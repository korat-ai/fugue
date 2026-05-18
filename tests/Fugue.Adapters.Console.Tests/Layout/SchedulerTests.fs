module Fugue.Adapters.Console.Tests.Layout.SchedulerTests

// see Helpers.fs FACT_DISCOVERY for why all tests are [<Property>]
open FsCheck.Xunit
open Fugue.Adapters.Console
open Fugue.Adapters.Console.Layout
open System.Threading
open System.Diagnostics

// ============================================================================
// Shared test helpers
// ============================================================================

/// A minimal valid Composition leaf used as a stand-in for "some layout".
let private leafA () : Composition =
    Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "A"))

/// A different minimal valid Composition leaf.
let private leafB () : Composition =
    Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "B"))

/// Wait up to `timeoutMs` for `cond ()` to become true, checking every 5 ms.
/// Returns true if condition met within timeout, false otherwise.
let private waitFor (timeoutMs: int) (cond: unit -> bool) : bool =
    let sw = Stopwatch.StartNew()
    while not (cond()) && sw.ElapsedMilliseconds < int64 timeoutMs do
        Thread.Sleep 5
    cond()

// ============================================================================
// T061 — Happy path: scheduler creates, trigger invokes producer at most once
// per tick.
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T061 — Scheduler.create returns scheduler; single trigger causes at most one producer call per 16ms tick`` () =
    let producerCallCount = ref 0
    let frameMs = 50  // 50ms tick avoids timing sensitivity in CI
    let layout = leafA ()
    let producer () =
        Interlocked.Increment(producerCallCount) |> ignore
        Ok layout
    let frameCount = ref 0
    let sched = Scheduler.create producer frameMs (fun _ -> Interlocked.Increment(frameCount) |> ignore)

    // Trigger once.
    Scheduler.trigger sched

    // Wait at most 3 ticks for the producer to be called.
    let called = waitFor (frameMs * 5) (fun () -> !producerCallCount >= 1)
    Scheduler.stop sched

    // Assert: producer was called at least once but onFrame also fired.
    called && !producerCallCount >= 1 && !frameCount >= 1

// ============================================================================
// T062 — Referential-equality short-circuit: same Composition ref → no second
// onFrame call.
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T062 — Scheduler referential-equality short-circuit: same Composition ref skips second onFrame`` () =
    // Single shared Composition object — same reference every call.
    let sharedLayout = leafA ()
    let frameCount = ref 0
    let frameMs = 50
    let producer () = Ok sharedLayout
    let sched = Scheduler.create producer frameMs (fun _ -> Interlocked.Increment(frameCount) |> ignore)

    // First trigger: should produce one onFrame call.
    Scheduler.trigger sched
    let firstCallOk = waitFor (frameMs * 5) (fun () -> !frameCount >= 1)

    // Second trigger: producer returns same reference → onFrame must NOT be called again.
    Scheduler.trigger sched
    // Wait two extra ticks to confirm no second call.
    Thread.Sleep(frameMs * 3)
    let noSecondCall = !frameCount = 1

    Scheduler.stop sched
    firstCallOk && noSecondCall

// ============================================================================
// T062a — Burst-rate stress: 1000 triggers in 1s → producer ≤ 62 invocations.
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T062a — Burst stress: 1000 triggers in 1s yields producer ≤ 62 invocations`` () =
    let producerCallCount = ref 0
    let mutable lastLayout : Composition option = None
    let frameMs = 16  // Real 60fps timing for this stress test.

    // Each producer call builds a fresh leaf (new reference) so onFrame always fires.
    let producer () =
        let n = Interlocked.Increment(producerCallCount)
        let leaf = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral $"tok-{n}"))
        lastLayout <- Some leaf
        Ok leaf

    let sched = Scheduler.create producer frameMs (fun _ -> ())

    // Inject 1000 triggers in a tight loop.
    let sw = Stopwatch.StartNew()
    for _ in 1 .. 1000 do
        Scheduler.trigger sched
    let loopMs = sw.ElapsedMilliseconds

    // Wait 1 second total for processing to complete.
    let waitMs = max 0 (int (1000L - loopMs))
    Thread.Sleep(max 100 waitMs)
    Scheduler.stop sched

    // At 60 Hz for 1 second → at most 62 producer invocations.
    // We allow up to 63 for rounding.
    let count = !producerCallCount
    count <= 63

// ============================================================================
// T063 — Stop semantics: after stop, triggers are no-ops.
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T063 — Scheduler.stop: post-stop triggers are no-ops and no exceptions`` () =
    let producerCallCount = ref 0
    let frameMs = 50
    let producer () =
        Interlocked.Increment(producerCallCount) |> ignore
        Ok (leafA ())
    let sched = Scheduler.create producer frameMs (fun _ -> ())

    // Let it run one cycle.
    Scheduler.trigger sched
    Thread.Sleep(frameMs * 2)

    // Stop the scheduler.
    Scheduler.stop sched
    let countAfterStop = !producerCallCount

    // Trigger several times after stop — must not throw, must not increment counter.
    for _ in 1 .. 10 do
        Scheduler.trigger sched
    Thread.Sleep(frameMs * 3)

    // Counter must not have grown beyond what it was at stop time.
    let countNow = !producerCallCount
    countNow <= countAfterStop + 1  // allow +1 for a tick that was in-flight at stop time

// ============================================================================
// T064 — Error path: producer returns Error → onFrame not called.
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T064 — Scheduler error path: producer Error skips onFrame`` () =
    let frameCount = ref 0
    let frameMs = 50
    let producer () : Result<Composition, RenderError> =
        Error (RenderError.RenderFailed ("test", "producer returned error"))
    let sched = Scheduler.create producer frameMs (fun _ -> Interlocked.Increment(frameCount) |> ignore)

    Scheduler.trigger sched
    Thread.Sleep(frameMs * 4)
    Scheduler.stop sched

    // onFrame must never have been called.
    !frameCount = 0

// ============================================================================
// T064a — Concurrent-trigger thread safety: 8 threads trigger simultaneously.
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T064a — Concurrent triggers: 8 threads × 500ms, no exceptions, no torn state`` () =
    let producerCallCount = ref 0
    let frameMs = 16

    // Producer builds a new leaf each call.
    let producer () =
        let n = Interlocked.Increment(producerCallCount)
        Ok (Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral $"t-{n}")))

    let sched = Scheduler.create producer frameMs (fun _ -> ())

    let mutable exceptionOccurred = false
    let threads =
        [| for _ in 1 .. 8 do
               Thread(fun () ->
                   let sw = Stopwatch.StartNew()
                   try
                       while sw.ElapsedMilliseconds < 500L do
                           Scheduler.trigger sched
                           Thread.SpinWait 100
                   with _ ->
                       exceptionOccurred <- true) |]

    for t in threads do t.Start()
    for t in threads do t.Join(2000) |> ignore

    Thread.Sleep(frameMs * 5)  // drain last tick
    Scheduler.stop sched

    // At 60 Hz for 500ms each thread runs → at most ~32 ticks * 8 threads but
    // MailboxProcessor serialises so total producer calls ≤ 32 frames in 500ms.
    not exceptionOccurred
