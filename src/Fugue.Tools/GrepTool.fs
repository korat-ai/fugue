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
    ([<Description("Word-boundary symbol search; skips comment lines.")>] symbol: bool option)
    : string =
    let r = root |> Option.map (resolve cwd) |> Option.defaultValue cwd
    if not (isUnder cwd r) then
        raise (UnauthorizedAccessException(sprintf "search root outside working directory: %s" (Option.defaultValue "." root)))
    let isSymbol = symbol = Some true
    let effectivePattern =
        if isSymbol then sprintf @"\b%s\b" (Regex.Escape pattern)
        else pattern
    let regex = Regex(effectivePattern, RegexOptions.Compiled)

    let files =
        let matcher = Matcher()
        matcher.AddInclude(glob |> Option.defaultValue "**/*") |> ignore
        let res =
            matcher.Execute(
                Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(DirectoryInfo r))
        res.Files |> Seq.map (fun f -> Path.Combine(r, f.Path))

    let isCommentLine (line: string) =
        let t = line.TrimStart()
        t.StartsWith("//") || t.StartsWith("#") || t.StartsWith("--")

    seq {
        for file in files do
            try
                let mutable lineNo = 0
                for line in File.ReadLines file do
                    lineNo <- lineNo + 1
                    if (not isSymbol || not (isCommentLine line)) && regex.IsMatch line then
                        let rel = Path.GetRelativePath(r, file).Replace('\\', '/')
                        yield rel + ":" + string lineNo + ":" + line
            with _ -> ()
    }
    |> String.concat "\n"
