module Fugue.Tests.ReplTests

open System.IO
open Xunit
open FsUnit.Xunit
open Fugue.Cli.Repl
open Fugue.Core.Localization

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private tmpDir () =
    let d = Path.Combine(Path.GetTempPath(), "FugueReplTests_" + Path.GetRandomFileName())
    Directory.CreateDirectory d |> ignore
    d

let private cleanup (d: string) =
    if Directory.Exists d then Directory.Delete(d, recursive = true)

let private en = Fugue.Core.Localization.en

// ---------------------------------------------------------------------------
// tryFindTestFile
// ---------------------------------------------------------------------------

[<Fact>]
let ``tryFindTestFile returns None when tests dir does not exist`` () =
    let cwd = tmpDir ()
    try
        let result = tryFindTestFile cwd "Repl.fs"
        result |> should equal None
    finally cleanup cwd

[<Fact>]
let ``tryFindTestFile returns None when no matching test file exists`` () =
    let cwd = tmpDir ()
    try
        Directory.CreateDirectory(Path.Combine(cwd, "tests", "Fugue.Tests")) |> ignore
        let result = tryFindTestFile cwd "Repl.fs"
        result |> should equal None
    finally cleanup cwd

[<Fact>]
let ``tryFindTestFile finds <Stem>Tests.fs and returns relative path`` () =
    let cwd = tmpDir ()
    try
        let testsSubDir = Path.Combine(cwd, "tests", "Fugue.Tests")
        Directory.CreateDirectory testsSubDir |> ignore
        File.WriteAllText(Path.Combine(testsSubDir, "ReplTests.fs"), "// test")
        let srcFile = Path.Combine(cwd, "src", "Fugue.Cli", "Repl.fs")
        let result = tryFindTestFile cwd srcFile
        result |> should not' (equal None)
        result.Value |> should haveSubstring "ReplTests.fs"
    finally cleanup cwd

[<Fact>]
let ``tryFindTestFile finds <Stem>Test.fs when only that variant exists`` () =
    let cwd = tmpDir ()
    try
        let testsSubDir = Path.Combine(cwd, "tests", "Fugue.Tests")
        Directory.CreateDirectory testsSubDir |> ignore
        File.WriteAllText(Path.Combine(testsSubDir, "ReplTest.fs"), "// test")
        let srcFile = Path.Combine(cwd, "src", "Fugue.Cli", "Repl.fs")
        let result = tryFindTestFile cwd srcFile
        result |> should not' (equal None)
        result.Value |> should haveSubstring "ReplTest.fs"
    finally cleanup cwd

[<Fact>]
let ``tryFindTestFile prefers <Stem>Tests.fs over <Stem>Test.fs`` () =
    let cwd = tmpDir ()
    try
        let testsSubDir = Path.Combine(cwd, "tests", "Fugue.Tests")
        Directory.CreateDirectory testsSubDir |> ignore
        File.WriteAllText(Path.Combine(testsSubDir, "ReplTests.fs"), "// tests")
        File.WriteAllText(Path.Combine(testsSubDir, "ReplTest.fs"),  "// test")
        let srcFile = Path.Combine(cwd, "src", "Fugue.Cli", "Repl.fs")
        let result = tryFindTestFile cwd srcFile
        result |> should not' (equal None)
        result.Value |> should haveSubstring "ReplTests.fs"
    finally cleanup cwd

// ---------------------------------------------------------------------------
// expandAtFiles
// ---------------------------------------------------------------------------

[<Fact>]
let ``expandAtFiles returns input unchanged when no at-tokens present`` () =
    let cwd = tmpDir ()
    try
        let result = expandAtFiles cwd en "hello world"
        result |> should equal "hello world"
    finally cleanup cwd

[<Fact>]
let ``expandAtFiles inlines file contents for at-path token`` () =
    let cwd = tmpDir ()
    try
        let srcDir = Path.Combine(cwd, "src")
        Directory.CreateDirectory srcDir |> ignore
        File.WriteAllText(Path.Combine(srcDir, "Foo.fs"), "module Foo")
        let result = expandAtFiles cwd en "@src/Foo.fs"
        result |> should haveSubstring "src/Foo.fs"
        result |> should haveSubstring "module Foo"
    finally cleanup cwd

[<Fact>]
let ``expandAtFiles appends test hint when sibling test file exists`` () =
    let cwd = tmpDir ()
    try
        let srcDir   = Path.Combine(cwd, "src")
        let testsDir = Path.Combine(cwd, "tests", "Fugue.Tests")
        Directory.CreateDirectory srcDir   |> ignore
        Directory.CreateDirectory testsDir |> ignore
        File.WriteAllText(Path.Combine(srcDir,   "Foo.fs"),      "module Foo")
        File.WriteAllText(Path.Combine(testsDir, "FooTests.fs"), "// test")
        let result = expandAtFiles cwd en "@src/Foo.fs"
        result |> should haveSubstring "FooTests.fs"
        result |> should haveSubstring "@"
    finally cleanup cwd

[<Fact>]
let ``expandAtFiles does not append hint when no test file exists`` () =
    let cwd = tmpDir ()
    try
        let srcDir = Path.Combine(cwd, "src")
        Directory.CreateDirectory srcDir |> ignore
        File.WriteAllText(Path.Combine(srcDir, "Foo.fs"), "module Foo")
        let result = expandAtFiles cwd en "@src/Foo.fs"
        result |> should not' (haveSubstring "test file found")
    finally cleanup cwd

[<Fact>]
let ``expandAtFiles leaves token in place when file does not exist`` () =
    let cwd = tmpDir ()
    try
        let result = expandAtFiles cwd en "look at @nonexistent.fs please"
        result |> should haveSubstring "@nonexistent.fs"
    finally cleanup cwd

[<Fact>]
let ``expandAtFiles expands multiple at-tokens in one message`` () =
    let cwd = tmpDir ()
    try
        let srcDir = Path.Combine(cwd, "src")
        Directory.CreateDirectory srcDir |> ignore
        File.WriteAllText(Path.Combine(srcDir, "A.fs"), "module A")
        File.WriteAllText(Path.Combine(srcDir, "B.fs"), "module B")
        let result = expandAtFiles cwd en "@src/A.fs and @src/B.fs"
        result |> should haveSubstring "module A"
        result |> should haveSubstring "module B"
    finally cleanup cwd

[<Fact>]
let ``expandAtFiles test hint uses locale string`` () =
    let cwd = tmpDir ()
    try
        let srcDir   = Path.Combine(cwd, "src")
        let testsDir = Path.Combine(cwd, "tests", "Sub")
        Directory.CreateDirectory srcDir   |> ignore
        Directory.CreateDirectory testsDir |> ignore
        File.WriteAllText(Path.Combine(srcDir,   "Bar.fs"),      "module Bar")
        File.WriteAllText(Path.Combine(testsDir, "BarTests.fs"), "// test")
        let ru     = Fugue.Core.Localization.ru
        let result = expandAtFiles cwd ru "@src/Bar.fs"
        result |> should haveSubstring "найден тест-файл"
    finally cleanup cwd
