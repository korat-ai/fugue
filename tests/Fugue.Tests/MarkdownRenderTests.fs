module Fugue.Tests.MarkdownRenderTests

open Spectre.Console
open Spectre.Console.Testing
open Xunit
open FsUnit.Xunit
open Fugue.Cli

let private renderToString (renderable: Spectre.Console.Rendering.IRenderable) : string =
    let console = new TestConsole()
    console.Profile.Width <- 80
    console.Write renderable
    console.Output

[<Fact>]
let ``paragraph renders as plain text`` () =
    let out = MarkdownRender.toRenderable "hello world" |> renderToString
    out |> should haveSubstring "hello world"

[<Fact>]
let ``bold renders with emphasis`` () =
    let out = MarkdownRender.toRenderable "make it **strong** now" |> renderToString
    out |> should haveSubstring "strong"

[<Fact>]
let ``inline code renders text`` () =
    let out = MarkdownRender.toRenderable "use `Console.WriteLine` here" |> renderToString
    out |> should haveSubstring "Console.WriteLine"

[<Fact>]
let ``heading renders text`` () =
    let out = MarkdownRender.toRenderable "# Heading One\nbody" |> renderToString
    out |> should haveSubstring "Heading One"
    out |> should haveSubstring "body"
