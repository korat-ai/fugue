module Fugue.Tests.MacrosTests

open Xunit
open FsUnit.Xunit
open Fugue.Cli.Macros

// Macros module uses process-wide mutable state → must run sequentially.
[<Collection("Sequential")>]
type MacrosTests() =

    do clear()

    interface System.IDisposable with
        member _.Dispose() = clear()

    [<Fact>]
    member _.``isRecording returns false before any recording``() =
        isRecording () |> should equal false

    [<Fact>]
    member _.``startRecording sets isRecording to true``() =
        startRecording "hello"
        isRecording () |> should equal true

    [<Fact>]
    member _.``stopRecording without start returns None``() =
        stopRecording () |> should equal None

    [<Fact>]
    member _.``stopRecording after start returns Some name``() =
        startRecording "foo"
        stopRecording () |> should equal (Some "foo")

    [<Fact>]
    member _.``stopRecording clears isRecording``() =
        startRecording "bar"
        stopRecording () |> ignore
        isRecording () |> should equal false

    [<Fact>]
    member _.``recordStep accumulates steps in order``() =
        startRecording "seq"
        recordStep "step1"
        recordStep "step2"
        recordStep "step3"
        stopRecording () |> ignore
        enqueuePlay "seq" |> should equal true
        dequeueStep () |> should equal (Some "step1")
        dequeueStep () |> should equal (Some "step2")
        dequeueStep () |> should equal (Some "step3")
        dequeueStep () |> should equal None

    [<Fact>]
    member _.``recordStep outside recording is ignored``() =
        recordStep "ghost"
        dequeueStep () |> should equal None

    [<Fact>]
    member _.``enqueuePlay non-existent macro returns false``() =
        enqueuePlay "missing" |> should equal false

    [<Fact>]
    member _.``dequeueStep from empty queue returns None``() =
        dequeueStep () |> should equal None

    [<Fact>]
    member _.``list returns empty when no macros saved``() =
        list () |> List.isEmpty |> should equal true

    [<Fact>]
    member _.``list returns correct name and step count``() =
        startRecording "m1"
        recordStep "a"
        recordStep "b"
        stopRecording () |> ignore
        startRecording "m2"
        recordStep "x"
        stopRecording () |> ignore
        let entries = list () |> List.sortBy fst
        entries |> should equal [ ("m1", 2); ("m2", 1) ]

    [<Fact>]
    member _.``remove returns true for existing macro``() =
        startRecording "del"
        recordStep "s"
        stopRecording () |> ignore
        remove "del" |> should equal true

    [<Fact>]
    member _.``remove returns false for non-existent macro``() =
        remove "nope" |> should equal false

    [<Fact>]
    member _.``remove makes macro unavailable for play``() =
        startRecording "gone"
        recordStep "s"
        stopRecording () |> ignore
        remove "gone" |> ignore
        enqueuePlay "gone" |> should equal false

    [<Fact>]
    member _.``overwriting macro replaces steps``() =
        startRecording "dup"
        recordStep "old1"
        recordStep "old2"
        stopRecording () |> ignore
        startRecording "dup"
        recordStep "new1"
        stopRecording () |> ignore
        enqueuePlay "dup" |> ignore
        dequeueStep () |> should equal (Some "new1")
        dequeueStep () |> should equal None

    [<Fact>]
    member _.``clear removes all macros and queue``() =
        startRecording "x"
        recordStep "s"
        stopRecording () |> ignore
        enqueuePlay "x" |> ignore
        clear ()
        list () |> List.isEmpty |> should equal true
        dequeueStep () |> should equal None
        isRecording () |> should equal false

    [<Fact>]
    member _.``playing macro twice enqueues steps both times``() =
        startRecording "rep"
        recordStep "a"
        recordStep "b"
        stopRecording () |> ignore
        enqueuePlay "rep" |> ignore
        enqueuePlay "rep" |> ignore
        dequeueStep () |> should equal (Some "a")
        dequeueStep () |> should equal (Some "b")
        dequeueStep () |> should equal (Some "a")
        dequeueStep () |> should equal (Some "b")
        dequeueStep () |> should equal None

    [<Fact>]
    member _.``empty macro records and plays zero steps``() =
        startRecording "empty"
        stopRecording () |> should equal (Some "empty")
        enqueuePlay "empty" |> should equal true
        dequeueStep () |> should equal None

    [<Fact>]
    member _.``list includes empty macro with count 0``() =
        startRecording "zero"
        stopRecording () |> ignore
        let entries = list ()
        entries |> should contain ("zero", 0)
