module Fugue.Tests.SessionPersistenceTests

open System
open System.Collections.Generic
open System.Diagnostics.CodeAnalysis
open System.IO
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open Xunit
open FsUnit.Xunit
open Fugue.Core.SessionRecord
open Fugue.Core.SessionPersistence

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private tmpDir () =
    let d = Path.Combine(Path.GetTempPath(), "FugueSessTests_" + Path.GetRandomFileName())
    Directory.CreateDirectory d |> ignore
    d

let private cleanup (d: string) =
    if Directory.Exists d then Directory.Delete(d, recursive = true)

// ---------------------------------------------------------------------------
// 1. JSONL round-trip: serialize 5 records, write to tmp file, readRecords,
//    assert count=5 and field equality for each variant.
// ---------------------------------------------------------------------------

[<Fact>]
let ``JSONL round-trip: 5 record variants serialize and deserialize correctly`` () =
    let tmp = tmpDir ()
    try
        let file = Path.Combine(tmp, "test.jsonl")
        let ts = DateTimeOffset.Parse "2026-05-01T10:00:00+00:00"
        let records = [
            SessionStart(ts, "/my/project", "qwen3-35b", "openai")
            UserTurn(ts.AddSeconds 5.0, "hello world")
            AssistantTurn(ts.AddSeconds 10.0, "I can help")
            ToolCall(ts.AddSeconds 11.0, "Read", "{\"path\":\"/foo.fs\"}", "c-01")
            ToolResult(ts.AddSeconds 12.0, "c-01", "let x = 42", false)
        ]
        for r in records do
            appendRecord file r
        let readBack = readRecords file
        readBack |> List.length |> should equal 5
        match readBack.[0] with
        | SessionStart(_, cwd, model, provider) ->
            cwd      |> should equal "/my/project"
            model    |> should equal "qwen3-35b"
            provider |> should equal "openai"
        | _ -> failwith "Expected SessionStart"
        match readBack.[1] with
        | UserTurn(_, content) -> content |> should equal "hello world"
        | _ -> failwith "Expected UserTurn"
        match readBack.[2] with
        | AssistantTurn(_, content) -> content |> should equal "I can help"
        | _ -> failwith "Expected AssistantTurn"
        match readBack.[3] with
        | ToolCall(_, toolName, args, callId) ->
            toolName |> should equal "Read"
            args     |> should equal "{\"path\":\"/foo.fs\"}"
            callId   |> should equal "c-01"
        | _ -> failwith "Expected ToolCall"
        match readBack.[4] with
        | ToolResult(_, callId, output, isError) ->
            callId  |> should equal "c-01"
            output  |> should equal "let x = 42"
            isError |> should equal false
        | _ -> failwith "Expected ToolResult"
    finally cleanup tmp

// ---------------------------------------------------------------------------
// 2. Path encoding: encodeCwd produces expected output, no / or \ in result.
// ---------------------------------------------------------------------------

[<Fact>]
let ``encodeCwd encodes POSIX path correctly`` () =
    encodeCwd "/Users/roman/dev/project" |> should equal "-Users-roman-dev-project"

[<Fact>]
let ``encodeCwd encodes Windows path correctly`` () =
    encodeCwd "C:\\Users\\roman" |> should equal "C_-Users-roman"

[<Fact>]
let ``encodeCwd result contains no path separators`` () =
    let encoded = encodeCwd "/some/deep/nested/path"
    encoded.Contains("/")  |> should equal false
    encoded.Contains("\\") |> should equal false

// ---------------------------------------------------------------------------
// 3. sessionPath: output is under ~/.fugue/sessions, contains encoded-cwd subdir,
//    ends with "<ulid>.jsonl". No actual file created.
// ---------------------------------------------------------------------------

[<Fact>]
let ``sessionPath produces correctly structured path`` () =
    let cwd  = "/Users/roman/project"
    let id   = "01HZ0000000000000000000000"
    let path = sessionPath cwd id
    let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
    path |> should startWith (Path.Combine(home, ".fugue", "sessions"))
    path.Contains(encodeCwd cwd) |> should equal true
    path |> should endWith (id + ".jsonl")
    // File must not have been created
    File.Exists path |> should equal false

// ---------------------------------------------------------------------------
// 4. findByIdInCwd — found: create the file, assert Some path returned.
// ---------------------------------------------------------------------------

[<Fact>]
let ``findByIdInCwd returns Some when file exists`` () =
    // We can't easily override the default ~/.fugue/sessions prefix,
    // so we test appendRecord + findByIdInCwd together using a real generated path.
    let cwd = Path.Combine(Path.GetTempPath(), "FugueFindTest_" + Path.GetRandomFileName())
    let id  = generateUlid ()
    try
        let path = sessionPath cwd id
        let dir : string | null = Path.GetDirectoryName path
        match dir with
        | null -> ()
        | d -> Directory.CreateDirectory d |> ignore
        File.WriteAllText(path, "")
        let result = findByIdInCwd cwd id
        result |> should not' (equal None)
        result |> Option.get |> should equal path
    finally
        // Clean up the created directory under ~/.fugue/sessions/<encoded-cwd>
        let path = sessionPath cwd id
        let dir  = Path.GetDirectoryName path
        match dir with
        | null -> ()
        | d -> if Directory.Exists d then Directory.Delete(d, recursive = true)

// ---------------------------------------------------------------------------
// 5. findByIdInCwd — not found: call with nonexistent ULID, assert None.
// ---------------------------------------------------------------------------

[<Fact>]
let ``findByIdInCwd returns None when file does not exist`` () =
    let cwd = "/nonexistent/project/path"
    let id  = "01HZ0000000000000000000000"
    findByIdInCwd cwd id |> should equal None

// ---------------------------------------------------------------------------
// 6. Resume reconstruct: write 4 records, reconstruct List<ChatMessage>,
//    call SetInMemoryChatHistory + TryGetInMemoryChatHistory and verify round-trip.
// ---------------------------------------------------------------------------

[<RequiresUnreferencedCode("Uses AgentSessionExtensions which touches STJ-backed AgentSessionStateBag")>]
[<RequiresDynamicCode("Uses AgentSessionExtensions which touches STJ-backed AgentSessionStateBag")>]
[<Fact>]
let ``resume reconstruct: SetInMemoryChatHistory round-trip succeeds`` () =
    let tmp = tmpDir ()
    try
        let file = Path.Combine(tmp, "resume.jsonl")
        let ts   = DateTimeOffset.UtcNow
        appendRecord file (SessionStart(ts, "/proj", "m", "p"))
        appendRecord file (UserTurn(ts.AddSeconds 1.0, "hello"))
        appendRecord file (AssistantTurn(ts.AddSeconds 2.0, "world"))
        appendRecord file (SessionEnd(ts.AddSeconds 3.0, 1, 3000L))

        let records = readRecords file
        // Reconstruct messages (same logic as Repl.fs /session resume handler)
        let messages = List<ChatMessage>()
        for r in records do
            match r with
            | UserTurn(_, content)      -> messages.Add(ChatMessage(ChatRole.User,      content))
            | AssistantTurn(_, content) -> messages.Add(ChatMessage(ChatRole.Assistant, content))
            | ToolCall(_, toolName, args, callId) ->
                messages.Add(ChatMessage(ChatRole.Assistant, $"[tool_call:{toolName} call_id:{callId}] {args}"))
            | ToolResult(_, callId, output, isError) ->
                let tag = if isError then " [error]" else ""
                messages.Add(ChatMessage(ChatRole.User, $"[tool_result:{callId}]{tag} {output}"))
            | _ -> ()
        messages.Count |> should equal 2
        messages.[0].Role    |> should equal ChatRole.User
        messages.[0].Text    |> should equal "hello"
        messages.[1].Role    |> should equal ChatRole.Assistant
        messages.[1].Text    |> should equal "world"

        // Verify SetInMemoryChatHistory + TryGetInMemoryChatHistory round-trip
        let session = Fugue.Tests.SessionAccessTests.TestAgentSession() :> AgentSession
        let stateKey : string | null = null
        let jsonOpts : System.Text.Json.JsonSerializerOptions | null = null
        AgentSessionExtensions.SetInMemoryChatHistory(session, messages, stateKey, jsonOpts)
        let mutable readBack = Unchecked.defaultof<List<ChatMessage>>
        let ok = AgentSessionExtensions.TryGetInMemoryChatHistory(session, &readBack, stateKey, jsonOpts)
        ok     |> should equal true
        readBack |> should not' (equal null)
        readBack.Count |> should equal 2
        readBack.[0].Text |> should equal "hello"
        readBack.[1].Text |> should equal "world"
    finally cleanup tmp

// ---------------------------------------------------------------------------
// 7. Malformed-line resilience: 3 valid + 1 malformed + 1 valid = 4 returned.
// ---------------------------------------------------------------------------

[<Fact>]
let ``readRecords skips malformed lines and returns only valid records`` () =
    let tmp = tmpDir ()
    try
        let file = Path.Combine(tmp, "malformed.jsonl")
        let ts = DateTimeOffset.UtcNow
        let lines = [
            serialize (UserTurn(ts, "first"))
            serialize (UserTurn(ts.AddSeconds 1.0, "second"))
            serialize (UserTurn(ts.AddSeconds 2.0, "third"))
            "{this is not valid json !!!"   // malformed
            serialize (UserTurn(ts.AddSeconds 4.0, "fifth"))
        ]
        File.WriteAllLines(file, lines)
        let result = readRecords file
        result |> List.length |> should equal 4  // malformed line skipped
        match result.[3] with
        | UserTurn(_, content) -> content |> should equal "fifth"
        | _ -> failwith "Expected UserTurn for last record"
    finally cleanup tmp
