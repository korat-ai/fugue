[<Xunit.Collection("Sequential")>]
module Fugue.Tests.CheckpointTests

open System.IO
open Xunit
open FsUnit.Xunit
open Fugue.Core.Checkpoint

[<Fact>]
let ``undo returns NothingToUndo when stack is empty`` () =
    clear ()
    undo () |> should equal NothingToUndo

[<Fact>]
let ``undo restores previous content after snapshot`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    try
        File.WriteAllText(tmp, "original")
        clear ()
        snapshot tmp
        File.WriteAllText(tmp, "modified")
        let result = undo ()
        result |> should equal (Restored tmp)
        File.ReadAllText tmp |> should equal "original"
    finally
        try File.Delete tmp with _ -> ()

[<Fact>]
let ``undo deletes file that did not exist before snapshot`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    try
        clear ()
        snapshot tmp                          // file absent → None
        File.WriteAllText(tmp, "new content") // write creates it
        let result = undo ()
        result |> should equal (Deleted tmp)
        File.Exists tmp |> should be False
    finally
        try File.Delete tmp with _ -> ()

[<Fact>]
let ``depth reflects number of snapshots`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    try
        File.WriteAllText(tmp, "a")
        clear ()
        snapshot tmp
        snapshot tmp
        depth () |> should equal 2
        undo () |> ignore
        depth () |> should equal 1
    finally
        try File.Delete tmp with _ -> ()

[<Fact>]
let ``clear empties the stack`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    try
        File.WriteAllText(tmp, "x")
        snapshot tmp
        clear ()
        depth () |> should equal 0
        undo () |> should equal NothingToUndo
    finally
        try File.Delete tmp with _ -> ()
