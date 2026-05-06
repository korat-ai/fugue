module Fugue.Core.Annotation

open System
open System.IO
open System.Text.Json

type Rating = Up | Down

type TurnAnnotation = {
    TurnIndex   : int
    Rating      : Rating option
    Note        : string option
    AnnotatedAt : int64   // Unix ms
}

let private annotationsDir () =
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fugue", "sessions")

let private annotationsPath (cwd: string) : string =
    let key = SessionSummary.sessionKey cwd
    Path.Combine(annotationsDir (), key + "-annotations.ndjson")

let private ratingStr = function Up -> "up" | Down -> "down"
let private parseRating (s: string) =
    match s with "up" -> Some Up | "down" -> Some Down | _ -> None

let save (cwd: string) (ann: TurnAnnotation) : unit =
    try
        Directory.CreateDirectory(annotationsDir ()) |> ignore
        let path = annotationsPath cwd
        let ratingVal =
            ann.Rating |> Option.map (fun r -> sprintf ",\"rating\":\"%s\"" (ratingStr r)) |> Option.defaultValue ""
        let noteVal =
            ann.Note |> Option.map (fun n -> sprintf ",\"note\":%s" (JsonSerializer.Serialize n)) |> Option.defaultValue ""
        let line =
            sprintf "{\"turnIndex\":%d,\"annotatedAt\":%d%s%s}"
                ann.TurnIndex ann.AnnotatedAt ratingVal noteVal
        File.AppendAllText(path, line + "\n")
    with _ -> ()

let loadAll (cwd: string) : TurnAnnotation list =
    try
        let path = annotationsPath cwd
        if not (File.Exists path) then []
        else
            File.ReadAllLines path
            |> Array.filter (fun l -> l.Trim() <> "")
            |> Array.toList
            |> List.choose (fun line ->
                try
                    use doc  = JsonDocument.Parse line
                    let root = doc.RootElement
                    let get (k: string) =
                        let mutable p = Unchecked.defaultof<JsonElement>
                        if root.TryGetProperty(k, &p) then Some p else None
                    let turnIndex   = get "turnIndex"   |> Option.map (fun p -> p.GetInt32()) |> Option.defaultValue 0
                    let annotatedAt = get "annotatedAt" |> Option.map (fun p -> p.GetInt64()) |> Option.defaultValue 0L
                    let rating      = get "rating"      |> Option.bind (fun p -> p.GetString() |> Option.ofObj |> Option.bind parseRating)
                    let note        = get "note"        |> Option.bind (fun p -> p.GetString() |> Option.ofObj)
                    Some { TurnIndex = turnIndex; Rating = rating; Note = note; AnnotatedAt = annotatedAt }
                with _ -> None)
    with _ -> []

/// Count up/down ratings for the current session.
let counts (cwd: string) : int * int =
    let all = loadAll cwd
    let ups   = all |> List.filter (fun a -> a.Rating = Some Up)   |> List.length
    let downs = all |> List.filter (fun a -> a.Rating = Some Down) |> List.length
    ups, downs
