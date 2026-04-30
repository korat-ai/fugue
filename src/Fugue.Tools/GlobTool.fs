module Fugue.Tools.GlobTool

open System
open System.IO
open System.ComponentModel
open Microsoft.Extensions.FileSystemGlobbing
open Fugue.Tools.PathSafety

[<Description("Find files matching a glob pattern. Returns paths sorted by mtime descending, one per line.")>]
let glob
    ([<Description("Working directory.")>] cwd: string)
    ([<Description("Glob pattern (e.g. **/*.fs).")>] pattern: string)
    ([<Description("Search root, defaults to cwd.")>] root: string option)
    : string =
    let r = root |> Option.map (resolve cwd) |> Option.defaultValue cwd
    if not (isUnder cwd r) then
        raise (UnauthorizedAccessException(sprintf "search root outside working directory: %s" (Option.defaultValue "." root)))
    let matcher = Matcher()
    matcher.AddInclude pattern |> ignore
    let results = matcher.Execute(Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(DirectoryInfo r))
    results.Files
    |> Seq.map (fun f -> Path.Combine(r, f.Path))
    |> Seq.sortByDescending (fun p -> (FileInfo p).LastWriteTimeUtc)
    |> String.concat "\n"
