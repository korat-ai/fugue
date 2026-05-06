/// Clipboard ring buffer — keeps the last 10 tool outputs accessible via @clip, @clip1..@clip10.
module Fugue.Cli.ClipRing

[<Literal>]
let Capacity = 10

let private ring : string array = Array.create Capacity ""
let mutable private count = 0   // total pushes, not capped

let private slot i = ring.[i % Capacity]

/// Push a new tool output onto the ring. Most recent is always at index 1 (@clip or @clip1).
let push (text: string) : unit =
    ring.[count % Capacity] <- text
    count <- count + 1

/// Get the Nth most recent item (1 = most recent, up to Capacity).
let get (n: int) : string option =
    if count = 0 || n < 1 || n > Capacity then None
    else
        let idx = (count - n + Capacity * 1000) % Capacity
        let v = ring.[idx]
        if v = "" then None else Some v

/// Reset (on /new).
let clear () : unit =
    for i in 0..Capacity - 1 do ring.[i] <- ""
    count <- 0
