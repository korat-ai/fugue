module Fugue.Tools.CircuitBreaker

open System.Collections.Generic

let private threshold = 5   // consecutive failures before opening the circuit

/// Per-session consecutive failure counter. Key = tool name.
let private failures = Dictionary<string, int>()
/// Open circuits. Key = tool name.
let private openCircuits = Dictionary<string, string>()

/// Record a failure for a tool. Returns true if the circuit just opened.
let recordFailure (toolName: string) : bool =
    let count = match failures.TryGetValue toolName with true, n -> n + 1 | _ -> 1
    failures.[toolName] <- count
    if count >= threshold && not (openCircuits.ContainsKey toolName) then
        let msg = sprintf "%s disabled after %d consecutive failures — fix the underlying issue or restart fugue" toolName count
        openCircuits.[toolName] <- msg
        true
    else false

/// Record a success: reset the failure counter.
let recordSuccess (toolName: string) : unit =
    failures.Remove toolName |> ignore
    openCircuits.Remove toolName |> ignore

/// Check if a circuit is open. Returns Some(reason) if open, None if closed.
let check (toolName: string) : string option =
    match openCircuits.TryGetValue toolName with
    | true, msg -> Some msg
    | _ -> None
