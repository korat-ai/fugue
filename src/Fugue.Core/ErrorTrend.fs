module Fugue.Core.ErrorTrend

open System
open System.IO
open System.Text.Json

let private logPath () =
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".fugue", "errors.jsonl")

let private escape (s: string) =
    s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "")

let record (sessionId: string) (toolName: string) (errorMsg: string) : unit =
    try
        let path = logPath ()
        match Path.GetDirectoryName path |> Option.ofObj with
        | Some dir -> Directory.CreateDirectory dir |> ignore
        | None -> ()
        let ts   = DateTimeOffset.UtcNow.ToString("O")
        let short = if errorMsg.Length > 80 then errorMsg.[..79] else errorMsg
        let sig' = sprintf "%s:%s" toolName short
        let line = sprintf "{\"ts\":\"%s\",\"sig\":\"%s\",\"sess\":\"%s\"}" (escape ts) (escape sig') (escape sessionId)
        File.AppendAllLines(path, [| line |])
    with _ -> ()

/// Returns (signature, count) pairs for errors seen >= threshold times in the last windowDays.
let findRecurring (threshold: int) (windowDays: int) : (string * int) list =
    try
        let path = logPath ()
        if not (File.Exists path) then []
        else
            let cutoff  = DateTimeOffset.UtcNow.AddDays(-float windowDays)
            let counts  = Collections.Generic.Dictionary<string, int>()
            for line in File.ReadLines path do
                try
                    use doc  = JsonDocument.Parse(line)
                    let root = doc.RootElement
                    match root.TryGetProperty("ts") with
                    | true, tsEl ->
                        let tsStr = tsEl.GetString() |> Option.ofObj |> Option.defaultValue ""
                        let ts    = try DateTimeOffset.Parse(tsStr) with _ -> DateTimeOffset.MinValue
                        if ts >= cutoff then
                            match root.TryGetProperty("sig") with
                            | true, sigEl ->
                                let sig' = sigEl.GetString() |> Option.ofObj |> Option.defaultValue ""
                                if sig' <> "" then
                                    let cur = match counts.TryGetValue sig' with true, n -> n | _ -> 0
                                    counts.[sig'] <- cur + 1
                            | _ -> ()
                    | _ -> ()
                with _ -> ()
            counts
            |> Seq.filter  (fun kv -> kv.Value >= threshold)
            |> Seq.map     (fun kv -> kv.Key, kv.Value)
            |> Seq.sortByDescending snd
            |> Seq.toList
    with _ -> []
