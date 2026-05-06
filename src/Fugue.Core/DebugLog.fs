module Fugue.Core.DebugLog

open System
open System.IO

// AOT-safe structured debug logging.
// Activated by FUGUE_DEBUG=1; writes newline-delimited JSON to ~/.fugue/debug.log.
// Never throws — debug logging must not affect the host process.

let private isEnabled () =
    Environment.GetEnvironmentVariable "FUGUE_DEBUG" = "1"

let private logPath () =
    let home =
        match Environment.GetEnvironmentVariable "HOME" |> Option.ofObj with
        | Some h when h <> "" -> h
        | _ ->
            match Environment.GetEnvironmentVariable "USERPROFILE" |> Option.ofObj with
            | Some h -> h
            | None -> "."
    Path.Combine(home, ".fugue", "debug.log")

/// Escape a string value for embedding inside a JSON string literal.
let private esc (s: string) : string =
    s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r")

/// Build a JSON object line from key-value string pairs.
/// Numbers are emitted unquoted when they parse cleanly as int64.
let private formatEntry (pairs: (string * string) list) : string =
    let field (k, v: string) =
        // Emit pure-integer values without quotes so JSON consumers see numbers.
        let mutable _n : int64 = 0L
        if Int64.TryParse(v, &_n) then sprintf "\"%s\":%s" k v
        else sprintf "\"%s\":\"%s\"" k (esc v)
    let body = pairs |> List.map field |> String.concat ","
    "{" + body + "}"

let private write (line: string) =
    if isEnabled () then
        try
            let path = logPath ()
            let dir = Path.GetDirectoryName(path) |> Option.ofObj |> Option.defaultValue "."
            Directory.CreateDirectory(dir) |> ignore
            File.AppendAllText(path, line + "\n")
        with _ -> ()

let private ts () = DateTimeOffset.UtcNow.ToString "O"

let sessionStart (provider: string) (model: string) (cwd: string) =
    write (formatEntry [ "event","session_start"; "ts",ts(); "provider",provider; "model",model; "cwd",cwd ])

let toolCall (name: string) (args: string) (resultPreview: string) (durationMs: int64) =
    let truncate (n: int) (s: string) = if s.Length <= n then s else s.[..n-1]
    write (formatEntry [
        "event","tool_call"
        "ts", ts()
        "name", name
        "args", truncate 500 args
        "result_preview", truncate 500 resultPreview
        "duration_ms", string durationMs ])

let turnCompleted (inputLen: int) (responseLen: int) (durationMs: int64) =
    write (formatEntry [
        "event","turn"
        "ts", ts()
        "input_chars", string inputLen
        "response_chars", string responseLen
        "duration_ms", string durationMs ])

let logError (msg: string) =
    write (formatEntry [ "event","error"; "ts",ts(); "message",msg ])
