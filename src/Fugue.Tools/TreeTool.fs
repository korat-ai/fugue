module Fugue.Tools.TreeTool

open System
open System.IO
open Fugue.Tools.PathSafety

/// Render a directory tree rooted at `dir` up to `maxDepth` levels deep.
/// `glob` is an optional filename pattern to filter (e.g., "*.fs").
/// Returns the ASCII tree as a string.
let tree (cwd: string) (dir: string) (maxDepth: int) (glob: string option) : string =
    let root = PathSafety.resolve cwd dir
    if not (PathSafety.isUnder cwd root) then
        sprintf "Error: path outside working directory: %s" dir
    elif not (Directory.Exists root) then
        sprintf "Error: directory not found: %s" dir
    else
        let globError =
            glob |> Option.bind (fun g ->
                try Directory.GetFiles(root, g) |> ignore; None
                with :? ArgumentException as ex -> Some (sprintf "Error: invalid glob pattern '%s': %s" g ex.Message))
        match globError with
        | Some err -> err
        | None ->
            let sb = System.Text.StringBuilder()
            let rootName = Path.GetFileName root |> Option.ofObj |> Option.defaultValue root
            sb.AppendLine(rootName) |> ignore

            let rec renderDir (path: string) (prefix: string) (depth: int) =
                if depth >= maxDepth then () else
                let entries =
                    try
                        let dirs  = Directory.GetDirectories(path) |> Array.sort
                        let files =
                            match glob with
                            | None   -> Directory.GetFiles(path) |> Array.sort
                            | Some g -> Directory.GetFiles(path, g) |> Array.sort
                        Array.append dirs files
                    with _ -> [||]

                for i in 0 .. entries.Length - 1 do
                    let entry = entries.[i]
                    let isLast = (i = entries.Length - 1)
                    let connector   = if isLast then "└── " else "├── "
                    let childPrefix = if isLast then prefix + "    " else prefix + "│   "
                    let entryName = Path.GetFileName entry |> Option.ofObj |> Option.defaultValue entry
                    sb.AppendLine(prefix + connector + entryName) |> ignore
                    if Directory.Exists entry then
                        renderDir entry childPrefix (depth + 1)

            renderDir root "" 0
            sb.ToString().TrimEnd()
