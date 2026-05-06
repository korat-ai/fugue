[<Xunit.Collection("Sequential")>]
module Fugue.Tests.SessionSummaryTests

open System
open System.IO
open Xunit
open FsUnit.Xunit
open Fugue.Core.SessionSummary

/// Redirect ~/.fugue/sessions to a tmp dir for test isolation.
let private withTempHome f =
    let tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory tmp |> ignore
    let prev = Environment.GetEnvironmentVariable "HOME"
    try
        Environment.SetEnvironmentVariable("HOME", tmp)
        f tmp
    finally
        Environment.SetEnvironmentVariable("HOME", prev)
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``loadLast returns None for unknown project`` () =
    withTempHome (fun _ ->
        loadLast "/some/project" |> should equal None)

[<Fact>]
let ``save then loadLast returns the saved summary`` () =
    withTempHome (fun _ ->
        let cwd = "/test/project"
        let ts  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1000L
        save cwd ts "Session A. Did something. Left something incomplete."
        match loadLast cwd with
        | None        -> failwith "expected Some"
        | Some actual -> actual |> should haveSubstring "Session A")

[<Fact>]
let ``loadLast returns the most recent entry`` () =
    withTempHome (fun _ ->
        let cwd = "/test/project"
        let ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        save cwd (ts - 2000L) "First summary."
        save cwd (ts - 1000L) "Second summary."
        match loadLast cwd with
        | None        -> failwith "expected Some"
        | Some actual -> actual |> should haveSubstring "Second")

[<Fact>]
let ``loadHistory returns entries most-recent first`` () =
    withTempHome (fun _ ->
        let cwd = "/test/project"
        let ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        save cwd (ts - 3000L) "Alpha."
        save cwd (ts - 2000L) "Beta."
        save cwd (ts - 1000L) "Gamma."
        let hist = loadHistory cwd 3
        hist.Length |> should equal 3
        snd hist.[0] |> should haveSubstring "Gamma"
        snd hist.[1] |> should haveSubstring "Beta"
        snd hist.[2] |> should haveSubstring "Alpha")

[<Fact>]
let ``loadHistory respects n limit`` () =
    withTempHome (fun _ ->
        let cwd = "/test/project"
        let ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        for i in 1..5 do
            save cwd (ts - int64 (i * 1000)) (sprintf "Entry %d." i)
        let hist = loadHistory cwd 2
        hist.Length |> should equal 2)

[<Fact>]
let ``sessionKey is stable for the same path`` () =
    sessionKey "/foo/bar" |> should equal (sessionKey "/foo/bar")

[<Fact>]
let ``sessionKey differs for different paths`` () =
    sessionKey "/foo/bar" |> should not' (equal (sessionKey "/foo/baz"))

[<Fact>]
let ``summary with special characters round-trips correctly`` () =
    withTempHome (fun _ ->
        let cwd = "/test/project"
        let ts  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 500L
        let text = "Used \"quotes\" and \\backslashes\\ here."
        save cwd ts text
        match loadLast cwd with
        | None        -> failwith "expected Some"
        | Some actual -> actual |> should equal text)
