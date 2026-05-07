module Fugue.Tests.PathSafetyPropertyTests

/// Property-style tests for PathSafety.isUnder — the security gate for all file tools.
///
/// These tests verify the four key invariants using a set of concrete representative
/// inputs rather than random generation, since FsCheck.Xunit discovery doesn't work
/// with xunit.runner.visualstudio 3.x in this project.

open System.IO
open Xunit
open FsUnit.Xunit
open Fugue.Tools.PathSafety

let private mkTmpDir () =
    let d = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory d |> ignore
    d

[<Fact>]
let ``isUnder allows a single-level subdirectory of cwd`` () =
    let cwd = mkTmpDir ()
    try
        let sub = Path.Combine(cwd, "subdir", "file.txt")
        isUnder cwd sub |> should equal true
    finally
        Directory.Delete(cwd, true)

[<Fact>]
let ``isUnder allows a deeply nested subdirectory of cwd`` () =
    let cwd = mkTmpDir ()
    try
        let sub = Path.Combine(cwd, "a", "b", "c", "file.txt")
        isUnder cwd sub |> should equal true
    finally
        Directory.Delete(cwd, true)

[<Fact>]
let ``isUnder blocks a path using .. to escape cwd`` () =
    let cwd = mkTmpDir ()
    try
        let escape = Path.Combine(cwd, "..", "escaped_file.txt")
        isUnder cwd escape |> should equal false
    finally
        Directory.Delete(cwd, true)

[<Fact>]
let ``isUnder blocks a path using multiple .. segments to escape cwd`` () =
    let cwd = mkTmpDir ()
    try
        let escape = Path.Combine(cwd, "sub", "..", "..", "escaped_file.txt")
        isUnder cwd escape |> should equal false
    finally
        Directory.Delete(cwd, true)

[<Fact>]
let ``isUnder allows cwd itself`` () =
    let cwd = mkTmpDir ()
    try
        isUnder cwd cwd |> should equal true
    finally
        Directory.Delete(cwd, true)

[<Fact>]
let ``isUnder blocks an absolute path that is a sibling of cwd`` () =
    let tmpRoot = Path.GetTempPath()
    let cwd = Path.Combine(tmpRoot, Path.GetRandomFileName())
    let sibling = Path.Combine(tmpRoot, Path.GetRandomFileName())
    Directory.CreateDirectory cwd |> ignore
    try
        let outsidePath = Path.Combine(sibling, "secret.txt")
        isUnder cwd outsidePath |> should equal false
    finally
        Directory.Delete(cwd, true)

[<Fact>]
let ``isUnder blocks /etc/passwd when cwd is a temp dir`` () =
    let cwd = mkTmpDir ()
    try
        isUnder cwd "/etc/passwd" |> should equal false
    finally
        Directory.Delete(cwd, true)
