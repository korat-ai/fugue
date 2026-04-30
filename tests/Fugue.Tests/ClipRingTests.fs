module Fugue.Tests.ClipRingTests

open Xunit
open FsUnit.Xunit
open Fugue.Cli.ClipRing

[<Fact>]
let ``get returns None on empty ring`` () =
    clear ()
    get 1 |> should equal None

[<Fact>]
let ``push then get 1 returns most recent`` () =
    clear ()
    push "first"
    get 1 |> should equal (Some "first")

[<Fact>]
let ``get 2 returns second most recent`` () =
    clear ()
    push "older"
    push "newer"
    get 1 |> should equal (Some "newer")
    get 2 |> should equal (Some "older")

[<Fact>]
let ``ring wraps at capacity`` () =
    clear ()
    for i in 1..Capacity + 1 do
        push (sprintf "item%d" i)
    // Most recent is item(Capacity+1)
    get 1 |> should equal (Some (sprintf "item%d" (Capacity + 1)))
    // item1 was overwritten
    get Capacity |> should equal (Some (sprintf "item%d" 2))

[<Fact>]
let ``get beyond filled count returns None`` () =
    clear ()
    push "only"
    get 2 |> should equal None

[<Fact>]
let ``clear resets ring`` () =
    clear ()
    push "something"
    clear ()
    get 1 |> should equal None
