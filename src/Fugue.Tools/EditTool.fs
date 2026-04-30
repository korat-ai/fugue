module Fugue.Tools.EditTool

open System
open System.IO
open System.ComponentModel
open Fugue.Tools.PathSafety

[<Description("Edit a text file by exact-string replacement. Requires old_string to be unique unless replace_all is true.")>]
let edit
    ([<Description("Working directory.")>] cwd: string)
    ([<Description("File to modify.")>] path: string)
    ([<Description("Exact text to find (must occur once unless replace_all=true).")>] oldString: string)
    ([<Description("Replacement text.")>] newString: string)
    ([<Description("If true, replace every occurrence; default false.")>] replaceAll: bool option)
    : string =
    let full = resolve cwd path
    if not (isUnder cwd full) then
        raise (UnauthorizedAccessException(sprintf "path outside working directory: %s" path))
    if not (File.Exists full) then raise (FileNotFoundException("file not found", full))
    let original = File.ReadAllText full
    let count =
        let mutable c, i = 0, 0
        while i >= 0 do
            i <- original.IndexOf(oldString, i, StringComparison.Ordinal)
            if i >= 0 then
                c <- c + 1
                i <- i + max 1 oldString.Length
        c

    if count = 0 then raise (InvalidOperationException $"old_string not found in {full}")
    let all = replaceAll |> Option.defaultValue false
    if count > 1 && not all then
        raise (InvalidOperationException
            $"old_string occurs {count} times in {full}; pass replace_all=true or pick a unique snippet")

    let updated = original.Replace(oldString, newString)
    Fugue.Core.Checkpoint.snapshot full
    File.WriteAllText(full, updated)
    "edited " + full + " (" + string count + " replacement" + (if count = 1 then "" else "s") + ")"
