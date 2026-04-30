module Fugue.Core.SessionSummary

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

/// Stable key derived from the project's working directory.
let sessionKey (cwd: string) : string =
    let bytes = Encoding.UTF8.GetBytes(cwd.TrimEnd('/').TrimEnd('\\'))
    let hash  = SHA256.HashData bytes
    Convert.ToHexString(hash).[..15].ToLowerInvariant()

let private sessionsDir () =
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fugue", "sessions")

/// Append a session record to ~/.fugue/sessions/{key}.ndjson.
let save (cwd: string) (startedAt: int64) (summary: string) : unit =
    try
        let dir = sessionsDir ()
        Directory.CreateDirectory dir |> ignore
        let key  = sessionKey cwd
        let path = Path.Combine(dir, key + ".ndjson")
        // Manual JSON construction — AOT-safe, no reflection required.
        let escapedSummary = JsonSerializer.Serialize summary  // handles escaping of the string value
        let line =
            sprintf """{"sessionId":"%s","projectKey":"%s","startedAt":%d,"endedAt":%d,"summary":%s}"""
                (Guid.NewGuid().ToString "N") key startedAt
                (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) escapedSummary
        File.AppendAllText(path, line + "\n")
    with _ -> ()

/// Load the most recent session summary for this project.
let loadLast (cwd: string) : string option =
    try
        let dir  = sessionsDir ()
        let key  = sessionKey cwd
        let path = Path.Combine(dir, key + ".ndjson")
        if not (File.Exists path) then None
        else
            let lines = File.ReadAllLines path |> Array.filter (fun l -> l.Trim() <> "")
            match Array.tryLast lines with
            | None -> None
            | Some line ->
                try
                    use doc  = JsonDocument.Parse line
                    let root = doc.RootElement
                    let mutable prop = Unchecked.defaultof<JsonElement>
                    if root.TryGetProperty("summary", &prop) then
                        prop.GetString() |> Option.ofObj |> Option.filter (fun s -> s.Trim() <> "")
                    else None
                with _ -> None
    with _ -> None

/// Load the last `n` session summaries (most-recent first), with their end timestamps.
let loadHistory (cwd: string) (n: int) : (int64 * string) list =
    try
        let dir  = sessionsDir ()
        let key  = sessionKey cwd
        let path = Path.Combine(dir, key + ".ndjson")
        if not (File.Exists path) then []
        else
            File.ReadAllLines path
            |> Array.filter (fun l -> l.Trim() <> "")
            |> Array.rev
            |> Array.truncate n
            |> Array.toList
            |> List.choose (fun line ->
                try
                    use doc  = JsonDocument.Parse line
                    let root = doc.RootElement
                    let mutable sProp = Unchecked.defaultof<JsonElement>
                    let mutable tProp = Unchecked.defaultof<JsonElement>
                    let summaryOpt = if root.TryGetProperty("summary", &sProp) then sProp.GetString() |> Option.ofObj else None
                    let ts        = if root.TryGetProperty("endedAt",  &tProp) then tProp.GetInt64() else 0L
                    summaryOpt |> Option.filter (fun s -> s.Trim() <> "") |> Option.map (fun s -> (ts, s))
                with _ -> None)
    with _ -> []
