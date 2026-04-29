module Fugue.Tools.PathSafety

open System
open System.IO

let resolve (cwd: string) (path: string) : string =
    if Path.IsPathRooted path then
        Path.GetFullPath path
    else
        Path.GetFullPath(Path.Combine(cwd, path))

let isUnder (root: string) (path: string) : bool =
    let r = (Path.GetFullPath root).TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar
    let p = Path.GetFullPath path
    p.StartsWith(r, StringComparison.Ordinal) || p = r.TrimEnd(Path.DirectorySeparatorChar)
