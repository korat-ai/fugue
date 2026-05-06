module Fugue.Core.Checkpoint

open System.IO
open System.Collections.Generic

// Per-operation undo stack: each Write/Edit call snapshots the previous file
// state. /undo restores the most recent snapshot.

type private FileSnapshot = {
    Path    : string
    Before  : string option  // None = file did not exist before write
}

let private stack     = Stack<FileSnapshot>()
let private maxDepth  = 25

/// Snapshot the current state of a path before a Write/Edit operation.
/// Called by WriteTool and EditTool before any modification.
let snapshot (absolutePath: string) : unit =
    let before =
        try if File.Exists absolutePath then Some (File.ReadAllText absolutePath) else None
        with _ -> None
    stack.Push { Path = absolutePath; Before = before }
    while stack.Count > maxDepth do
        // Stack has no remove-at-bottom; rebuild
        let arr = stack.ToArray()
        stack.Clear()
        for i in 0 .. arr.Length - 2 do stack.Push arr.[i]

/// Number of available undo steps.
let depth () = stack.Count

type UndoResult =
    | Restored of path: string
    | Deleted  of path: string
    | NothingToUndo

/// Undo the most recent Write/Edit operation.
let undo () : UndoResult =
    if stack.Count = 0 then NothingToUndo
    else
        let snap = stack.Pop()
        try
            match snap.Before with
            | None ->
                if File.Exists snap.Path then File.Delete snap.Path
                Deleted snap.Path
            | Some content ->
                let dir = Path.GetDirectoryName snap.Path |> Option.ofObj |> Option.defaultValue "."
                if dir <> "" then Directory.CreateDirectory dir |> ignore
                File.WriteAllText(snap.Path, content)
                Restored snap.Path
        with ex ->
            // Re-push so depth stays consistent, then propagate
            stack.Push snap
            raise ex

/// Discard all snapshots (e.g. on /new session reset).
let clear () = stack.Clear()
