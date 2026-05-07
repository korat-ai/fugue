module Fugue.Tests.StatusBarHeartbeatTests

/// Phase 1.3a — heartbeat timer integration tests.
///
/// The status-bar heartbeat repaints periodically so timer / branch / cwd
/// indicators don't go stale between explicit events. These tests verify:
///   1. `startHeartbeat` actually fires the callback at the configured period.
///   2. `stopHeartbeat` disposes the timer (no further fires after stop).
///   3. Calling `stopHeartbeat` without an active timer is a no-op.
///
/// We test the heartbeat helpers directly rather than through `StatusBar.start`
/// because `start` early-returns under non-TTY stdout (which is true in CI),
/// so end-to-end coverage of the start path requires real TTY anyway and is
/// covered by manual smoke tests.
///
/// Thread.Sleep has been replaced with CountdownEvent to avoid timing flakiness:
/// each test waits for an exact number of ticks rather than sleeping a fixed
/// wall-clock interval.

open System
open System.Threading
open Xunit
open FsUnit.Xunit
open Fugue.Cli.StatusBar

[<Fact>]
let ``startHeartbeat fires callback at configured period`` () =
    let ticksNeeded = 3
    use latch = new CountdownEvent(ticksNeeded)
    let originalPeriod = heartbeatPeriodMs
    heartbeatPeriodMs <- 50
    try
        startHeartbeat (fun () ->
            if not latch.IsSet then latch.Signal() |> ignore)
        let signalled = latch.Wait(TimeSpan.FromSeconds 5.)
        stopHeartbeat ()
        signalled |> should equal true
    finally
        heartbeatPeriodMs <- originalPeriod
        stopHeartbeat ()

[<Fact>]
let ``stopHeartbeat halts further callback fires`` () =
    use latch = new CountdownEvent(2)
    let originalPeriod = heartbeatPeriodMs
    heartbeatPeriodMs <- 30
    try
        startHeartbeat (fun () ->
            if not latch.IsSet then latch.Signal() |> ignore)
        // Wait for at least 2 ticks to confirm timer is running.
        let started = latch.Wait(TimeSpan.FromSeconds 5.)
        stopHeartbeat ()
        let counter = ref 0
        // Count any further ticks after stop (we expect none).
        use afterLatch = new CountdownEvent(1)
        startHeartbeat (fun () -> Interlocked.Increment(counter) |> ignore)
        stopHeartbeat ()
        // Give a small window (150 ms) for any spurious fire; should not happen.
        afterLatch.Wait(TimeSpan.FromMilliseconds 150.) |> ignore
        started |> should equal true
        !counter |> should equal 0
    finally
        heartbeatPeriodMs <- originalPeriod
        stopHeartbeat ()

[<Fact>]
let ``stopHeartbeat is a no-op when no timer is active`` () =
    // Should not throw or block.
    stopHeartbeat ()
    stopHeartbeat ()  // idempotent
    // Reaching here = pass.
    true |> should equal true

[<Fact>]
let ``heartbeat callback exceptions don't kill the timer`` () =
    // First tick throws; subsequent ticks must still fire.
    let ticksAfterFirst = 2
    use latch = new CountdownEvent(ticksAfterFirst)
    let counter = ref 0
    let originalPeriod = heartbeatPeriodMs
    heartbeatPeriodMs <- 40
    try
        startHeartbeat (fun () ->
            let n = Interlocked.Increment(counter)
            if n = 1 then failwith "boom — first tick throws on purpose"
            else if not latch.IsSet then latch.Signal() |> ignore)
        let signalled = latch.Wait(TimeSpan.FromSeconds 5.)
        stopHeartbeat ()
        signalled |> should equal true
        !counter |> should be (greaterThanOrEqualTo 3)
    finally
        heartbeatPeriodMs <- originalPeriod
        stopHeartbeat ()
