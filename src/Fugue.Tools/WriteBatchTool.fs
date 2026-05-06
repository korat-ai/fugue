module Fugue.Tools.WriteBatchTool

open System
open System.IO
open System.Text
open System.Text.Json
open System.ComponentModel
open Fugue.Tools.PathSafety

[<Description("Atomically write multiple files. Either all succeed or none are written.")>]
let writeBatch
    ([<Description("Working directory.")>] cwd: string)
    ([<Description("JSON array of {\"path\":\"...\",\"content\":\"...\"} objects.")>] filesJson: string)
    : string =
    use doc = JsonDocument.Parse(filesJson)
    let root = doc.RootElement
    if root.ValueKind <> JsonValueKind.Array then
        raise (ArgumentException "files must be a JSON array of {path, content} objects")
    let items =
        [ for el in root.EnumerateArray() do
            let getStr (k: string) =
                match el.TryGetProperty(k) with
                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj |> Option.defaultValue ""
                | _ -> ""
            yield getStr "path", getStr "content" ]
    if items |> List.exists (fun (p, _) -> p = "") then
        raise (ArgumentException "each item must have a non-empty path")
    // Validate all paths
    let resolved = items |> List.map (fun (p, c) ->
        let full = resolve cwd p
        if not (isUnder cwd full) then
            raise (UnauthorizedAccessException $"path outside working directory: {p}")
        full, c)
    // Stage to .fugue-tmp
    let staged = resolved |> List.map (fun (full, content) ->
        let tmp = full + ".fugue-tmp"
        let dir = Path.GetDirectoryName full |> Option.ofObj |> Option.defaultValue "."
        Directory.CreateDirectory dir |> ignore
        Fugue.Core.Checkpoint.snapshot full
        File.WriteAllText(tmp, content, UTF8Encoding(false))
        full, tmp, content)
    // Commit
    try
        staged |> List.iter (fun (full, tmp, _) -> File.Move(tmp, full, true))
        let count = List.length staged
        let totalBytes = staged |> List.sumBy (fun (_, _, c) -> Encoding.UTF8.GetByteCount c)
        $"wrote {count} file(s) ({totalBytes} bytes total)"
    with ex ->
        staged |> List.iter (fun (_, tmp, _) -> try File.Delete tmp with _ -> ())
        raise ex
