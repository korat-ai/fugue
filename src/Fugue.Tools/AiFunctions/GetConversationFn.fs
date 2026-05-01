module Fugue.Tools.AiFunctions.GetConversationFn

open System
open System.Buffers
open System.Collections.Generic
open System.Diagnostics.CodeAnalysis
open System.Text
open System.Text.Json
open System.Threading
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

// Module-level session holder — updated by Repl after each CreateSessionAsync / /new / /clear-history.
let mutable private currentSession : AgentSession | null = null

/// Called by Repl.run after the session is created or reset.
let setSession (s: AgentSession | null) : unit = currentSession <- s

[<RequiresUnreferencedCode("Uses AgentSessionExtensions which touches STJ-backed AgentSessionStateBag")>]
[<RequiresDynamicCode("Uses AgentSessionExtensions which touches STJ-backed AgentSessionStateBag")>]
let getConversationJson (session: AgentSession | null) : string =
    let buf = new ArrayBufferWriter<byte>()
    use w = new Utf8JsonWriter(buf)
    w.WriteStartObject()

    match session with
    | null ->
        w.WriteString("status", "no_session")
        w.WriteNumber("turn_count", 0)
        w.WriteStartArray("messages")
        w.WriteEndArray()
        w.WriteEndObject()
        w.Flush()
        Encoding.UTF8.GetString(buf.WrittenSpan)
    | sess ->
        // Unchecked.defaultof lets us hold the [MaybeNull] out param without null-safety errors.
        let mutable messages = Unchecked.defaultof<List<ChatMessage>>
        let stateKey : string | null = null
        let jsonOpts : System.Text.Json.JsonSerializerOptions | null = null
        let ok = AgentSessionExtensions.TryGetInMemoryChatHistory(sess, &messages, stateKey, jsonOpts)
        // messages is MaybeNull after the call — check before use
        if not ok || isNull (box messages) then
            w.WriteString("status", "history_unavailable")
            w.WriteNumber("turn_count", 0)
            w.WriteStartArray("messages")
            w.WriteEndArray()
            w.WriteEndObject()
            w.Flush()
            Encoding.UTF8.GetString(buf.WrittenSpan)
        else
            w.WriteString("status", "ok")
            w.WriteNumber("turn_count", messages.Count)
            w.WriteStartArray("messages")
            for i in 0 .. messages.Count - 1 do
                let msg = messages.[i]
                w.WriteStartObject()
                w.WriteNumber("index", i)
                w.WriteString("role", msg.Role.Value)
                // Concatenate all text content
                let textParts =
                    msg.Contents
                    |> Seq.choose (function :? TextContent as tc -> Some tc.Text | _ -> None)
                    |> String.concat ""
                w.WriteString("content", textParts)
                // Detect function call content
                let toolCalls =
                    msg.Contents
                    |> Seq.choose (function :? FunctionCallContent as fc -> Some fc | _ -> None)
                    |> Seq.toArray
                w.WriteBoolean("has_tool_calls", toolCalls.Length > 0)
                if toolCalls.Length > 0 then
                    w.WriteStartArray("tool_calls")
                    for tc in toolCalls do
                        w.WriteStartObject()
                        w.WriteString("name", tc.Name)
                        w.WriteString("call_id", tc.CallId)
                        w.WriteEndObject()
                    w.WriteEndArray()
                w.WriteEndObject()
            w.WriteEndArray()
            w.WriteEndObject()
            w.Flush()
            Encoding.UTF8.GetString(buf.WrittenSpan)

let private schema = DelegatedFn.parseSchema """{
  "type":"object",
  "properties":{
    "filter_role":{"type":"string","description":"Optional: 'user', 'assistant', or 'tool'. If provided, only messages with that role are returned (not yet enforced in Phase 1)."}
  },
  "required":[]
}"""

let private desc =
    "Return the current conversation history as JSON. Use this to review context, " +
    "build a summary, debug the session state, or export the dialogue. " +
    "Returns { turn_count, status, messages: [{index, role, content, has_tool_calls}] }. " +
    "status is 'ok' when history is available, 'no_session' if no session exists, " +
    "or 'history_unavailable' if the session has not produced history yet."

/// Create the GetConversation AIFunction. Uses the module-level session set via setSession.
/// The optional `getSession` closure overrides the module-level state (useful in tests).
[<RequiresUnreferencedCode("Calls getConversationJson which uses AgentSessionExtensions over STJ state")>]
[<RequiresDynamicCode("Calls getConversationJson which uses AgentSessionExtensions over STJ state")>]
let create (getSession: (unit -> AgentSession | null) option) (hooksConfig: Fugue.Core.Hooks.HooksConfig) (sessionId: string) : AIFunction =
    DelegatedFn.DelegatedAIFunction(
        name        = "GetConversation",
        description = desc,
        schema      = schema,
        hooksConfig = hooksConfig,
        sessionId   = sessionId,
        invoke      = fun _args ct -> task {
            ct.ThrowIfCancellationRequested()
            let session =
                match getSession with
                | Some f -> f ()
                | None   -> currentSession
            return getConversationJson session
        }) :> AIFunction
