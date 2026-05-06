module Fugue.Core.SessionRecord

open System
open System.Buffers
open System.Text
open System.Text.Json

/// One record in a session JSONL file.
type SessionRecord =
    | SessionStart  of timestamp: DateTimeOffset * cwd: string * model: string * provider: string
    | UserTurn      of timestamp: DateTimeOffset * content: string
    | AssistantTurn of timestamp: DateTimeOffset * content: string
    | ToolCall      of timestamp: DateTimeOffset * toolName: string * args: string * callId: string
    | ToolResult    of timestamp: DateTimeOffset * callId: string * output: string * isError: bool
    | SessionEnd    of timestamp: DateTimeOffset * turnCount: int * durationMs: int64

/// Serialize a SessionRecord to a single JSON line (no trailing newline).
/// Uses Utf8JsonWriter — no reflection, AOT-safe.
let serialize (record: SessionRecord) : string =
    let buf = new ArrayBufferWriter<byte>()
    use w = new Utf8JsonWriter(buf)
    w.WriteStartObject()
    match record with
    | SessionStart(ts, cwd, model, provider) ->
        w.WriteString("type", "session_start")
        w.WriteString("ts", ts.ToString("o"))
        w.WriteString("cwd", cwd)
        w.WriteString("model", model)
        w.WriteString("provider", provider)
    | UserTurn(ts, content) ->
        w.WriteString("type", "user_turn")
        w.WriteString("ts", ts.ToString("o"))
        w.WriteString("content", content)
    | AssistantTurn(ts, content) ->
        w.WriteString("type", "assistant_turn")
        w.WriteString("ts", ts.ToString("o"))
        w.WriteString("content", content)
    | ToolCall(ts, toolName, args, callId) ->
        w.WriteString("type", "tool_call")
        w.WriteString("ts", ts.ToString("o"))
        w.WriteString("tool", toolName)
        w.WriteString("args", args)
        w.WriteString("call_id", callId)
    | ToolResult(ts, callId, output, isError) ->
        w.WriteString("type", "tool_result")
        w.WriteString("ts", ts.ToString("o"))
        w.WriteString("call_id", callId)
        w.WriteString("output", output)
        w.WriteBoolean("is_error", isError)
    | SessionEnd(ts, turnCount, durationMs) ->
        w.WriteString("type", "session_end")
        w.WriteString("ts", ts.ToString("o"))
        w.WriteNumber("turn_count", turnCount)
        w.WriteNumber("duration_ms", durationMs)
    w.WriteEndObject()
    w.Flush()
    Encoding.UTF8.GetString(buf.WrittenSpan)

/// Parse one JSONL line into a SessionRecord.
/// Returns None on parse error or unknown type — safe to call on malformed input.
let deserialize (line: string) : SessionRecord option =
    try
        use doc = JsonDocument.Parse line
        let root = doc.RootElement
        let mutable typeProp = Unchecked.defaultof<JsonElement>
        if not (root.TryGetProperty("type", &typeProp)) then None
        else
            let getStr (key: string) =
                let mutable el = Unchecked.defaultof<JsonElement>
                if root.TryGetProperty(key, &el) then el.GetString() |> Option.ofObj |> Option.defaultValue ""
                else ""
            let getTs () =
                let mutable el = Unchecked.defaultof<JsonElement>
                if root.TryGetProperty("ts", &el) then
                    let s = el.GetString()
                    match DateTimeOffset.TryParse(s) with
                    | true, dto -> dto
                    | false, _  -> DateTimeOffset.UtcNow
                else DateTimeOffset.UtcNow
            match typeProp.GetString() with
            | "session_start" ->
                Some (SessionStart(getTs (), getStr "cwd", getStr "model", getStr "provider"))
            | "user_turn" ->
                Some (UserTurn(getTs (), getStr "content"))
            | "assistant_turn" ->
                Some (AssistantTurn(getTs (), getStr "content"))
            | "tool_call" ->
                Some (ToolCall(getTs (), getStr "tool", getStr "args", getStr "call_id"))
            | "tool_result" ->
                let mutable errEl = Unchecked.defaultof<JsonElement>
                let isError =
                    if root.TryGetProperty("is_error", &errEl) then errEl.GetBoolean()
                    else false
                Some (ToolResult(getTs (), getStr "call_id", getStr "output", isError))
            | "session_end" ->
                let mutable tcEl = Unchecked.defaultof<JsonElement>
                let mutable dmEl = Unchecked.defaultof<JsonElement>
                let turnCount  = if root.TryGetProperty("turn_count",  &tcEl) then tcEl.GetInt32() else 0
                let durationMs = if root.TryGetProperty("duration_ms", &dmEl) then dmEl.GetInt64() else 0L
                Some (SessionEnd(getTs (), turnCount, durationMs))
            | _ -> None
    with _ -> None
