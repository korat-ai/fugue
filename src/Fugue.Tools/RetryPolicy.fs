module Fugue.Tools.RetryPolicy

open System
open System.IO
open System.Threading
open System.Threading.Tasks

/// Transient errors that are worth retrying: FS locking, sharing violations, temp permission denied.
let private isTransient (ex: exn) =
    match ex with
    | :? IOException as io ->
        let msg = io.Message.ToLowerInvariant()
        msg.Contains "sharing violation" || msg.Contains "locked" ||
        msg.Contains "being used by another process" || msg.Contains "temporarily unavailable"
    | :? UnauthorizedAccessException as ua ->
        let msg = ua.Message.ToLowerInvariant()
        msg.Contains "access to the path" && (msg.Contains "denied" || msg.Contains "lock")
    | _ -> false

/// Retry an async operation up to 3 times with exponential backoff (100ms, 200ms, 400ms).
/// Only retries on transient I/O errors.
let retryAsync (ct: CancellationToken) (f: unit -> Task<string>) : Task<string> = task {
    let delays = [| 100; 200; 400 |]
    let mutable attempt = 0
    let mutable result = ""
    let mutable stop = false
    while not stop do
        try
            let! r = f ()
            result <- r
            stop <- true
        with ex when isTransient ex && attempt < delays.Length ->
            do! Task.Delay(delays.[attempt], ct)
            attempt <- attempt + 1
    return result
}
