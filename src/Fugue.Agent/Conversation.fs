module Fugue.Agent.Conversation

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open FSharp.Control

/// UI-friendly conversation events.
type Event =
    | TextChunk of string
    | ToolStarted of id: string * name: string * argsJson: string
    | ToolCompleted of id: string * output: string * isError: bool
    | Finished
    | Failed of exn

/// Run a single user-input through the AIAgent and surface UI events.
/// `session` is the persistent AgentSession (None — start fresh / null session).
/// MAF handles the full tool-loop internally; we observe the stream.
let run
    (agent: AIAgent)
    (session: AgentSession | null)
    (userInput: string)
    (cancel: CancellationToken)
    : IAsyncEnumerable<Event> =

    taskSeq {
        let mutable failed = false
        try
            let stream = agent.RunStreamingAsync(userInput, session, options = null, cancellationToken = cancel)
            let enumerator = stream.GetAsyncEnumerator(cancel)
            let mutable hasNext = true
            while hasNext do
                let! step = enumerator.MoveNextAsync().AsTask()
                if step then
                    let upd : AgentResponseUpdate = enumerator.Current
                    // Contents is non-nullable (NotNull) — iterate directly
                    for c in upd.Contents do
                        match c with
                        | :? TextContent as tc when not (String.IsNullOrEmpty tc.Text) ->
                            yield TextChunk tc.Text
                        | :? FunctionCallContent as fc ->
                            let argsJson =
                                if isNull (box fc.Arguments) then "{}"
                                else
                                    try System.Text.Json.JsonSerializer.Serialize fc.Arguments
                                    with _ -> "{}"
                            yield ToolStarted(fc.CallId, fc.Name, argsJson)
                        | :? FunctionResultContent as fr ->
                            let output = if isNull (box fr.Result) then "" else string fr.Result
                            let isError = not (isNull (box fr.Exception))
                            yield ToolCompleted(fr.CallId, output, isError)
                        | _ -> ()
                else
                    hasNext <- false
        with ex ->
            failed <- true
            yield Failed ex
        if not failed then yield Finished
    }
