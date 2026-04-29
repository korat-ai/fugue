module Fugue.Tools.GrepTool

open System
open System.IO
open System.Text.RegularExpressions
open System.ComponentModel
open Microsoft.Extensions.FileSystemGlobbing
open Fugue.Tools.PathSafety

[<Description("Search files for a regex pattern. Returns matching lines as `path:lineno:text` (one per line).")>]
let grep
    ([<Description("Working directory.")>] cwd: string)
    ([<Description("Regex pattern (.NET regex syntax).")>] pattern: string)
    ([<Description("Search root, defaults to cwd.")>] root: string option)
    ([<Description("Optional include glob (e.g. **/*.fs); default = all files.")>] glob: string option)
    : string =
    let r = root |> Option.map (resolve cwd) |> Option.defaultValue cwd
    let regex = Regex(pattern, RegexOptions.Compiled)

    let files =
        let matcher = Matcher()
        matcher.AddInclude(glob |> Option.defaultValue "**/*") |> ignore
        let res =
            matcher.Execute(
                Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(DirectoryInfo r))
        res.Files |> Seq.map (fun f -> Path.Combine(r, f.Path))

    seq {
        for file in files do
            try
                let mutable lineNo = 0
                for line in File.ReadLines file do
                    lineNo <- lineNo + 1
                    if regex.IsMatch line then
                        let rel = Path.GetRelativePath(r, file).Replace('\\', '/')
                        yield rel + ":" + string lineNo + ":" + line
            with _ -> ()
    }
    |> String.concat "\n"
