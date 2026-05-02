module Fugue.Tests.ActorWriterTests

/// Phase 1.3c — verify ActorWriter funnels Console writes through the actor.
///
/// Architectural property under test:
///   Once `Console.SetOut(new ActorWriter(actor))` is called, EVERY downstream
///   write (Spectre, Markdig, Console.WriteLine, raw Console.Write of byte
///   sequences) lands in the actor's WriteLog as a `RawAnsi` op — no path
///   bypasses the actor, no path overrides the redirect.
///
/// We exercise the writer directly (not via Console.SetOut, which would clobber
/// the test runner's stdout) by calling its Write methods. The contract proven
/// is the same: each Write() call → one Execute message with one RawAnsi op.

open System
open Xunit
open FsUnit.Xunit
open Fugue.Surface

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
                | SurfaceMessage.Resize _ -> return! loop ()
                | SurfaceMessage.Shutdown reply -> reply.Reply()
            }
            loop ())
    (actor, term)

/// Block until the actor has processed everything queued so far.
let private drain (actor: MailboxProcessor<SurfaceMessage>) =
    actor.PostAndAsyncReply(fun ch -> SurfaceMessage.ExecuteAndAck([], ch))
    |> Async.RunSynchronously

let private rawAnsiCount (term: MockTerminal) : int =
    term.WriteLog
    |> List.filter (fun op -> match op with DrawOp.RawAnsi _ -> true | _ -> false)
    |> List.length

let private rawAnsiTexts (term: MockTerminal) : string list =
    term.WriteLog
    |> List.choose (fun op -> match op with DrawOp.RawAnsi t -> Some t | _ -> None)

[<Fact>]
let ``Write(string) posts one Execute with RawAnsi to the actor`` () =
    let (actor, term) = startMockActor ()
    use writer = new ActorWriter(actor)
    writer.Write("hello")
    drain actor
    rawAnsiTexts term |> should equal ["hello"]
    actor.PostAndAsyncReply(fun ch -> SurfaceMessage.Shutdown ch)
    |> Async.RunSynchronously

[<Fact>]
let ``WriteLine appends newline`` () =
    let (actor, term) = startMockActor ()
    use writer = new ActorWriter(actor)
    writer.WriteLine("hello")
    drain actor
    rawAnsiTexts term |> should equal ["hello\n"]
    actor.PostAndAsyncReply(fun ch -> SurfaceMessage.Shutdown ch)
    |> Async.RunSynchronously

[<Fact>]
let ``Write(char) posts a single-character RawAnsi`` () =
    let (actor, term) = startMockActor ()
    use writer = new ActorWriter(actor)
    writer.Write('a')
    writer.Write('b')
    drain actor
    rawAnsiTexts term |> should equal ["a"; "b"]
    actor.PostAndAsyncReply(fun ch -> SurfaceMessage.Shutdown ch)
    |> Async.RunSynchronously

[<Fact>]
let ``empty / null writes are dropped silently — no zero-length ops`` () =
    let (actor, term) = startMockActor ()
    use writer = new ActorWriter(actor)
    writer.Write(null : string | null)
    writer.Write("")
    drain actor
    rawAnsiCount term |> should equal 0
    actor.PostAndAsyncReply(fun ch -> SurfaceMessage.Shutdown ch)
    |> Async.RunSynchronously

[<Fact>]
let ``many writes preserve order — no coalescing of RawAnsi`` () =
    let (actor, term) = startMockActor ()
    use writer = new ActorWriter(actor)
    let chunks = [ "alpha"; "beta"; "gamma"; "delta"; "epsilon" ]
    for c in chunks do writer.Write(c)
    drain actor
    rawAnsiTexts term |> should equal chunks
    actor.PostAndAsyncReply(fun ch -> SurfaceMessage.Shutdown ch)
    |> Async.RunSynchronously

[<Fact>]
let ``Write(buffer, index, count) writes the slice`` () =
    let (actor, term) = startMockActor ()
    use writer = new ActorWriter(actor)
    let buf = "abcdef".ToCharArray()
    writer.Write(buf, 1, 3)  // "bcd"
    drain actor
    rawAnsiTexts term |> should equal ["bcd"]
    actor.PostAndAsyncReply(fun ch -> SurfaceMessage.Shutdown ch)
    |> Async.RunSynchronously
