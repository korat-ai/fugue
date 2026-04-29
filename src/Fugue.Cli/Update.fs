module Fugue.Cli.Update

open System.Text
open Fugue.Cli.Model

let private appendBlock (b: Block) (history: Block list) = history @ [ b ]

/// Pure update — no Cmd dispatched here. Effects are wired in Program.fs.
let update (msg: Msg) (m: Model) : Model =
    match msg with
    | InputChanged s -> { m with Input = s }

    | Submit when m.Streaming || m.Input.Trim() = "" -> m
    | Submit ->
        let userBlock = UserMsg m.Input
        { m with
            History = appendBlock userBlock m.History
            Input = ""
            Streaming = true
            Status = "thinking…" }

    | StreamStarted cts ->
        { m with Cancel = Some cts }

    // Drop late-arriving stream events once the user has cancelled or the stream
    // has finished/failed. Without this the agent keeps appending text after a
    // cancel because Anthropic/Ollama keep flushing buffered chunks for a while.
    | TextChunk _      when not m.Streaming -> m
    | ToolStarted _    when not m.Streaming -> m
    | ToolCompleted _  when not m.Streaming -> m

    | TextChunk t ->
        let history' =
            match List.tryLast m.History with
            | Some (AssistantText sb) ->
                sb.Append t |> ignore
                m.History
            | _ ->
                let sb = StringBuilder(t)
                appendBlock (AssistantText sb) m.History
        { m with History = history' }

    | ToolStarted(id, name, args) ->
        { m with History = appendBlock (ToolBlock(id, name, args, Running)) m.History }

    | ToolCompleted(id, output, isError) ->
        let history' =
            m.History
            |> List.map (fun b ->
                match b with
                | ToolBlock(bid, name, args, _) when bid = id ->
                    let st = if isError then FailedTool output else Done output
                    ToolBlock(bid, name, args, st)
                | other -> other)
        { m with History = history' }

    | StreamFinished ->
        { m with Streaming = false; Cancel = None; Status = "" }

    | StreamFailed err ->
        { m with
            Streaming = false
            Cancel = None
            Status = ""
            History = appendBlock (ErrorBlock err) m.History }

    | Cancel ->
        m.Cancel |> Option.iter (fun cts -> try cts.Cancel() with _ -> ())
        { m with Streaming = false; Cancel = None; Status = "cancelled" }

    | Quit ->
        // Application.RequestStop is called from outside (Program.fs)
        m
