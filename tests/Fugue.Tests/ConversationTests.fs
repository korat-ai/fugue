module Fugue.Tests.ConversationTests

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open Xunit
open FsUnit.Xunit
open FSharp.Control
open Fugue.Agent.Conversation

/// Minimal concrete AgentSession for the FakeAgent.
type FakeAgentSession() =
    inherit AgentSession()
    override _.GetService(_: Type, _: obj) : obj | null = null

/// Fake AIAgent that emits a scripted list of AgentResponseUpdate's.
/// Only RunCoreStreamingAsync is functional; all others throw NotImplementedException.
type FakeAgent(updates: AgentResponseUpdate list) =
    inherit AIAgent()

    override _.Name = "FakeAgent"
    override _.Description = "Fake"

    override _.CreateSessionCoreAsync(_ct: CancellationToken) =
        ValueTask<AgentSession>(FakeAgentSession() :> AgentSession)

    override _.DeserializeSessionCoreAsync(_json: JsonElement, _opts: JsonSerializerOptions, _ct: CancellationToken) =
        ValueTask<AgentSession>(FakeAgentSession() :> AgentSession)

    override _.SerializeSessionCoreAsync(_session: AgentSession, _opts: JsonSerializerOptions, _ct: CancellationToken) =
        ValueTask<JsonElement>(JsonDocument.Parse("{}").RootElement)

    override _.RunCoreAsync(_messages, _session, _options, _ct) =
        Task.FromException<AgentResponse>(NotImplementedException("FakeAgent.RunCoreAsync not implemented"))

    override _.RunCoreStreamingAsync(_messages: IEnumerable<ChatMessage>, _session: AgentSession, _options: AgentRunOptions, _ct: CancellationToken) : IAsyncEnumerable<AgentResponseUpdate> =
        taskSeq { for u in updates do yield u }

let private textUpd (s: string) =
    let contents : IList<AIContent> = ResizeArray<AIContent>([| TextContent(s) :> AIContent |])
    AgentResponseUpdate(role = Nullable(), contents = contents)

[<Fact>]
let ``Conversation streams text chunks and ends with Finished`` () = task {
    let agent = FakeAgent([ textUpd "hel"; textUpd "lo" ]) :> AIAgent
    let events = ResizeArray<Event>()
    let stream = run agent null "hi" CancellationToken.None
    let enumerator = stream.GetAsyncEnumerator(CancellationToken.None)
    let mutable hasNext = true
    while hasNext do
        let! step = enumerator.MoveNextAsync().AsTask()
        if step then events.Add enumerator.Current
        else hasNext <- false
    events
    |> Seq.choose (function TextChunk s -> Some s | _ -> None)
    |> String.concat ""
    |> should equal "hello"
    events
    |> Seq.exists (function Finished -> true | _ -> false)
    |> should equal true
}
