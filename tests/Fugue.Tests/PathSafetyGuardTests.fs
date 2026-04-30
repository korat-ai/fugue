module Fugue.Tests.PathSafetyGuardTests

open System
open System.IO
open Xunit
open FsUnit.Xunit

let private mkTmpDir () =
    let d = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory d |> ignore
    d

[<Fact>]
let ``ReadTool rejects path outside working directory`` () =
    let tmpDir = mkTmpDir ()
    try
        (fun () -> Fugue.Tools.ReadTool.read tmpDir "../../etc/passwd" None None |> ignore)
        |> should throw typeof<UnauthorizedAccessException>
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``ReadTool rejects absolute path outside working directory`` () =
    let tmpDir = mkTmpDir ()
    try
        (fun () -> Fugue.Tools.ReadTool.read tmpDir "/etc/passwd" None None |> ignore)
        |> should throw typeof<UnauthorizedAccessException>
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``WriteTool rejects path outside working directory`` () =
    let tmpDir = mkTmpDir ()
    try
        (fun () -> Fugue.Tools.WriteTool.write tmpDir "../../evil.txt" "bad" |> ignore)
        |> should throw typeof<UnauthorizedAccessException>
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``WriteTool rejects absolute path outside working directory`` () =
    let tmpDir = mkTmpDir ()
    try
        let outside = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
        (fun () -> Fugue.Tools.WriteTool.write tmpDir outside "bad" |> ignore)
        |> should throw typeof<UnauthorizedAccessException>
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``EditTool rejects path outside working directory`` () =
    let tmpDir = mkTmpDir ()
    try
        (fun () -> Fugue.Tools.EditTool.edit tmpDir "../../etc/passwd" "old" "new" None |> ignore)
        |> should throw typeof<UnauthorizedAccessException>
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``EditTool rejects absolute path outside working directory`` () =
    let tmpDir = mkTmpDir ()
    try
        let outside = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
        File.WriteAllText(outside, "content")
        try
            (fun () -> Fugue.Tools.EditTool.edit tmpDir outside "content" "other" None |> ignore)
            |> should throw typeof<UnauthorizedAccessException>
        finally
            File.Delete outside
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``GlobTool rejects root outside working directory`` () =
    let tmpDir = mkTmpDir ()
    let outsideDir = mkTmpDir ()
    try
        (fun () -> Fugue.Tools.GlobTool.glob tmpDir "**/*" (Some outsideDir) |> ignore)
        |> should throw typeof<UnauthorizedAccessException>
    finally
        Directory.Delete(tmpDir, true)
        Directory.Delete(outsideDir, true)

[<Fact>]
let ``GlobTool rejects traversal root outside working directory`` () =
    let tmpDir = mkTmpDir ()
    try
        (fun () -> Fugue.Tools.GlobTool.glob tmpDir "**/*" (Some "../../etc") |> ignore)
        |> should throw typeof<UnauthorizedAccessException>
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``GrepTool rejects root outside working directory`` () =
    let tmpDir = mkTmpDir ()
    let outsideDir = mkTmpDir ()
    try
        (fun () -> Fugue.Tools.GrepTool.grep tmpDir "pattern" (Some outsideDir) None |> ignore)
        |> should throw typeof<UnauthorizedAccessException>
    finally
        Directory.Delete(tmpDir, true)
        Directory.Delete(outsideDir, true)

[<Fact>]
let ``GrepTool rejects traversal root outside working directory`` () =
    let tmpDir = mkTmpDir ()
    try
        (fun () -> Fugue.Tools.GrepTool.grep tmpDir "pattern" (Some "../../etc") None |> ignore)
        |> should throw typeof<UnauthorizedAccessException>
    finally
        Directory.Delete(tmpDir, true)
