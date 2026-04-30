module Fugue.Tests.BashToolTests

open System.IO
open System.Threading
open Xunit
open FsUnit.Xunit

[<Fact>]
let ``Bash echoes stdout`` () =
    let result = Fugue.Tools.BashTool.bash (Path.GetTempPath()) "echo hello" None CancellationToken.None
    result |> should haveSubstring "hello"
    result |> should haveSubstring "exit_code=0"

[<Fact>]
let ``Bash captures stderr`` () =
    let result = Fugue.Tools.BashTool.bash (Path.GetTempPath()) "echo oops 1>&2" None CancellationToken.None
    result |> should haveSubstring "oops"

[<Fact>]
let ``Bash reports non-zero exit code`` () =
    let result = Fugue.Tools.BashTool.bash (Path.GetTempPath()) "false" None CancellationToken.None
    result |> should haveSubstring "exit_code=1"

[<Fact>]
let ``Bash respects timeout`` () =
    let result = Fugue.Tools.BashTool.bash (Path.GetTempPath()) "sleep 5" (Some 200) CancellationToken.None
    result |> should haveSubstring "<timeout>"

[<Fact>]
let ``Bash timeout message includes elapsed ms`` () =
    let result = Fugue.Tools.BashTool.bash (Path.GetTempPath()) "sleep 5" (Some 150) CancellationToken.None
    result |> should haveSubstring "150 ms"

[<Fact>]
let ``Bash completes normally within explicit timeout`` () =
    let result = Fugue.Tools.BashTool.bash (Path.GetTempPath()) "echo done" (Some 5000) CancellationToken.None
    result |> should haveSubstring "exit_code=0"
    result |> should haveSubstring "done"

[<Fact>]
let ``Bash returns cancelled marker when token is cancelled`` () =
    use cts = new CancellationTokenSource()
    cts.CancelAfter(200)
    let result = Fugue.Tools.BashTool.bash (Path.GetTempPath()) "sleep 10" (Some 30000) cts.Token
    result |> should haveSubstring "<cancelled>"
