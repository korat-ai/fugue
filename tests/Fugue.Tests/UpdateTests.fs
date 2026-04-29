module Fugue.Tests.UpdateTests

open System
open System.Text
open System.Threading
open Xunit
open FsUnit.Xunit
open Fugue.Cli.Model
open Fugue.Cli.Update
open Fugue.Core.Config

let private freshModel () =
    { Config = { Provider = Anthropic("k", "m"); SystemPrompt = None; MaxIterations = 30; Ui = Fugue.Core.Config.defaultUi () }
      Session = None
      History = []
      Input = ""
      Streaming = false
      Cancel = None
      Status = "" }

[<Fact>]
let ``InputChanged updates Input`` () =
    let m = freshModel ()
    let m' = update (InputChanged "hello") m
    m'.Input |> should equal "hello"

[<Fact>]
let ``Submit moves Input into UserMsg block and clears Input`` () =
    let m = { freshModel () with Input = "hi" }
    let m' = update Submit m
    m'.Input |> should equal ""
    m'.Streaming |> should equal true
    match List.last m'.History with
    | UserMsg "hi" -> ()
    | other -> failwithf "expected UserMsg \"hi\", got %A" other

[<Fact>]
let ``Submit with empty input is no-op`` () =
    let m = { freshModel () with Input = "   " }
    let m' = update Submit m
    m'.Streaming |> should equal false
    m'.History |> should be Empty

[<Fact>]
let ``TextChunk appends to last AssistantText block, creating one if absent`` () =
    // Streaming=true is the precondition: TextChunk arriving while not streaming is dropped
    // (cancelled / finished — late chunks must not mutate history).
    let m = { freshModel () with Streaming = true }
    let m1 = update (TextChunk "hel") m
    let m2 = update (TextChunk "lo") m1
    match List.last m2.History with
    | AssistantText sb -> string sb |> should equal "hello"
    | other -> failwithf "expected AssistantText, got %A" other

[<Fact>]
let ``TextChunk is dropped when not Streaming`` () =
    let m = freshModel ()
    let m' = update (TextChunk "late") m
    m'.History |> should be Empty

[<Fact>]
let ``ToolStarted adds Running ToolBlock`` () =
    let m = { freshModel () with Streaming = true }
    let m' = update (ToolStarted("id1", "Read", "{}")) m
    match List.last m'.History with
    | ToolBlock("id1", "Read", "{}", Running) -> ()
    | other -> failwithf "wrong block %A" other

[<Fact>]
let ``ToolCompleted updates matching ToolBlock`` () =
    let m = { freshModel () with Streaming = true }
    let m1 = update (ToolStarted("id1", "Read", "{}")) m
    let m2 = update (ToolCompleted("id1", "ok", false)) m1
    match List.last m2.History with
    | ToolBlock("id1", "Read", "{}", Done "ok") -> ()
    | other -> failwithf "wrong block %A" other

[<Fact>]
let ``StreamFinished clears Streaming and Cancel`` () =
    let cts = new CancellationTokenSource()
    let m = { freshModel () with Streaming = true; Cancel = Some cts }
    let m' = update StreamFinished m
    m'.Streaming |> should equal false
    m'.Cancel |> should equal None

[<Fact>]
let ``StreamFailed appends ErrorBlock`` () =
    let m = { freshModel () with Streaming = true }
    let m' = update (StreamFailed "boom") m
    m'.Streaming |> should equal false
    match List.last m'.History with
    | ErrorBlock "boom" -> ()
    | other -> failwithf "wrong block %A" other

[<Fact>]
let ``StreamStarted records the CTS`` () =
    let cts = new CancellationTokenSource()
    let m = { freshModel () with Streaming = true }
    let m' = update (StreamStarted cts) m
    m'.Cancel |> should equal (Some cts)
