module Fugue.Tests.ReadToolTests

open System
open System.IO
open Xunit
open FsUnit.Xunit

let private tmpFile (lines: string list) =
    let p = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt")
    File.WriteAllLines(p, lines)
    p

[<Fact>]
let ``Read returns numbered lines, full file`` () =
    let path = tmpFile [ "alpha"; "beta"; "gamma" ]
    let cwd = Path.GetTempPath()
    let result = Fugue.Tools.ReadTool.read cwd path None None
    let expected = "     1\talpha\n     2\tbeta\n     3\tgamma"
    result |> should equal expected

[<Fact>]
let ``Read with offset and limit returns slice`` () =
    let path = tmpFile [ "a"; "b"; "c"; "d"; "e" ]
    let result = Fugue.Tools.ReadTool.read (Path.GetTempPath()) path (Some 1) (Some 2)
    result |> should equal "     2\tb\n     3\tc"

[<Fact>]
let ``Read of missing file throws`` () =
    let path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    (fun () -> Fugue.Tools.ReadTool.read (Path.GetTempPath()) path None None |> ignore)
    |> should throw typeof<FileNotFoundException>
