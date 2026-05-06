module Fugue.Cli.Macros

open System.Collections.Generic

let private store = Dictionary<string, string list>()
let mutable private recording : (string * string list) option = None
let private playQueue = System.Collections.Generic.Queue<string>()

let isRecording () = recording.IsSome

let startRecording (name: string) =
    recording <- Some (name, [])

let recordStep (step: string) =
    recording <- recording |> Option.map (fun (n, steps) -> n, steps @ [step])

let stopRecording () : string option =
    match recording with
    | None -> None
    | Some (name, steps) ->
        store.[name] <- steps
        recording <- None
        Some name

let enqueuePlay (name: string) : bool =
    match store.TryGetValue(name) with
    | true, steps ->
        for step in steps do playQueue.Enqueue(step)
        true
    | _ -> false

let dequeueStep () : string option =
    if playQueue.Count > 0 then Some (playQueue.Dequeue()) else None

let list () : (string * int) list =
    store
    |> Seq.map (fun kv -> kv.Key, kv.Value.Length)
    |> Seq.toList

let remove (name: string) : bool =
    store.Remove(name)

let clear () =
    store.Clear()
    recording <- None
    playQueue.Clear()
