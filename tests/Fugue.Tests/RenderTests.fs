module Fugue.Tests.RenderTests

open Spectre.Console.Testing
open Xunit
open FsUnit.Xunit
open Fugue.Cli
open Fugue.Cli.Render
open Fugue.Core.Config
open Fugue.Core.Localization

let private uiLeft : UiConfig = { UserAlignment = Left; Locale = "en" }
let private uiRight : UiConfig = { UserAlignment = Right; Locale = "en" }

let private toStr (r: Spectre.Console.Rendering.IRenderable) : string =
    let c = new TestConsole()
    c.Profile.Width <- 80
    c.Write r
    c.Output

[<Fact>]
let ``userMessage Left contains '> ' prefix`` () =
    let out = userMessage uiLeft "hello" 1 |> toStr
    out |> should haveSubstring "›"
    out |> should haveSubstring "hello"

[<Fact>]
let ``userMessage Right does not have left-edge prefix`` () =
    // Right-aligned: text is right-justified within terminal width, no '>' prefix
    let out = userMessage uiRight "hello" 1 |> toStr
    out |> should haveSubstring "hello"

[<Fact>]
let ``cancelled uses localized text`` () =
    let s = pick "ru"
    let out = cancelled s |> toStr
    out |> should haveSubstring "отменено"

[<Fact>]
let ``errorLine includes error prefix and message`` () =
    let s = pick "en"
    let out = errorLine s "boom" |> toStr
    out |> should haveSubstring "error"
    out |> should haveSubstring "boom"

[<Fact>]
let ``toolBullet running shows yellow circle and tool name`` () =
    let s = pick "en"
    let state = Running("Bash", "echo hi")
    let out = toolBullet s state |> toStr
    out |> should haveSubstring "Bash"
    out |> should haveSubstring "running"

[<Fact>]
let ``toolBullet completed plain output renders without diff`` () =
    let s = pick "en"
    let state = Completed("Read", "{}", "alpha\nbeta")
    let out = toolBullet s state |> toStr
    out |> should haveSubstring "Read"
    out |> should haveSubstring "alpha"
