module Fugue.Tests.StatusBarActorTests

/// PR C — MailboxProcessor actor integration tests.
///
/// These tests verify that the Surface actor:
///   1. Serialises concurrent posts (fire-and-forget Execute from multiple threads)
///      without crashing or losing messages.
///   2. ExecuteAndAck provides a synchronous barrier — the reply only arrives
///      after the ops have been processed.
///   3. The actor shuts down cleanly via Shutdown even when the queue is non-empty.
///
/// We use MockExecutor (in-memory) rather than RealExecutor so tests are
/// TTY-free and CI-safe.  The structural assertions are on the actor's
/// message-processing semantics, not on terminal escape sequences.

open System
open System.Threading
open Xunit
open FsUnit.Xunit
open Fugue.Surface

// ---------------------------------------------------------------------------
// Helpers: a minimal MailboxProcessor-based actor using MockExecutor
// ---------------------------------------------------------------------------

/// Creates a test actor backed by a MockTerminal (120×30).
/// Returns (actor, term) — term is mutated by the actor when it processes ops.
let private startMockActor () : MailboxProcessor<SurfaceMessage> * MockTerminal =
    let term = MockExecutor.create 120 30
    let actor =
        MailboxProcessor.Start(fun inbox ->
            let rec loop () = async {
                let! msg = inbox.Receive()
                match msg with
                | SurfaceMessage.Execute ops ->
                    MockExecutor.execute ops term |> ignore
                    return! loop ()
                | SurfaceMessage.ExecuteHighPriority ops ->
                    MockExecutor.execute ops term |> ignore
                    return! loop ()
                | SurfaceMessage.ExecuteAndAck(ops, reply) ->
                    MockExecutor.execute ops term |> ignore
                    reply.Reply()
                    return! loop ()
                | SurfaceMessage.Resize _ ->
                    return! loop ()
                | SurfaceMessage.Shutdown reply ->
                    reply.Reply()
                // loop exits — actor terminates
            }
            loop ())
    (actor, term)

// ---------------------------------------------------------------------------
// Test 1. Fire-and-forget Execute: all messages eventually processed
// ---------------------------------------------------------------------------

[<Fact>]
let ``Execute ops from multiple threads are all processed via actor`` () =
    let (actor, term) = startMockActor ()
    let count = 20
    // Simulate timer-thread + main-thread posts.
    let threads =
        Array.init count (fun _ ->
            Thread(fun () ->
                let ops = [ DrawOp.Append "tick" ]
                actor.Post(SurfaceMessage.Execute ops)))
    for t in threads do t.Start()
    for t in threads do t.Join()
    // Drain via ExecuteAndAck — waits for all queued Execute to finish.
    actor.PostAndAsyncReply(fun ch -> SurfaceMessage.ExecuteAndAck([], ch))
    |> Async.RunSynchronously
    // WriteLog must have ≥ count Append ops (may also have the empty ack op).
    let appendCount =
        term.WriteLog
        |> List.filter (fun op -> match op with DrawOp.Append _ -> true | _ -> false)
        |> List.length
    appendCount |> should be (greaterThanOrEqualTo count)

// ---------------------------------------------------------------------------
// Test 2. ExecuteAndAck provides synchronous barrier
// ---------------------------------------------------------------------------

[<Fact>]
let ``ExecuteAndAck reply arrives only after ops are applied`` () =
    let (actor, term) = startMockActor ()
    let ops = [
        DrawOp.Paint(Region.StatusBarLine1, "barrier-test", Style.normal)
    ]
    // PostAndAsyncReply blocks until reply.Reply() is called, which happens
    // only after MockExecutor.execute ops term has run.
    actor.PostAndAsyncReply(fun ch -> SurfaceMessage.ExecuteAndAck(ops, ch))
    |> Async.RunSynchronously
    // After the ack we can safely inspect term.
    // StatusBarLine1 → row (height-2) = 28 in a 30-row terminal.
    let line = MockExecutor.regionContent Region.StatusBarLine1 term
    line |> should haveSubstring "barrier-test"

// ---------------------------------------------------------------------------
// Test 3. Shutdown drains and terminates cleanly
// ---------------------------------------------------------------------------

[<Fact>]
let ``actor shuts down cleanly after Shutdown message`` () =
    let (actor, _term) = startMockActor ()
    // Post a few normal messages then shut down.
    for _ in 1..5 do
        actor.Post(SurfaceMessage.Execute [ DrawOp.Append "pre-shutdown" ])
    // Shutdown waits for reply — reply only comes after the loop exits.
    let timeout = TimeSpan.FromSeconds 3.0
    let task = actor.PostAndAsyncReply(fun ch -> SurfaceMessage.Shutdown ch) |> Async.StartAsTask
    let completed = task.Wait(timeout)
    completed |> should equal true
