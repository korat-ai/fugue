module Fugue.Tests.BashToolTests

open System.IO
open Xunit
open FsUnit.Xunit

[<Fact>]
let ``Bash echoes stdout`` () =
    let result = Fugue.Tools.BashTool.bash (Path.GetTempPath()) "echo hello" None
    result |> should haveSubstring "hello"
    result |> should haveSubstring "exit_code=0"

[<Fact>]
let ``Bash captures stderr`` () =
    let result = Fugue.Tools.BashTool.bash (Path.GetTempPath()) "echo oops 1>&2" None
    result |> should haveSubstring "oops"

[<Fact>]
let ``Bash reports non-zero exit code`` () =
    let result = Fugue.Tools.BashTool.bash (Path.GetTempPath()) "false" None
    result |> should haveSubstring "exit_code=1"

[<Fact>]
let ``Bash respects timeout`` () =
    let result = Fugue.Tools.BashTool.bash (Path.GetTempPath()) "sleep 5" (Some 200)
    result |> should haveSubstring "<timeout>"

[<Fact>]
let ``Bash timeout message includes elapsed ms`` () =
    let result = Fugue.Tools.BashTool.bash (Path.GetTempPath()) "sleep 5" (Some 150)
    result |> should haveSubstring "150 ms"

[<Fact>]
let ``Bash completes normally within explicit timeout`` () =
    let result = Fugue.Tools.BashTool.bash (Path.GetTempPath()) "echo done" (Some 5000)
    result |> should haveSubstring "exit_code=0"
    result |> should haveSubstring "done"
