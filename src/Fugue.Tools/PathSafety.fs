module Fugue.Tools.PathSafety

open System
open System.IO

let private realPath (p: string) : string =
    // Resolve symlinks if the path exists; fall back to Path.GetFullPath if not.
    try
        let resolveInfo (fi: FileSystemInfo) =
            match fi.ResolveLinkTarget(returnFinalTarget = true) with
            | null -> p   // not a symlink
            | target -> Path.GetFullPath target.FullName
        if Directory.Exists p then resolveInfo (DirectoryInfo(p))
        elif File.Exists p then resolveInfo (FileInfo(p))
        else p
    with _ -> p

let resolve (cwd: string) (path: string) : string =
    let full =
        if Path.IsPathRooted path then Path.GetFullPath path
        else Path.GetFullPath(Path.Combine(cwd, path))
    realPath full

let isUnder (root: string) (path: string) : bool =
    let r = (Path.GetFullPath root).TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar
    let p = Path.GetFullPath path
    p.StartsWith(r, StringComparison.Ordinal) || p = r.TrimEnd(Path.DirectorySeparatorChar)
