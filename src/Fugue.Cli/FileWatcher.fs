module Fugue.Cli.FileWatcher

open System.Collections.Generic
open System.IO
open System.Threading.Channels

let private watchers = Dictionary<string, FileSystemWatcher * string>()
let private channel = Channel.CreateUnbounded<string>()

let remove (absPath: string) =
    match watchers.TryGetValue(absPath) with
    | true, (fsw, _) ->
        fsw.EnableRaisingEvents <- false
        fsw.Dispose()
        watchers.Remove(absPath) |> ignore
    | _ -> ()

let add (absPath: string) (command: string) =
    remove absPath
    let dir  = Path.GetDirectoryName(absPath) |> Option.ofObj |> Option.defaultValue "."
    let file = Path.GetFileName(absPath) |> Option.ofObj |> Option.defaultValue absPath
    let fsw  = new FileSystemWatcher(dir, file)
    fsw.NotifyFilter <- NotifyFilters.LastWrite
    fsw.Changed.Add(fun _ -> channel.Writer.TryWrite(command) |> ignore)
    fsw.EnableRaisingEvents <- true
    watchers.[absPath] <- (fsw, command)

let drain () : string list =
    let mutable cmds : string list = []
    let mutable cmd = ""
    while channel.Reader.TryRead(&cmd) do
        cmds <- cmd :: cmds
    List.rev cmds

let list () : (string * string) list =
    watchers
    |> Seq.map (fun kv -> kv.Key, snd kv.Value)
    |> Seq.toList

let clear () =
    for kv in (watchers |> Seq.toList) do
        let (fsw, _) = kv.Value
        fsw.EnableRaisingEvents <- false
        fsw.Dispose()
    watchers.Clear()
