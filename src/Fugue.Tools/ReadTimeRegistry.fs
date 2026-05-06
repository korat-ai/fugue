module Fugue.Tools.ReadTimeRegistry

open System
open System.Collections.Concurrent

let private registry = ConcurrentDictionary<string, DateTime>()

let recordRead (path: string) =
    try registry.[path] <- IO.FileInfo(path).LastWriteTimeUtc
    with _ -> ()

let checkConflict (path: string) : string option =
    match registry.TryGetValue path with
    | true, t ->
        try
            let current = IO.FileInfo(path).LastWriteTimeUtc
            if current > t then
                Some (sprintf "⚠ conflict: %s changed on disk since last Read (%s → %s UTC)"
                        (IO.Path.GetFileName path)
                        (t.ToString "HH:mm:ss")
                        (current.ToString "HH:mm:ss"))
            else None
        with _ -> None
    | _ -> None

let reset () = registry.Clear()
