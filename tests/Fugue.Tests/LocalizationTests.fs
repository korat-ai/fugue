module Fugue.Tests.LocalizationTests

open Xunit
open FsUnit.Xunit
open Fugue.Core.Localization

[<Fact>]
let ``pick "ru" returns russian strings`` () =
    let s = pick "ru"
    s.Cancelled |> should equal "отменено"

[<Fact>]
let ``pick normalizes locale to lowercase`` () =
    let s = pick "RU"
    s.Cancelled |> should equal "отменено"

[<Fact>]
let ``pick falls back to english for unknown locale`` () =
    let s = pick "fr"
    s.Cancelled |> should equal "cancelled"

[<Fact>]
let ``en strings are all non-empty`` () =
    let s = en
    [ s.Cancelled; s.ToolRunning; s.ErrorPrefix
      s.StatusBarApp; s.StatusBarOn
      s.PromptHelpEnter; s.PromptHelpShiftEnter; s.PromptHelpCtrlD
      s.ConfigHelpHeader; s.ConfigHelpEnvHint; s.ConfigHelpFileHint
      s.DiscoveryNoCandidates; s.DiscoveryPickProvider ]
    |> List.iter (fun v -> v |> should not' (equal ""))

[<Fact>]
let ``ru strings are all non-empty`` () =
    let s = ru
    [ s.Cancelled; s.ToolRunning; s.ErrorPrefix
      s.StatusBarApp; s.StatusBarOn
      s.PromptHelpEnter; s.PromptHelpShiftEnter; s.PromptHelpCtrlD
      s.ConfigHelpHeader; s.ConfigHelpEnvHint; s.ConfigHelpFileHint
      s.DiscoveryNoCandidates; s.DiscoveryPickProvider ]
    |> List.iter (fun v -> v |> should not' (equal ""))
