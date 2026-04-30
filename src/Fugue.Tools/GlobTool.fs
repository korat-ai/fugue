module Fugue.Tools.GlobTool

open System
open System.IO
open System.ComponentModel
open Microsoft.Extensions.FileSystemGlobbing
open Fugue.Tools.PathSafety

let rec expandBraces (pat: string) : string list =
    let start = pat.IndexOf '{'
    if start < 0 then [pat]
    else
        let finish = pat.IndexOf('}', start)
        if finish < 0 then [pat]
        else
            let prefix = pat.[..start-1]
            let inner  = pat.[start+1..finish-1]
            let suffix = pat.[finish+1..]
            inner.Split(',')
            |> Array.toList
            |> List.collect (fun alt -> expandBraces (prefix + alt.Trim() + suffix))

[<Description("Find files matching a glob pattern. Returns paths sorted by mtime descending, one per line.")>]
let glob
    ([<Description("Working directory.")>] cwd: string)
    ([<Description("Glob pattern (e.g. **/*.fs). Space-separated patterns, !prefix for exclusions, brace expansion supported.")>] pattern: string)
    ([<Description("Search root, defaults to cwd.")>] root: string option)
    : string =
    let r = root |> Option.map (resolve cwd) |> Option.defaultValue cwd
    if not (isUnder cwd r) then
        raise (UnauthorizedAccessException(sprintf "search root outside working directory: %s" (Option.defaultValue "." root)))
    let parts =
        pattern.Split([|' '|], StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList
    let includes, excludes =
        parts |> List.partition (fun p -> not (p.StartsWith "!"))
    let expandedIncludes =
        includes |> List.collect expandBraces
    let expandedExcludes =
        excludes |> List.map (fun p -> p.[1..]) |> List.collect expandBraces
    let finalIncludes =
        if expandedIncludes.IsEmpty then ["**/*"] else expandedIncludes
    let matcher = Matcher()
    finalIncludes |> List.iter (fun p -> matcher.AddInclude p |> ignore)
    expandedExcludes |> List.iter (fun p -> matcher.AddExclude p |> ignore)
    let results = matcher.Execute(Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(DirectoryInfo r))
    results.Files
    |> Seq.map (fun f -> Path.Combine(r, f.Path))
    |> Seq.sortByDescending (fun p -> (FileInfo p).LastWriteTimeUtc)
    |> String.concat "\n"
