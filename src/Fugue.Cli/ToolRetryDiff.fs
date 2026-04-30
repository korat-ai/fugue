module Fugue.Cli.ToolRetryDiff

open System.Text.Json

type DiffLine =
    | Added   of key: string * newVal: string
    | Removed of key: string * oldVal: string
    | Changed of key: string * oldVal: string * newVal: string

/// Parse a flat JSON object and return key → value string map.
let private parseFlat (json: string) : Map<string, string> =
    try
        use doc  = JsonDocument.Parse(json)
        let root = doc.RootElement
        if root.ValueKind <> JsonValueKind.Object then Map.empty
        else
            root.EnumerateObject()
            |> Seq.map (fun p ->
                let v =
                    match p.Value.ValueKind with
                    | JsonValueKind.String -> p.Value.GetString() |> Option.ofObj |> Option.defaultValue ""
                    | _                   -> p.Value.GetRawText()
                p.Name, v)
            |> Map.ofSeq
    with _ -> Map.empty

let diffArgs (prevJson: string) (nextJson: string) : DiffLine list =
    let prev = parseFlat prevJson
    let next = parseFlat nextJson
    let allKeys = Set.union (prev |> Map.keys |> Set.ofSeq) (next |> Map.keys |> Set.ofSeq) |> Set.toList |> List.sort
    [ for key in allKeys do
        match Map.tryFind key prev, Map.tryFind key next with
        | None,    Some nv           -> yield Added   (key, nv)
        | Some pv, None              -> yield Removed (key, pv)
        | Some pv, Some nv when pv <> nv -> yield Changed (key, pv, nv)
        | _ -> () ]

let formatDiff (diff: DiffLine list) : string =
    diff
    |> List.map (function
        | Added   (k, nv)      -> $"+ {k}: {nv}"
        | Removed (k, pv)      -> $"- {k}: {pv}"
        | Changed (k, pv, nv)  -> $"~ {k}: {pv} → {nv}")
    |> String.concat "\n"
