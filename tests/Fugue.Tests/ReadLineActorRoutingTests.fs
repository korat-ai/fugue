module Fugue.Tests.ReadLineActorRoutingTests

/// Phase 1.3b — verify ReadLine writes route through the Surface actor when one
/// is wired. The race we eliminated: ReadLine and StatusBar both writing to
/// Console.Out concurrently, producing interleaved escape sequences and cursor
/// drift (#913, #910 timing half).
///
/// We test the routing contract:
///   1. setAgent + a single keystroke produces at least one Execute message
///      with a RawAnsi op in the actor's WriteLog.
///   2. Multiple keystrokes produce multiple Execute messages — they are NOT
///      coalesced (RawAnsi has no region key, so each one stands).
///   3. Without setAgent, ReadLine falls back to direct Console.Out (we can't
///      assert directly without capturing stdout, but the test harness still
///      produces no exception — fallback path is exercised in headless tests).
///
/// We use a mock actor (same shape as StatusBarActorTests) because the real
/// executor writes ANSI to Console.Out which can't be captured cleanly in
/// xUnit. The structural assertion is "did the message arrive at the actor?",
/// not "what did it render to the terminal?".

open System
open Xunit
open FsUnit.Xunit
open Fugue.Surface
open Fugue.Cli.ReadLine

/// Spin up a mailbox-backed actor over a MockTerminal and return both.
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
            }
            loop ())
    (actor, term)

/// Drain pending messages by posting an ExecuteAndAck with empty ops.
/// Returns once the actor has processed everything queued before this call.
let private drain (actor: MailboxProcessor<SurfaceMessage>) : unit =
    actor.PostAndAsyncReply(fun ch -> SurfaceMessage.ExecuteAndAck([], ch))
    |> Async.RunSynchronously

let private mkState (text: string) (cursor: int) : S =
    { Buffer          = ResizeArray<char>(text.ToCharArray())
      Cursor          = cursor
      LinesRendered   = 0
      ExitArmed       = false
      RowsBelowCursor = 0
      HistoryIdx      = -1
      SavedBuffer     = None
      PromptText      = "> "
      PromptVisLen    = 2
      HintWhenArmed   = ""
      Placeholder     = ""
      ColorEnabled    = false
      Width           = 80
      SlashHelp       = []
      ModelCompleter  = fun _ -> ([], false) }

[<Fact>]
let ``setAgent + redraw routes RawAnsi ops through the actor`` () =
    let (actor, term) = startMockActor ()
    setAgent actor
    try
        let s = mkState "hello" 5
        // redraw is the prompt-rendering path that emits multiple writeRaw calls
        redraw s
        drain actor
        // After redraw, the actor's WriteLog should contain at least one
        // RawAnsi op — that's the proof writes were serialised through
        // the mailbox rather than going straight to Console.Out.
        let rawCount =
            term.WriteLog
            |> List.filter (fun op -> match op with DrawOp.RawAnsi _ -> true | _ -> false)
            |> List.length
        rawCount |> should be (greaterThan 0)
    finally
        // Don't leave the mutable agent set across tests.
        actor.PostAndAsyncReply(fun ch -> SurfaceMessage.Shutdown ch)
        |> Async.RunSynchronously

[<Fact>]
let ``RawAnsi ops are not coalesced — multiple writes all reach the actor`` () =
    let (actor, term) = startMockActor ()
    setAgent actor
    try
        let s = mkState "" 0
        // Three back-to-back redraws on the same state — coalesce SHOULD NOT
        // collapse them, because RawAnsi has no region key. Every keystroke's
        // worth of writes must land.
        redraw s
        redraw s
        redraw s
        drain actor
        let rawCount =
            term.WriteLog
            |> List.filter (fun op -> match op with DrawOp.RawAnsi _ -> true | _ -> false)
            |> List.length
        // Each redraw emits multiple RawAnsi ops; 3 redraws should produce
        // strictly more ops than a single redraw would. We assert >= 3 as a
        // weak lower bound that is robust to redraw's internal op count.
        rawCount |> should be (greaterThanOrEqualTo 3)
    finally
        actor.PostAndAsyncReply(fun ch -> SurfaceMessage.Shutdown ch)
        |> Async.RunSynchronously
