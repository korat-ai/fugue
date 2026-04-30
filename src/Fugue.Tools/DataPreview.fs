module Fugue.Tools.DataPreview

open System
open System.Text
open System.Text.Json

let private maxPreviewRows = 20

/// Parse a CSV line respecting double-quoted fields.
let private parseCsvLine (line: string) : string list =
    let result = ResizeArray<string>()
    let field  = StringBuilder()
    let mutable i = 0
    while i < line.Length do
        if line.[i] = '"' then
            i <- i + 1
            let mutable inQuote = true
            while i < line.Length && inQuote do
                if line.[i] = '"' then
                    if i + 1 < line.Length && line.[i + 1] = '"' then
                        field.Append '"' |> ignore
                        i <- i + 2
                    else
                        inQuote <- false
                        i <- i + 1
                else
                    field.Append line.[i] |> ignore
                    i <- i + 1
            // after closing quote, skip comma if present
            if i < line.Length && line.[i] = ',' then
                result.Add(field.ToString())
                field.Clear() |> ignore
                i <- i + 1
        elif line.[i] = ',' then
            result.Add(field.ToString())
            field.Clear() |> ignore
            i <- i + 1
        else
            field.Append line.[i] |> ignore
            i <- i + 1
    result.Add(field.ToString())
    result |> Seq.toList

/// Render rows as a markdown table. Truncates cell values to 40 chars.
let private markdownTable (headers: string list) (rows: string list list) : string =
    let truncate (s: string) = if s.Length > 40 then s.[..37] + "…" else s
    let cols = headers.Length
    let widths =
        Array.init cols (fun i ->
            rows
            |> List.map (fun row -> if i < row.Length then (truncate row.[i]).Length else 0)
            |> List.fold max (if i < headers.Length then headers.[i].Length else 3))
    let pad (s: string) w = s.PadRight(w)
    let headerRow = headers |> List.mapi (fun i h -> pad h widths.[i]) |> String.concat " | "
    let sep       = widths |> Array.map (fun w -> String.replicate w "-") |> Array.toList |> String.concat " | "
    let dataRows  = rows |> List.map (fun row ->
        [ for i in 0 .. cols - 1 ->
            let v = if i < row.Length then truncate row.[i] else ""
            pad v widths.[i] ] |> String.concat " | ")
    "| " + headerRow + " |\n| " + sep + " |\n" +
        (dataRows |> List.map (fun r -> "| " + r + " |") |> String.concat "\n")

let tryPreviewCsv (content: string) : string option =
    let lines = content.Split('\n') |> Array.map (fun l -> l.TrimEnd('\r'))
    if lines.Length < 2 then None
    else
        let headers = parseCsvLine lines.[0]
        if headers.Length < 2 then None
        else
            let dataLines = lines.[1..] |> Array.filter (fun l -> l.Trim() <> "")
            let rows =
                dataLines.[..min (maxPreviewRows - 1) (dataLines.Length - 1)]
                |> Array.map parseCsvLine
                |> Array.toList
            let total = dataLines.Length
            let preview = markdownTable headers rows
            let note =
                if total > maxPreviewRows then
                    sprintf "\n\n_(preview: %d of %d rows)_" maxPreviewRows total
                else sprintf "\n\n_(%d rows)_" total
            Some (preview + note)

let tryPreviewJsonl (content: string) : string option =
    let lines =
        content.Split('\n')
        |> Array.map (fun l -> l.Trim())
        |> Array.filter (fun l -> l.StartsWith '{')
    if lines.Length = 0 then None
    else
        let sample = lines.[0]
        try
            use doc = JsonDocument.Parse sample
            let keys = doc.RootElement.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Seq.toList
            if keys.IsEmpty then None
            else
                let rows =
                    lines.[..min (maxPreviewRows - 1) (lines.Length - 1)]
                    |> Array.choose (fun l ->
                        try
                            use d = JsonDocument.Parse l
                            let vals = keys |> List.map (fun k ->
                                match d.RootElement.TryGetProperty k with
                                | true, v -> v.ToString()
                                | _ -> "")
                            Some vals
                        with _ -> None)
                    |> Array.toList
                let preview = markdownTable keys rows
                let note =
                    if lines.Length > maxPreviewRows then sprintf "\n\n_(preview: %d of %d records)_" maxPreviewRows lines.Length
                    else sprintf "\n\n_(%d records)_" lines.Length
                Some (preview + note)
        with _ -> None

let tryPreviewJson (content: string) : string option =
    try
        use doc = JsonDocument.Parse content
        let kind =
            match doc.RootElement.ValueKind with
            | JsonValueKind.Array ->
                let count = doc.RootElement.GetArrayLength()
                if count > 0 then
                    let first = doc.RootElement.[0]
                    if first.ValueKind = JsonValueKind.Object then
                        let keys = first.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Seq.toList
                        let rows =
                            seq { for i in 0 .. min (maxPreviewRows - 1) (count - 1) -> doc.RootElement.[i] }
                            |> Seq.choose (fun el ->
                                if el.ValueKind = JsonValueKind.Object then
                                    Some (keys |> List.map (fun k ->
                                        match el.TryGetProperty k with
                                        | true, v -> v.ToString()
                                        | _ -> ""))
                                else None)
                            |> Seq.toList
                        let preview = markdownTable keys rows
                        let note =
                            if count > maxPreviewRows then sprintf "\n\n_(preview: %d of %d items)_" maxPreviewRows count
                            else sprintf "\n\n_(%d items)_" count
                        Some (preview + note)
                    else None
                else None
            | _ -> None
        kind
    with _ -> None
