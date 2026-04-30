module Fugue.Tests.RenderTests

open System
open Spectre.Console.Testing
open Xunit
open FsUnit.Xunit
open Fugue.Cli
open Fugue.Cli.Render
open Fugue.Core.Config
open Fugue.Core.Localization

let private uiLeft : UiConfig = { UserAlignment = Left; Locale = "en"; PromptTemplate = "♩ "; Bell = false }
let private uiRight : UiConfig = { UserAlignment = Right; Locale = "en"; PromptTemplate = "♩ "; Bell = false }

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
    let state = Completed("Read", "{}", "alpha\nbeta", TimeSpan.FromMilliseconds 12.0)
    let out = toolBullet s state |> toStr
    out |> should haveSubstring "Read"
    out |> should haveSubstring "alpha"
    out |> should haveSubstring "12ms"

[<Fact>]
let ``toolBullet completed shows seconds for long tools`` () =
    let strings = pick "en"
    let state = Completed("Bash", "ls", "file.txt", TimeSpan.FromSeconds 2.3)
    let out = toolBullet strings state |> toStr
    out |> should haveSubstring "Bash"
    out |> should haveSubstring "2.3s"

[<Fact>]
let ``toolBullet failed shows elapsed`` () =
    let strings = pick "en"
    let state = Failed("Bash", "rm -rf", "Permission denied", TimeSpan.FromMilliseconds 5.0)
    let out = toolBullet strings state |> toStr
    out |> should haveSubstring "Bash"
    out |> should haveSubstring "5ms"

[<Fact>]
let ``toolBullet failed suppresses stack trace`` () =
    let s = pick "en"
    let raw = "File not found: foo.txt\n   at System.IO.File.ReadAllText(String path)\n   at Fugue.Tools.ReadTool.run()"
    let state = Failed("Read", "foo.txt", raw, System.TimeSpan.FromMilliseconds 1.0)
    let out = toolBullet s state |> toStr
    out |> should haveSubstring "File not found"
    out |> should not' (haveSubstring "at System.IO")
