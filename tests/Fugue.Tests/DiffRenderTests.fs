module Fugue.Tests.DiffRenderTests

open Spectre.Console.Testing
open Xunit
open FsUnit.Xunit
open Fugue.Cli

[<Fact>]
let ``looksLikeDiff true on canonical hunk`` () =
    let txt =
        "@@ -1,3 +1,3 @@\n" +
        "-old\n" +
        "+new\n" +
        " context\n"
    DiffRender.looksLikeDiff txt |> should equal true

[<Fact>]
let ``looksLikeDiff false on plain text`` () =
    DiffRender.looksLikeDiff "just a sentence" |> should equal false

[<Fact>]
let ``looksLikeDiff true on multiple plus minus lines without hunk header`` () =
    let txt = "-line1\n+line1-mod\n-line2\n+line2-mod\n"
    DiffRender.looksLikeDiff txt |> should equal true

[<Fact>]
let ``toRenderable renders without throwing on empty input`` () =
    let console = new TestConsole()
    console.Profile.Width <- 80
    console.Write(DiffRender.toRenderable "")
    console.Output |> should not' (be NullOrEmptyString)

[<Fact>]
let ``toRenderable contains - and + content`` () =
    let txt = "@@ -1 +1 @@\n-a\n+b\n"
    let console = new TestConsole()
    console.Profile.Width <- 80
    console.Write(DiffRender.toRenderable txt)
    console.Output |> should haveSubstring "a"
    console.Output |> should haveSubstring "b"
