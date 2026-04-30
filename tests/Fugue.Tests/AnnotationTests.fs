[<Xunit.Collection("Sequential")>]
module Fugue.Tests.AnnotationTests

open System
open System.IO
open Xunit
open FsUnit.Xunit
open Fugue.Core.Annotation

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
let ``loadAll returns empty list for unknown project`` () =
    withTempHome (fun _ ->
        loadAll "/no/project" |> should be Empty)

[<Fact>]
let ``save then loadAll returns the annotation`` () =
    withTempHome (fun _ ->
        let ann = { TurnIndex = 3; Rating = Some Up; Note = None; AnnotatedAt = 0L }
        save "/test/proj" ann
        let result = loadAll "/test/proj"
        result.Length |> should equal 1
        result.[0].TurnIndex |> should equal 3
        result.[0].Rating    |> should equal (Some Up))

[<Fact>]
let ``counts returns correct up/down totals`` () =
    withTempHome (fun _ ->
        let ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        save "/p" { TurnIndex = 1; Rating = Some Up;   Note = None; AnnotatedAt = ts }
        save "/p" { TurnIndex = 2; Rating = Some Up;   Note = None; AnnotatedAt = ts }
        save "/p" { TurnIndex = 3; Rating = Some Down; Note = None; AnnotatedAt = ts }
        let ups, downs = counts "/p"
        ups   |> should equal 2
        downs |> should equal 1)

[<Fact>]
let ``note with special characters round-trips correctly`` () =
    withTempHome (fun _ ->
        let note = "Great \"answer\" with \\backslash"
        let ann = { TurnIndex = 1; Rating = None; Note = Some note; AnnotatedAt = 0L }
        save "/test" ann
        let result = loadAll "/test"
        result.[0].Note |> should equal (Some note))
