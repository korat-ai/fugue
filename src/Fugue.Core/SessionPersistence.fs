module Fugue.Core.SessionPersistence

open System
open System.IO
open System.Security.Cryptography

/// Generate a ULID (Universally Unique Lexicographically Sortable Identifier).
/// Crockford Base32 encoding. AOT-clean — no reflection.
let generateUlid () : string =
    let alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
    use rng = RandomNumberGenerator.Create()
    let ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    let sb = Text.StringBuilder(26)
    let mutable t = ts
    let tsChars = Array.zeroCreate 10
    for i in 9 .. -1 .. 0 do
        tsChars.[i] <- alphabet.[int (t % 32L)]
        t <- t / 32L
    for c in tsChars do sb.Append c |> ignore
    let randBytes = Array.zeroCreate 10
    rng.GetBytes randBytes
    let mutable bits = 0UL
    let mutable bitsAvail = 0
    for b in randBytes do
        bits <- (bits <<< 8) ||| uint64 b
        bitsAvail <- bitsAvail + 8
        while bitsAvail >= 5 do
            bitsAvail <- bitsAvail - 5
            let idx = int ((bits >>> bitsAvail) &&& 0x1FUL)
            sb.Append alphabet.[idx] |> ignore
    sb.ToString().[..25]

/// Encode a filesystem path for use as a directory name under ~/.fugue/sessions/.
/// Rules (matches Claude Code's ~/.claude/projects/<encoded> convention):
///   1. Trim trailing / or \
///   2. Replace every / and \ with -
///   3. Replace : (Windows drive separator) with _
/// A leading - from an absolute POSIX path (e.g. /Users/roman → -Users-roman) is intentional.
let encodeCwd (cwd: string) : string =
    cwd.TrimEnd('/', '\\')
       .Replace('\\', '-')
       .Replace('/', '-')
       .Replace(':', '_')

/// Root directory for all session JSONL files.
let sessionsDir () : string =
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fugue", "sessions")

/// Full path for a session JSONL file.
/// Layout: ~/.fugue/sessions/<encoded-cwd>/<ulid>.jsonl
let sessionPath (cwd: string) (id: string) : string =
    let dir = sessionsDir ()
    let sub = encodeCwd cwd
    Path.Combine(dir, sub, id + ".jsonl")

/// Append a single SessionRecord as a JSONL line.
/// Fire-and-forget: IO errors are silently swallowed (same policy as SessionSummary.save).
/// NOTE: not safe for concurrent writes from two processes to the same file —
///       accepted risk in Phase 2 (same trade-off as SessionSummary.save).
let appendRecord (path: string) (record: SessionRecord.SessionRecord) : unit =
    try
        let dir : string | null = Path.GetDirectoryName path
        match dir with
        | null -> ()
        | d -> Directory.CreateDirectory d |> ignore
        let line = SessionRecord.serialize record
        File.AppendAllText(path, line + "\n")
    with _ -> ()

/// Read all SessionRecord lines from a JSONL file.
/// Malformed or unknown lines are silently skipped.
let readRecords (path: string) : SessionRecord.SessionRecord list =
    try
        if not (File.Exists path) then []
        else
            File.ReadAllLines path
            |> Array.toList
            |> List.choose (fun line ->
                let l = line.Trim()
                if l = "" then None
                else SessionRecord.deserialize l)
    with _ -> []

/// Find a session file by its ULID under the current-cwd subdirectory.
/// Returns Some(absolutePath) if the file exists, None otherwise.
/// Cross-cwd search is deferred to Phase 3.
let findByIdInCwd (cwd: string) (id: string) : string option =
    let path = sessionPath cwd id
    if File.Exists path then Some path else None
