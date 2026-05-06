module Fugue.Tests.PathSafetySymlinkTests

open System.IO
open Xunit
open FsUnit.Xunit
open Fugue.Tools

[<Fact>]
let ``isUnder blocks symlink pointing outside cwd`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    let outsideDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory tmpDir |> ignore
    Directory.CreateDirectory outsideDir |> ignore
    try
        let linkPath = Path.Combine(tmpDir, "escape")
        try
            Directory.CreateSymbolicLink(linkPath, outsideDir) |> ignore
        with _ ->
            () // Skip if symlink creation fails (e.g., no privilege on Windows CI)
        if Directory.Exists linkPath then
            let resolved = PathSafety.resolve tmpDir "escape"
            let safe = PathSafety.isUnder tmpDir resolved
            safe |> should equal false
    finally
        try Directory.Delete(tmpDir, true) with _ -> ()
        try Directory.Delete(outsideDir, true) with _ -> ()
