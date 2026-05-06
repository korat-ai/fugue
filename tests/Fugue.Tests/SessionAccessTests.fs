module Fugue.Tests.SessionAccessTests

open System
open System.Collections.Generic
open System.Diagnostics.CodeAnalysis
open System.Text.Json
open System.Threading
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open Xunit
open FsUnit.Xunit
open Fugue.Tools.AiFunctions.GetConversationFn
open Fugue.Tools

/// Minimal concrete AgentSession with no special behaviour.
type TestAgentSession() =
    inherit AgentSession()
    override _.GetService(_: Type, _: obj) : obj | null = null

[<RequiresUnreferencedCode("Calls getConversationJson which uses AgentSessionExtensions over STJ state")>]
[<RequiresDynamicCode("Calls getConversationJson which uses AgentSessionExtensions over STJ state")>]
[<Fact>]
let ``getConversationJson null session returns no_session status`` () =
    let json = getConversationJson null
    use doc = JsonDocument.Parse json
    let root = doc.RootElement
    root.GetProperty("status").GetString() |> should equal "no_session"
    root.GetProperty("turn_count").GetInt32() |> should equal 0
    root.GetProperty("messages").GetArrayLength() |> should equal 0

[<RequiresUnreferencedCode("Calls getConversationJson which uses AgentSessionExtensions over STJ state")>]
[<RequiresDynamicCode("Calls getConversationJson which uses AgentSessionExtensions over STJ state")>]
[<Fact>]
let ``getConversationJson fresh session with no history returns history_unavailable`` () =
    let session = TestAgentSession() :> AgentSession
    let json = getConversationJson session
    use doc = JsonDocument.Parse json
    let root = doc.RootElement
    root.GetProperty("status").GetString() |> should equal "history_unavailable"
    root.GetProperty("turn_count").GetInt32() |> should equal 0
    root.GetProperty("messages").GetArrayLength() |> should equal 0

[<RequiresUnreferencedCode("Calls getConversationJson which uses AgentSessionExtensions over STJ state")>]
[<RequiresDynamicCode("Calls getConversationJson which uses AgentSessionExtensions over STJ state")>]
[<Fact>]
let ``getConversationJson session with two messages returns ok status and both messages`` () =
    // Key discovery test: verify TryGetInMemoryChatHistory works with key=null.
    let session = TestAgentSession() :> AgentSession
    let messages = List<ChatMessage>()
    messages.Add(ChatMessage(ChatRole.User, "Hello from user"))
    messages.Add(ChatMessage(ChatRole.Assistant, "Hello from assistant"))
    let stateKey : string | null = null
    let jsonOpts : System.Text.Json.JsonSerializerOptions | null = null
    AgentSessionExtensions.SetInMemoryChatHistory(session, messages, stateKey, jsonOpts)

    let json = getConversationJson session
    use doc = JsonDocument.Parse json
    let root = doc.RootElement

    root.GetProperty("status").GetString() |> should equal "ok"
    root.GetProperty("turn_count").GetInt32() |> should equal 2

    let msgs = root.GetProperty("messages")
    msgs.GetArrayLength() |> should equal 2

    let m0 = msgs.[0]
    m0.GetProperty("index").GetInt32() |> should equal 0
    m0.GetProperty("role").GetString() |> should equal "user"
    m0.GetProperty("content").GetString() |> should equal "Hello from user"
    m0.GetProperty("has_tool_calls").GetBoolean() |> should equal false

    let m1 = msgs.[1]
    m1.GetProperty("index").GetInt32() |> should equal 1
    m1.GetProperty("role").GetString() |> should equal "assistant"
    m1.GetProperty("content").GetString() |> should equal "Hello from assistant"

[<RequiresUnreferencedCode("Calls getConversationJson which uses AgentSessionExtensions over STJ state")>]
[<RequiresDynamicCode("Calls getConversationJson which uses AgentSessionExtensions over STJ state")>]
[<Fact>]
let ``getConversationJson JSON round-trip has correct top-level shape`` () =
    let session = TestAgentSession() :> AgentSession
    let messages = List<ChatMessage>()
    messages.Add(ChatMessage(ChatRole.User, "test input"))
    let stateKey : string | null = null
    let jsonOpts : System.Text.Json.JsonSerializerOptions | null = null
    AgentSessionExtensions.SetInMemoryChatHistory(session, messages, stateKey, jsonOpts)

    let json = getConversationJson session
    use doc = JsonDocument.Parse json
    let root = doc.RootElement

    // Verify top-level keys exist with correct types
    root.TryGetProperty("status") |> fst |> should equal true
    root.TryGetProperty("turn_count") |> fst |> should equal true
    root.TryGetProperty("messages") |> fst |> should equal true
    root.GetProperty("turn_count").ValueKind |> should equal JsonValueKind.Number
    root.GetProperty("messages").ValueKind |> should equal JsonValueKind.Array

    // Verify message shape
    let m0 = root.GetProperty("messages").[0]
    m0.TryGetProperty("index") |> fst |> should equal true
    m0.TryGetProperty("role") |> fst |> should equal true
    m0.TryGetProperty("content") |> fst |> should equal true
    m0.TryGetProperty("has_tool_calls") |> fst |> should equal true
    m0.GetProperty("role").GetString() |> should equal "user"

[<RequiresUnreferencedCode("Calls ToolRegistry.buildAll which uses AgentSessionExtensions over STJ state")>]
[<RequiresDynamicCode("Calls ToolRegistry.buildAll which uses AgentSessionExtensions over STJ state")>]
[<Fact>]
let ``ToolRegistry includes GetConversation and invoking it with no session returns no_session`` () = task {
    let getSession () : Microsoft.Agents.AI.AgentSession | null = null
    let tools = ToolRegistry.buildAll "/tmp" Fugue.Core.Hooks.defaultConfig "test-session" getSession

    // Verify GetConversation is in the names list
    ToolRegistry.names |> should contain "GetConversation"

    // Verify there is a tool named GetConversation in the list
    let gc = tools |> List.tryFind (fun t -> t.Name = "GetConversation")
    gc |> should not' (equal None)

    // The closure returns null → tool reports no_session.
    let gcFn = gc.Value
    let args = Microsoft.Extensions.AI.AIFunctionArguments()
    let result = gcFn.InvokeAsync(args, CancellationToken.None)
    let! resultObj = result.AsTask()
    let resultStr = string resultObj
    use doc = JsonDocument.Parse resultStr
    doc.RootElement.GetProperty("status").GetString() |> should equal "no_session"
}
