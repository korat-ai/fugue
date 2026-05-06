module Fugue.Tests.InputSanitizeTests

open Xunit
open FsUnit.Xunit
open Fugue.Cli.ReadLine

[<Fact>]
let ``normalize replaces CRLF with LF (not double LF)`` () =
    normalizeInput "a\r\nb" |> should equal "a\nb"

[<Fact>]
let ``normalize replaces standalone CR with LF`` () =
    normalizeInput "a\rb" |> should equal "a\nb"

[<Fact>]
let ``normalize replaces LINE SEPARATOR U+2028 with LF`` () =
    normalizeInput ("a" + string (char 0x2028) + "b") |> should equal "a\nb"

[<Fact>]
let ``normalize replaces PARAGRAPH SEPARATOR U+2029 with LF`` () =
    normalizeInput ("a" + string (char 0x2029) + "b") |> should equal "a\nb"

[<Fact>]
let ``normalize leaves plain text unchanged`` () =
    normalizeInput "hello world" |> should equal "hello world"

[<Fact>]
let ``hasZeroWidth true for U+200B ZERO WIDTH SPACE`` () =
    hasZeroWidth ("a" + string (char 0x200B) + "b") |> should equal true

[<Fact>]
let ``hasZeroWidth true for U+200C ZERO WIDTH NON-JOINER`` () =
    hasZeroWidth ("a" + string (char 0x200C) + "b") |> should equal true

[<Fact>]
let ``hasZeroWidth true for U+200D ZERO WIDTH JOINER`` () =
    hasZeroWidth ("a" + string (char 0x200D) + "b") |> should equal true

[<Fact>]
let ``hasZeroWidth true for U+FEFF BYTE ORDER MARK`` () =
    hasZeroWidth ("a" + string (char 0xFEFF) + "b") |> should equal true

[<Fact>]
let ``hasZeroWidth true for U+2060 WORD JOINER`` () =
    hasZeroWidth ("a" + string (char 0x2060) + "b") |> should equal true

[<Fact>]
let ``hasZeroWidth true for U+00AD SOFT HYPHEN`` () =
    hasZeroWidth ("a" + string (char 0x00AD) + "b") |> should equal true

[<Fact>]
let ``hasZeroWidth false for plain ASCII`` () =
    hasZeroWidth "hello world 123" |> should equal false
