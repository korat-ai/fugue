module Fugue.Tools.ReadTool

open System
open System.IO
open System.ComponentModel
open Fugue.Tools.PathSafety

let private formatBytes (n: int64) : string =
    if n >= 1_073_741_824L then sprintf "%.1f GB" (float n / 1_073_741_824.0)
    elif n >= 1_048_576L then sprintf "%.1f MB" (float n / 1_048_576.0)
    elif n >= 1024L then sprintf "%.1f KB" (float n / 1024.0)
    else sprintf "%d B" n

[<Description("Read a text file. Returns lines prefixed with 1-based line numbers (cat -n format).")>]
let read
    ([<Description("File path (absolute or relative to working directory).")>] cwd: string)
    ([<Description("File to read.")>] path: string)
    ([<Description("Skip this many leading lines (default 0).")>] offset: int option)
    ([<Description("Read at most this many lines (default unlimited).")>] limit: int option)
    : string =
    let full = resolve cwd path
    if not (isUnder cwd full) then
        raise (UnauthorizedAccessException(sprintf "path outside working directory: %s" path))
    if not (File.Exists full) then raise (FileNotFoundException("file not found", full))
    let sizeBytes = FileInfo(full).Length
    if sizeBytes > 1_048_576L then
        raise (InvalidOperationException(
            sprintf "file too large (%s) — use Grep to search or Glob to list sections"
                (formatBytes sizeBytes)))

    let allLines = File.ReadAllLines full
    let off = offset |> Option.defaultValue 0
    let take =
        match limit with
        | Some n -> n
        | None -> allLines.Length - off

    let body =
        seq {
            for i in 0 .. take - 1 do
                let idx = off + i
                if idx < allLines.Length then
                    let lineNo = idx + 1
                    yield (string lineNo).PadLeft(6) + "\t" + allLines.[idx]
        }
        |> String.concat "\n"

    // Nudge the model to use offset/limit on large full reads
    let hint =
        if offset.IsNone && limit.IsNone && allLines.Length > 300 then
            sprintf "\n\n[file has %d lines — re-read with offset+limit to focus on the relevant section]" allLines.Length
        else ""

    // Data file preview: prepend markdown table for CSV / JSON / JSONL when reading from the top.
    let preview =
        if offset.IsNone then
            let ext = (Path.GetExtension(full) |> Option.ofObj |> Option.defaultValue "").ToLowerInvariant()
            let rawContent = String.concat "\n" allLines
            match ext with
            | ".csv"  -> DataPreview.tryPreviewCsv  rawContent
            | ".jsonl"-> DataPreview.tryPreviewJsonl rawContent
            | ".json" -> DataPreview.tryPreviewJson  rawContent
            | _ -> None
        else None
    match preview with
    | Some p -> p + "\n\n---\n\n" + body + hint
    | None   -> body + hint
