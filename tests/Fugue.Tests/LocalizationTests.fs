module Fugue.Tests.LocalizationTests

open Xunit
open FsUnit.Xunit
open Fugue.Core.Localization

[<Fact>]
let ``pick "en" returns english strings`` () =
    let s = pick "en"
    s.Cancelled |> should equal "cancelled"

[<Fact>]
let ``pick "ru" returns russian strings`` () =
    let s = pick "ru"
    s.Cancelled |> should equal "отменено"

[<Fact>]
let ``pick unknown locale falls back to english`` () =
    let s = pick "fr"
    s.Cancelled |> should equal "cancelled"

[<Fact>]
let ``en strings are all non-empty`` () =
    let s = en
    s.Cancelled       |> should not' (be EmptyString)
    s.ToolRunning     |> should not' (be EmptyString)
    s.ErrorPrefix     |> should not' (be EmptyString)
    s.StatusBarApp    |> should not' (be EmptyString)
    s.StatusBarOn     |> should not' (be EmptyString)

[<Fact>]
let ``ru strings are all non-empty`` () =
    let s = ru
    s.Cancelled       |> should not' (be EmptyString)
    s.ToolRunning     |> should not' (be EmptyString)
    s.ErrorPrefix     |> should not' (be EmptyString)
    s.StatusBarApp    |> should not' (be EmptyString)
    s.StatusBarOn     |> should not' (be EmptyString)
