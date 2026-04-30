module Fugue.Agent.Conversation

open System
open System.Collections.Generic
open System.Diagnostics
open System.Diagnostics.CodeAnalysis
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
[<RequiresUnreferencedCode("Serializes FunctionCallContent.Arguments via STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
[<RequiresDynamicCode("Serializes FunctionCallContent.Arguments via STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
let run
    (agent: AIAgent)
    (session: AgentSession | null)
    (userInput: string)
    (cancel: CancellationToken)
    : IAsyncEnumerable<Event> =

    taskSeq {
        let mutable failed = false
        // id -> (name, argsJson, stopwatch) for debug timing
        let toolTimers = Dictionary<string, string * string * Stopwatch>()
        Fugue.Core.Log.info "conv" ("run start, input=" + string userInput.Length + " chars, ct.IsCancelled=" + string cancel.IsCancellationRequested)
        try
            let stream = agent.RunStreamingAsync(userInput, session, options = null, cancellationToken = cancel)
            let enumerator = stream.GetAsyncEnumerator(cancel)
            let mutable hasNext = true
            let mutable updIdx = 0
            while hasNext do
                let! step = enumerator.MoveNextAsync().AsTask()
                if step then
                    let upd : AgentResponseUpdate = enumerator.Current
                    updIdx <- updIdx + 1
                    Fugue.Core.Log.info "conv" ("upd #" + string updIdx + ", contents=" + string (Seq.length upd.Contents) + ", ct.IsCancelled=" + string cancel.IsCancellationRequested)
                    // Contents is non-nullable (NotNull) — iterate directly
                    for c in upd.Contents do
                        match c with
                        | :? TextContent as tc when not (String.IsNullOrEmpty tc.Text) ->
                            Fugue.Core.Log.info "conv" ("  text chunk len=" + string tc.Text.Length)
                            yield TextChunk tc.Text
                        | :? FunctionCallContent as fc ->
                            let argsJson =
                                if isNull (box fc.Arguments) then "{}"
                                else
                                    try System.Text.Json.JsonSerializer.Serialize fc.Arguments
                                    with _ -> "{}"
                            Fugue.Core.Log.info "conv" (sprintf "  tool start id=%s name=%s args=%s" fc.CallId fc.Name argsJson)
                            toolTimers.[fc.CallId] <- (fc.Name, argsJson, Stopwatch.StartNew())
                            yield ToolStarted(fc.CallId, fc.Name, argsJson)
                        | :? FunctionResultContent as fr ->
                            let output = if isNull (box fr.Result) then "" else string fr.Result
                            let isError = not (isNull (box fr.Exception))
                            Fugue.Core.Log.info "conv" ("  tool done id=" + fr.CallId + " isError=" + string isError + " outLen=" + string output.Length)
                            let name, argsJson, durationMs =
                                match toolTimers.TryGetValue fr.CallId with
                                | true, (n, a, sw) -> sw.Stop(); n, a, sw.ElapsedMilliseconds
                                | _ -> fr.CallId, "{}", 0L
                            Fugue.Core.DebugLog.toolCall name argsJson output durationMs
                            yield ToolCompleted(fr.CallId, output, isError)
                        | other ->
                            Fugue.Core.Log.info "conv" (sprintf "  ignored content type=%s" (other.GetType().Name))
                else
                    hasNext <- false
        with
        | :? OperationCanceledException as oce ->
            // Don't yield Failed — let cancellation propagate so Program.fs's outer catch
            // owns the single "cancelled" error path. Otherwise both layers fire.
            // (taskSeq blocks reraise(), so we rethrow the original exception explicitly.)
            Fugue.Core.Log.info "conv" "cancellation propagating"
            raise oce
        | ex ->
            failed <- true
            Fugue.Core.Log.ex "conv" "stream caught" ex
            yield Failed ex
        if not failed then
            Fugue.Core.Log.info "conv" "run finished cleanly"
            yield Finished
        else
            Fugue.Core.Log.info "conv" "run finished with failure"
    }
