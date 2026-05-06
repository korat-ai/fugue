module Fugue.Core.SearchIndex

open System
open System.IO
open Microsoft.Data.Sqlite

// Schema notes:
//   sessions     — metadata row per JSONL session file
//   session_fts  — FTS5 virtual table; session_id column UNINDEXED (not searchable)
//   *.ndjson files in the same sessionsDir() root are SessionSummary files — ignored here.
//   reindexFromJsonl uses "*.jsonl" filter (not "*") to avoid them.

type SessionRow = {
    Id        : string
    Cwd       : string
    StartedAt : string
    EndedAt   : string option
    TurnCount : int
    Summary   : string option
}

type SearchHit = {
    SessionId : string
    Snippet   : string
    Rank      : float
    Cwd       : string
    StartedAt : string
}

/// DB path resolution. Honors FUGUE_INDEX_DB env override (used by tests
/// for full isolation), falling back to ~/.fugue/index.db. Symmetric with
/// FUGUE_PROMPTS_DIR / FUGUE_CACHE_DIR / FUGUE_SESSIONS_DIR.
let dbPath () : string =
    match Environment.GetEnvironmentVariable "FUGUE_INDEX_DB" |> Option.ofObj with
    | Some p when p <> "" -> p
    | _ -> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fugue", "index.db")

let private openConn () : SqliteConnection =
    let path = dbPath ()
    let dir : string | null = Path.GetDirectoryName path
    match dir with
    | null -> ()
    | d -> Directory.CreateDirectory d |> ignore
    let conn = new SqliteConnection($"Data Source={path}")
    conn.Open()
    conn

let ensureSchema (conn: SqliteConnection) : unit =
    // WAL mode: single-writer, multiple-reader; survives crash better than DELETE journal.
    use pragmas = conn.CreateCommand()
    pragmas.CommandText <- "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;"
    pragmas.ExecuteNonQuery() |> ignore
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
CREATE TABLE IF NOT EXISTS sessions (
    id         TEXT PRIMARY KEY,
    cwd        TEXT NOT NULL,
    started_at TEXT NOT NULL,
    ended_at   TEXT,
    turn_count INTEGER NOT NULL DEFAULT 0,
    summary    TEXT
);
CREATE VIRTUAL TABLE IF NOT EXISTS session_fts USING fts5(
    session_id UNINDEXED,
    content,
    tokenize = 'unicode61 remove_diacritics 1'
);"""
    cmd.ExecuteNonQuery() |> ignore

let private withConn (f: SqliteConnection -> 'a) : 'a =
    use conn = openConn ()
    ensureSchema conn
    f conn

let upsertSession (row: SessionRow) : unit =
    try
        withConn (fun conn ->
            use cmd = conn.CreateCommand()
            cmd.CommandText <- """
INSERT INTO sessions (id, cwd, started_at, ended_at, turn_count, summary)
VALUES ($id, $cwd, $sa, $ea, $tc, $sum)
ON CONFLICT(id) DO UPDATE SET
    cwd        = excluded.cwd,
    started_at = excluded.started_at,
    ended_at   = excluded.ended_at,
    turn_count = excluded.turn_count,
    summary    = excluded.summary;"""
            let optVal (o: string option) : obj | null =
                match o with
                | Some s -> s
                | None   -> DBNull.Value
            cmd.Parameters.AddWithValue("$id",  row.Id)   |> ignore
            cmd.Parameters.AddWithValue("$cwd", row.Cwd)  |> ignore
            cmd.Parameters.AddWithValue("$sa",  row.StartedAt) |> ignore
            cmd.Parameters.AddWithValue("$ea",  optVal row.EndedAt)  |> ignore
            cmd.Parameters.AddWithValue("$tc",  row.TurnCount) |> ignore
            cmd.Parameters.AddWithValue("$sum", optVal row.Summary) |> ignore
            cmd.ExecuteNonQuery() |> ignore)
    with _ -> ()  // best-effort; same policy as SessionPersistence.appendRecord

let upsertContent (sessionId: string) (content: string) : unit =
    try
        withConn (fun conn ->
            // Delete existing FTS row(s) for this session, then insert fresh.
            use del = conn.CreateCommand()
            del.CommandText <- "DELETE FROM session_fts WHERE session_id = $id;"
            del.Parameters.AddWithValue("$id", sessionId) |> ignore
            del.ExecuteNonQuery() |> ignore
            use ins = conn.CreateCommand()
            ins.CommandText <- "INSERT INTO session_fts(session_id, content) VALUES ($id, $c);"
            ins.Parameters.AddWithValue("$id", sessionId) |> ignore
            ins.Parameters.AddWithValue("$c",  content)   |> ignore
            ins.ExecuteNonQuery() |> ignore)
    with _ -> ()

/// Wrap user query in FTS5 phrase-query syntax to handle hyphens, asterisks, etc.
/// Embedded double-quotes are doubled per SQLite FTS5 spec.
let private sanitizeFtsQuery (q: string) : string =
    let escaped = q.Replace("\"", "\"\"")
    $"\"{escaped}\""

let search (query: string) (limit: int) : SearchHit list =
    try
        if String.IsNullOrWhiteSpace query then []
        else
            withConn (fun conn ->
                use cmd = conn.CreateCommand()
                cmd.CommandText <- """
SELECT s.id, s.cwd, s.started_at,
       snippet(session_fts, 1, '«', '»', '…', 32) AS snip,
       bm25(session_fts) AS rank
FROM session_fts
JOIN sessions s ON session_fts.session_id = s.id
WHERE session_fts MATCH $q
ORDER BY rank
LIMIT $lim;"""
                cmd.Parameters.AddWithValue("$q",   sanitizeFtsQuery query) |> ignore
                cmd.Parameters.AddWithValue("$lim", limit) |> ignore
                use rdr = cmd.ExecuteReader()
                let mutable hits = []
                while rdr.Read() do
                    try
                        let hit = {
                            SessionId = rdr.GetString(0)
                            Cwd       = rdr.GetString(1)
                            StartedAt = rdr.GetString(2)
                            Snippet   = if rdr.IsDBNull(3) then "" else rdr.GetString(3)
                            Rank      = if rdr.IsDBNull(4) then 0.0 else rdr.GetDouble(4)
                        }
                        hits <- hit :: hits
                    with _ -> ()  // skip malformed rows
                List.rev hits)
    with _ -> []

let listForCwd (cwd: string) (limit: int) : SessionRow list =
    try
        withConn (fun conn ->
            use cmd = conn.CreateCommand()
            cmd.CommandText <- """
SELECT id, cwd, started_at, ended_at, turn_count, summary
FROM sessions
WHERE cwd = $cwd
ORDER BY started_at DESC
LIMIT $lim;"""
            cmd.Parameters.AddWithValue("$cwd", cwd)   |> ignore
            cmd.Parameters.AddWithValue("$lim", limit) |> ignore
            use rdr = cmd.ExecuteReader()
            let mutable rows = []
            while rdr.Read() do
                try
                    let row = {
                        Id        = rdr.GetString(0)
                        Cwd       = rdr.GetString(1)
                        StartedAt = rdr.GetString(2)
                        EndedAt   = if rdr.IsDBNull(3) then None else Some (rdr.GetString(3))
                        TurnCount = rdr.GetInt32(4)
                        Summary   = if rdr.IsDBNull(5) then None else Some (rdr.GetString(5))
                    }
                    rows <- row :: rows
                with _ -> ()  // skip malformed rows silently
            List.rev rows)
    with _ -> []

let listAll (limit: int) : SessionRow list =
    try
        withConn (fun conn ->
            use cmd = conn.CreateCommand()
            cmd.CommandText <- """
SELECT id, cwd, started_at, ended_at, turn_count, summary
FROM sessions
ORDER BY started_at DESC
LIMIT $lim;"""
            cmd.Parameters.AddWithValue("$lim", limit) |> ignore
            use rdr = cmd.ExecuteReader()
            let mutable rows = []
            while rdr.Read() do
                try
                    let row = {
                        Id        = rdr.GetString(0)
                        Cwd       = rdr.GetString(1)
                        StartedAt = rdr.GetString(2)
                        EndedAt   = if rdr.IsDBNull(3) then None else Some (rdr.GetString(3))
                        TurnCount = rdr.GetInt32(4)
                        Summary   = if rdr.IsDBNull(5) then None else Some (rdr.GetString(5))
                    }
                    rows <- row :: rows
                with _ -> ()
            List.rev rows)
    with _ -> []

/// Rebuild the search index from all *.jsonl session files found under sessionsDir().
/// Uses "*.jsonl" extension filter — *.ndjson files (SessionSummary) are intentionally excluded.
/// Returns Ok(count) on success, Error(message) on hard failure.
let reindexFromJsonl () : Result<int, string> =
    try
        let dir = SessionPersistence.sessionsDir ()
        if not (Directory.Exists dir) then Ok 0
        else
        let files = Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories) |> Seq.toList
        let mutable indexed = 0
        for file in files do
            try
                let records = SessionPersistence.readRecords file
                // Extract metadata from SessionStart / SessionEnd records.
                let startRec  = records |> List.tryPick (function SessionRecord.SessionStart(ts, cwd, _, _) -> Some (ts, cwd) | _ -> None)
                let endRec    = records |> List.tryPick (function SessionRecord.SessionEnd(ts, tc, _) -> Some (ts, tc) | _ -> None)
                match startRec with
                | None -> ()  // not a valid session file; skip
                | Some (startTs, cwd) ->
                    // ULID = filename stem; GetFileNameWithoutExtension may return null on unusual paths
                    match Path.GetFileNameWithoutExtension file |> Option.ofObj with
                    | None -> ()
                    | Some sessionId ->
                        let row = {
                            Id        = sessionId
                            Cwd       = cwd
                            StartedAt = startTs.ToString("o")
                            EndedAt   = endRec |> Option.map (fun (ts, _) -> ts.ToString("o"))
                            TurnCount = endRec |> Option.map snd |> Option.defaultValue 0
                            Summary   = None
                        }
                        upsertSession row
                        // Build content blob from user turns, assistant turns, tool names.
                        let content =
                            records
                            |> List.choose (function
                                | SessionRecord.UserTurn(_, c)                         -> Some c
                                | SessionRecord.AssistantTurn(_, c) when c <> ""      -> Some c
                                | SessionRecord.ToolCall(_, n, a, _)                  -> Some $"{n} {a}"
                                | _                                                    -> None)
                            |> String.concat " "
                        upsertContent sessionId content
                        indexed <- indexed + 1
            with _ -> ()  // skip unreadable files
        Ok indexed
    with ex ->
        Error ex.Message
