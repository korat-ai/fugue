module Fugue.Core.Log

open System
open System.IO
open System.Threading

/// Lightweight append-only file logger. Writes one line per call to
/// ~/.fugue/log.txt. Safe to call from any thread (single global lock).
/// No-op if the directory cannot be created.

let private lockObj = obj ()

let private logFile () : string option =
    let envOrEmpty (k: string) : string =
        match Environment.GetEnvironmentVariable k |> Option.ofObj with
        | Some v -> v
        | None -> ""
    let home =
        let h = envOrEmpty "HOME"
        if h <> "" then h else envOrEmpty "USERPROFILE"
    if home = "" then None
    else
        let dir = Path.Combine(home, ".fugue")
        try
            Directory.CreateDirectory dir |> ignore
            Some(Path.Combine(dir, "log.txt"))
        with _ -> None

let private write (level: string) (category: string) (msg: string) =
    match logFile () with
    | None -> ()
    | Some path ->
        let line =
            sprintf "%s [%s] [%-4s] %s: %s%s"
                (DateTime.Now.ToString "HH:mm:ss.fff")
                (Thread.CurrentThread.ManagedThreadId.ToString().PadLeft(3))
                level
                category
                msg
                Environment.NewLine
        lock lockObj (fun () ->
            try File.AppendAllText(path, line) with _ -> ())

let info  (cat: string) (msg: string) = write "INFO" cat msg
let warn  (cat: string) (msg: string) = write "WARN" cat msg
let error (cat: string) (msg: string) = write "ERR " cat msg

/// Log an exception with class + message + first 5 stack frames.
let ex (cat: string) (prefix: string) (e: exn) =
    let frames =
        match e.StackTrace with
        | null -> ""
        | s ->
            s.Split('\n')
            |> Array.truncate 5
            |> String.concat " | "
    write "ERR " cat (sprintf "%s: %s: %s [%s]" prefix (e.GetType().Name) e.Message frames)

/// Mark the start of a session in the log file (separator + timestamp).
let session () =
    match logFile () with
    | None -> ()
    | Some path ->
        let banner =
            sprintf "%s===== fugue session started at %s =====%s"
                Environment.NewLine
                (DateTime.Now.ToString "yyyy-MM-dd HH:mm:ss")
                Environment.NewLine
        lock lockObj (fun () ->
            try File.AppendAllText(path, banner) with _ -> ())
