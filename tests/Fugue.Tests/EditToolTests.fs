module Fugue.Tests.EditToolTests

open System
open System.IO
open Xunit
open FsUnit.Xunit

let private write (content: string) =
    let p = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt")
    File.WriteAllText(p, content)
    p

[<Fact>]
let ``Edit replaces unique occurrence`` () =
    let path = write "alpha BETA gamma"
    Fugue.Tools.EditTool.edit (Path.GetTempPath()) path "BETA" "beta" None |> ignore
    File.ReadAllText path |> should equal "alpha beta gamma"

[<Fact>]
let ``Edit fails when old_string is non-unique and replace_all not set`` () =
    let path = write "x x x"
    (fun () -> Fugue.Tools.EditTool.edit (Path.GetTempPath()) path "x" "y" None |> ignore)
    |> should throw typeof<InvalidOperationException>

[<Fact>]
let ``Edit replace_all replaces every occurrence`` () =
    let path = write "x y x y x"
    Fugue.Tools.EditTool.edit (Path.GetTempPath()) path "x" "Z" (Some true) |> ignore
    File.ReadAllText path |> should equal "Z y Z y Z"

[<Fact>]
let ``Edit fails when old_string not found`` () =
    let path = write "alpha"
    (fun () -> Fugue.Tools.EditTool.edit (Path.GetTempPath()) path "BETA" "x" None |> ignore)
    |> should throw typeof<InvalidOperationException>
