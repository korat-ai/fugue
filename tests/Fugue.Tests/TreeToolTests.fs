module Fugue.Tests.TreeToolTests

open System.IO
open Xunit
open FsUnit.Xunit
open Fugue.Tools.TreeTool

[<Fact>]
let ``tree returns root directory name`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory tmpDir |> ignore
    try
        let result = tree tmpDir "." 3 None
        result |> should haveSubstring (Path.GetFileName tmpDir)
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``tree lists files in directory`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory tmpDir |> ignore
    try
        File.WriteAllText(Path.Combine(tmpDir, "hello.txt"), "hi")
        let result = tree tmpDir "." 3 None
        result |> should haveSubstring "hello.txt"
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``tree filters by glob`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory tmpDir |> ignore
    try
        File.WriteAllText(Path.Combine(tmpDir, "main.fs"), "")
        File.WriteAllText(Path.Combine(tmpDir, "notes.txt"), "")
        let result = tree tmpDir "." 3 (Some "*.fs")
        result |> should haveSubstring "main.fs"
        result |> should not' (haveSubstring "notes.txt")
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``tree returns error for nonexistent directory`` () =
    let result = tree "/tmp" "/tmp/does-not-exist-xyz" 3 None
    result |> should haveSubstring "Error"

[<Fact>]
let ``tree rejects path outside working directory`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory tmpDir |> ignore
    try
        let result = tree tmpDir "../etc" 3 None
        result |> should haveSubstring "path outside working directory"
    finally
        Directory.Delete(tmpDir, true)
