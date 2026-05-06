module Fugue.Tests.DataPreviewTests

open Xunit
open FsUnit.Xunit
open Fugue.Tools.DataPreview

[<Fact>]
let ``tryPreviewCsv renders markdown table`` () =
    let csv = "name,age,city\nAlice,30,London\nBob,25,Paris"
    let result = tryPreviewCsv csv
    result |> should not' (equal None)
    let t = result.Value
    t |> should haveSubstring "name"
    t |> should haveSubstring "Alice"
    t |> should haveSubstring "London"

[<Fact>]
let ``tryPreviewCsv handles quoted fields`` () =
    let csv = "name,bio\nAlice,\"loves F#, hates XML\"\nBob,\"plain\""
    let result = tryPreviewCsv csv
    result |> should not' (equal None)
    result.Value |> should haveSubstring "loves F#, hates XML"

[<Fact>]
let ``tryPreviewCsv returns None for single-column data`` () =
    let csv = "name\nAlice\nBob"
    tryPreviewCsv csv |> should equal None

[<Fact>]
let ``tryPreviewJsonl renders table from object records`` () =
    let jsonl = "{\"id\":1,\"name\":\"Alice\"}\n{\"id\":2,\"name\":\"Bob\"}"
    let result = tryPreviewJsonl jsonl
    result |> should not' (equal None)
    result.Value |> should haveSubstring "id"
    result.Value |> should haveSubstring "Alice"

[<Fact>]
let ``tryPreviewJsonl returns None for non-object records`` () =
    let jsonl = "\"just a string\"\n\"another\""
    tryPreviewJsonl jsonl |> should equal None

[<Fact>]
let ``tryPreviewJson renders array of objects`` () =
    let json = "[{\"x\":1,\"y\":2},{\"x\":3,\"y\":4}]"
    let result = tryPreviewJson json
    result |> should not' (equal None)
    result.Value |> should haveSubstring "x"
    result.Value |> should haveSubstring "1"

[<Fact>]
let ``tryPreviewJson returns None for non-array JSON`` () =
    let json = "{\"key\":\"value\"}"
    tryPreviewJson json |> should equal None
