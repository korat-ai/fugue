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

open System
open System.Threading
open Xunit
open FsUnit.Xunit
open Fugue.Cli.StatusBar

[<Fact>]
let ``startHeartbeat fires callback at configured period`` () =
    let counter = ref 0
    let originalPeriod = heartbeatPeriodMs
    heartbeatPeriodMs <- 50
    try
        startHeartbeat (fun () -> Interlocked.Increment(counter) |> ignore)
        // Sleep long enough for at least 4 ticks (50ms × 4 = 200ms; give 300ms buffer).
        Thread.Sleep 300
        stopHeartbeat ()
        // Expect at least 3 ticks; allow CI jitter to drop one.
        !counter |> should be (greaterThanOrEqualTo 3)
    finally
        heartbeatPeriodMs <- originalPeriod
        stopHeartbeat ()  // idempotent — safe even if already stopped

[<Fact>]
let ``stopHeartbeat halts further callback fires`` () =
    let counter = ref 0
    let originalPeriod = heartbeatPeriodMs
    heartbeatPeriodMs <- 30
    try
        startHeartbeat (fun () -> Interlocked.Increment(counter) |> ignore)
        Thread.Sleep 150
        stopHeartbeat ()
        let snapshot = !counter
        // After stop, no more increments should happen.
        Thread.Sleep 200
        !counter |> should equal snapshot
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
    let counter = ref 0
    let originalPeriod = heartbeatPeriodMs
    heartbeatPeriodMs <- 40
    try
        startHeartbeat (fun () ->
            let n = Interlocked.Increment(counter)
            if n = 1 then failwith "boom — first tick throws on purpose")
        // Even though tick 1 throws, ticks 2..N must still happen.
        Thread.Sleep 250
        stopHeartbeat ()
        !counter |> should be (greaterThanOrEqualTo 3)
    finally
        heartbeatPeriodMs <- originalPeriod
        stopHeartbeat ()
