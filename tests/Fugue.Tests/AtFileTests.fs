module Fugue.Tests.AtFileTests

open Xunit
open FsUnit.Xunit

// findAtTokens is private in Repl, so we replicate the pure parser logic here
// to keep the tests self-contained and AOT-safe (no Reflection needed).

/// Replicated pure parser (mirrors Repl.findAtTokens) for unit testing.
let private findAtTokens (input: string) : (int * int * string) list =
    let mutable i = 0
    let mutable results = []
    while i < input.Length do
        if input.[i] = '@' && (i = 0 || System.Char.IsWhiteSpace input.[i - 1]) then
            let start = i
            i <- i + 1
            let pathStart = i
            while i < input.Length && not (System.Char.IsWhiteSpace input.[i]) do
                i <- i + 1
            if i > pathStart then
                results <- (start, i - start, input.[start + 1 .. i - 1]) :: results
        else
            i <- i + 1
    List.rev results

[<Fact>]
let ``findAtTokens finds single token in middle of string`` () =
    let tokens = findAtTokens "hello @file.txt world"
    tokens |> should haveLength 1
    let (start, len, path) = tokens.[0]
    path  |> should equal "file.txt"
    start |> should equal 6
    len   |> should equal 9  // length of "@file.txt"

[<Fact>]
let ``findAtTokens returns empty list when no at-tokens present`` () =
    let tokens = findAtTokens "no at tokens here"
    tokens |> should be Empty

[<Fact>]
let ``findAtTokens finds both tokens when two at-file tokens are present`` () =
    let tokens = findAtTokens "@file1.txt @file2.txt"
    tokens |> should haveLength 2
    let (_, _, p1) = tokens.[0]
    let (_, _, p2) = tokens.[1]
    p1 |> should equal "file1.txt"
    p2 |> should equal "file2.txt"

[<Fact>]
let ``findAtTokens handles token at start of string`` () =
    let tokens = findAtTokens "@README.md"
    tokens |> should haveLength 1
    let (start, _, path) = tokens.[0]
    start |> should equal 0
    path  |> should equal "README.md"

[<Fact>]
let ``findAtTokens ignores at-sign in the middle of a word`` () =
    let tokens = findAtTokens "user@example.com is an email"
    tokens |> should be Empty

[<Fact>]
let ``findAtTokens handles path with directory separators`` () =
    let tokens = findAtTokens "look at @src/Fugue.Core/Config.fs please"
    tokens |> should haveLength 1
    let (_, _, path) = tokens.[0]
    path |> should equal "src/Fugue.Core/Config.fs"
