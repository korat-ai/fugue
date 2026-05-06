module Fugue.Tests.SearchIndexTests

open System
open System.IO
open Xunit
open FsUnit.Xunit
open Fugue.Core.SearchIndex
open Fugue.Core.SessionPersistence
open Fugue.Core.SessionRecord

// ---------------------------------------------------------------------------
// Helper: each test gets its own temp SQLite DB via FUGUE_INDEX_DB env override
// ---------------------------------------------------------------------------

let private withTmpDb f =
    let tmp = Path.Combine(Path.GetTempPath(), "FugueIdxTests_" + Path.GetRandomFileName())
    Directory.CreateDirectory tmp |> ignore
    let dbFile = Path.Combine(tmp, "index.db")
    let prev = Environment.GetEnvironmentVariable "FUGUE_INDEX_DB"
    try
        Environment.SetEnvironmentVariable("FUGUE_INDEX_DB", dbFile)
        f tmp
    finally
        Environment.SetEnvironmentVariable("FUGUE_INDEX_DB", prev)
        try Directory.Delete(tmp, true) with _ -> ()

// ---------------------------------------------------------------------------
// 1. upsertSession round-trip
// ---------------------------------------------------------------------------

[<Fact>]
let ``upsertSession round-trip: listAll returns inserted row`` () =
    withTmpDb (fun _ ->
        let row = {
            Id        = "01HZ0000000000000000000001"
            Cwd       = "/my/project"
            StartedAt = "2026-05-01T10:00:00+00:00"
            EndedAt   = Some "2026-05-01T10:30:00+00:00"
            TurnCount = 3
            Summary   = None
        }
        upsertSession row
        let all = listAll 10
        all |> List.length |> should equal 1
        let r = all.[0]
        r.Id        |> should equal row.Id
        r.Cwd       |> should equal "/my/project"
        r.TurnCount |> should equal 3
        r.EndedAt   |> should equal (Some "2026-05-01T10:30:00+00:00"))

// ---------------------------------------------------------------------------
// 2. upsertContent + FTS5 search match
// ---------------------------------------------------------------------------

[<Fact>]
let ``upsertContent then search returns matching hit`` () =
    withTmpDb (fun _ ->
        let sid = "01HZ0000000000000000000002"
        upsertSession {
            Id        = sid
            Cwd       = "/proj/alpha"
            StartedAt = "2026-05-01T09:00:00+00:00"
            EndedAt   = None
            TurnCount = 1
            Summary   = None
        }
        upsertContent sid "implement recursive descent parser in F#"
        let hits = search "recursive" 10
        hits |> List.length |> should be (greaterThan 0)
        hits |> List.head |> (fun h -> h.SessionId) |> should equal sid)

// ---------------------------------------------------------------------------
// 3. BM25 ranking: two sessions, stronger match ranks first
// ---------------------------------------------------------------------------

[<Fact>]
let ``search ranking: session with more keyword occurrences ranks first`` () =
    withTmpDb (fun _ ->
        let sidA = "01HZ0000000000000000000003"
        let sidB = "01HZ0000000000000000000004"
        upsertSession { Id = sidA; Cwd = "/p"; StartedAt = "2026-05-01T08:00:00+00:00"; EndedAt = None; TurnCount = 0; Summary = None }
        upsertSession { Id = sidB; Cwd = "/p"; StartedAt = "2026-05-01T08:01:00+00:00"; EndedAt = None; TurnCount = 0; Summary = None }
        // A has "parser" once; B has "parser" four times — B should rank higher (more negative bm25)
        upsertContent sidA "parser implementation"
        upsertContent sidB "parser parser parser parser tokenizer"
        let hits = search "parser" 10
        hits |> List.length |> should equal 2
        hits.[0].SessionId |> should equal sidB)

// ---------------------------------------------------------------------------
// 4. Summary upsert round-trip
// ---------------------------------------------------------------------------

[<Fact>]
let ``upsertSession with summary: listForCwd returns summary field`` () =
    withTmpDb (fun _ ->
        let sid = "01HZ0000000000000000000005"
        upsertSession {
            Id        = sid
            Cwd       = "/project/beta"
            StartedAt = "2026-05-01T11:00:00+00:00"
            EndedAt   = Some "2026-05-01T11:15:00+00:00"
            TurnCount = 2
            Summary   = Some "Refactored the HTTP client module."
        }
        let rows = listForCwd "/project/beta" 10
        rows |> List.length |> should equal 1
        rows.[0].Summary |> should equal (Some "Refactored the HTTP client module."))

// ---------------------------------------------------------------------------
// 5. reindexFromJsonl from scratch
// ---------------------------------------------------------------------------

[<Fact>]
let ``reindexFromJsonl: indexes 3 JSONL session files`` () =
    withTmpDb (fun tmp ->
        // Build sessions dir structure inside tmp (not the real ~/.fugue)
        // We need a fake sessionsDir — override it by writing files where sessionsDir() points.
        // sessionsDir() = ~/.fugue/sessions but with FUGUE_INDEX_DB set the DB is in tmp.
        // We still need to write JSONL files somewhere reindexFromJsonl can find them.
        // sessionsDir() reads GetFolderPath(UserProfile) which is fixed; we write there.
        let sessDir = Path.Combine(tmp, "sessions", "-tmp-proj")
        Directory.CreateDirectory sessDir |> ignore
        // Write JSONL files using sessionPath directly — but that uses the real sessionsDir.
        // Instead, write directly to our tmp dir and use readRecords to verify independently.
        // reindexFromJsonl uses sessionsDir() from SessionPersistence — we test it separately.
        // For this test: write files to tmp that match the *.jsonl pattern,
        // then call reindexFromJsonl() which will scan the real sessionsDir (may be empty).
        // Write to the actual ~/.fugue/sessions dir with a unique subdirectory and clean up.
        let realSessionsDir = sessionsDir ()
        let testSubDir = Path.Combine(realSessionsDir, "test-reindex-" + Path.GetRandomFileName())
        Directory.CreateDirectory testSubDir |> ignore
        try
            let ts = DateTimeOffset.Parse "2026-05-01T12:00:00+00:00"
            for i in 1..3 do
                let ulid = $"01TEST000000000000000000{i:D2}"
                let path = Path.Combine(testSubDir, ulid + ".jsonl")
                appendRecord path (SessionStart(ts.AddMinutes(float i), "/tmp/proj", "m", "p"))
                appendRecord path (UserTurn(ts.AddMinutes(float i + 0.5), $"question {i}"))
                appendRecord path (SessionEnd(ts.AddMinutes(float i + 1.0), 1, 60000L))
            let result = reindexFromJsonl ()
            match result with
            | Error e  -> failwith $"reindex failed: {e}"
            | Ok count -> count |> should be (greaterThanOrEqualTo 3)
        finally
            try Directory.Delete(testSubDir, true) with _ -> ())

// ---------------------------------------------------------------------------
// 6. Missing DB: search returns [] without throwing
// ---------------------------------------------------------------------------

[<Fact>]
let ``search on fresh DB returns empty list without throwing`` () =
    withTmpDb (fun _ ->
        // No content inserted; DB file just created with schema.
        let hits = search "anything" 10
        hits |> should be Empty)

// ---------------------------------------------------------------------------
// 7. listAll returns valid rows (resilience baseline)
// ---------------------------------------------------------------------------

[<Fact>]
let ``listAll returns valid rows after upsert`` () =
    withTmpDb (fun _ ->
        let row = {
            Id        = "01HZ0000000000000000000007"
            Cwd       = "/good/project"
            StartedAt = "2026-05-01T13:00:00+00:00"
            EndedAt   = None
            TurnCount = 5
            Summary   = None
        }
        upsertSession row
        let rows = listAll 10
        rows |> List.length |> should be (greaterThan 0)
        rows |> List.exists (fun r -> r.Id = "01HZ0000000000000000000007") |> should equal true)
