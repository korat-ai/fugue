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

// ---------------------------------------------------------------------------
// /gen generators
// ---------------------------------------------------------------------------

let private crockfordAlphabet = System.Collections.Generic.HashSet<char>("0123456789ABCDEFGHJKMNPQRSTVWXYZ")
let private nanoidAlphabet    = System.Collections.Generic.HashSet<char>("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-")

[<Fact>]
let ``generateUlid returns 26 chars in Crockford base32`` () =
    let v = generateUlid ()
    v |> should haveLength 26
    v |> Seq.forall (fun c -> crockfordAlphabet.Contains c) |> should equal true

[<Fact>]
let ``generateUlid produces unique values`` () =
    let a = generateUlid ()
    let b = generateUlid ()
    a |> should not' (equal b)

[<Fact>]
let ``generateNanoid returns 21 chars in URL-safe alphabet`` () =
    let v = generateNanoid ()
    v |> should haveLength 21
    v |> Seq.forall (fun c -> nanoidAlphabet.Contains c) |> should equal true

[<Fact>]
let ``generateNanoid produces unique values`` () =
    let a = generateNanoid ()
    let b = generateNanoid ()
    a |> should not' (equal b)

// ---------------------------------------------------------------------------
// /turns picker — content preview helper + JSONL round-trip
// ---------------------------------------------------------------------------

[<Fact>]
let ``previewTurnContent passes through short single-line content unchanged`` () =
    previewTurnContent "hello world" |> should equal "hello world"

[<Fact>]
let ``previewTurnContent clips at 60 chars`` () =
    let long = String.replicate 80 "x"
    let result = previewTurnContent long
    result.Length |> should equal 60

[<Fact>]
let ``previewTurnContent collapses newlines and tabs to spaces`` () =
    let result = previewTurnContent "line1\nline2\rline3\tend"
    result |> should equal "line1 line2 line3 end"
    result.Contains '\n' |> should equal false
    result.Contains '\r' |> should equal false
    result.Contains '\t' |> should equal false

[<Fact>]
let ``readRecords filtered to user/assistant turns yields correct count`` () =
    let dir = tmpDir ()
    try
        let path = Path.Combine(dir, "01TURNS00000000000000000001.jsonl")
        let ts = System.DateTimeOffset.UtcNow
        Fugue.Core.SessionPersistence.appendRecord path
            (Fugue.Core.SessionRecord.SessionStart(ts, dir, "model-x", "provider-x"))
        for i in 1..3 do
            Fugue.Core.SessionPersistence.appendRecord path
                (Fugue.Core.SessionRecord.UserTurn(ts, $"q{i}"))
        for i in 1..2 do
            Fugue.Core.SessionPersistence.appendRecord path
                (Fugue.Core.SessionRecord.AssistantTurn(ts, $"a{i}"))
        Fugue.Core.SessionPersistence.appendRecord path
            (Fugue.Core.SessionRecord.ToolCall(ts, "Read", "{}", "{}"))
        Fugue.Core.SessionPersistence.appendRecord path
            (Fugue.Core.SessionRecord.SessionEnd(ts, 5, 1000L))

        let records = Fugue.Core.SessionPersistence.readRecords path
        let turnCount =
            records
            |> List.choose (function
                | Fugue.Core.SessionRecord.UserTurn(_, c)      -> Some c
                | Fugue.Core.SessionRecord.AssistantTurn(_, c) -> Some c
                | _                                            -> None)
            |> List.length
        turnCount |> should equal 5
    finally
        cleanup dir

[<Fact>]
let ``empty session jsonl yields no turn items`` () =
    let dir = tmpDir ()
    try
        let path = Path.Combine(dir, "01EMPTY0000000000000000000.jsonl")
        let ts = System.DateTimeOffset.UtcNow
        Fugue.Core.SessionPersistence.appendRecord path
            (Fugue.Core.SessionRecord.SessionStart(ts, dir, "m", "p"))
        Fugue.Core.SessionPersistence.appendRecord path
            (Fugue.Core.SessionRecord.SessionEnd(ts, 0, 10L))
        let records = Fugue.Core.SessionPersistence.readRecords path
        let turns =
            records
            |> List.choose (function
                | Fugue.Core.SessionRecord.UserTurn(_, c) | Fugue.Core.SessionRecord.AssistantTurn(_, c) -> Some c
                | _ -> None)
        turns |> should be Empty
    finally
        cleanup dir
