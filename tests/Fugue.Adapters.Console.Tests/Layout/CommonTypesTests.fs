module Fugue.Adapters.Console.Tests.Layout.CommonTypesTests

// see Helpers.fs FACT_DISCOVERY for why all tests are [<Property>]
open FsCheck.Xunit
open Fugue.Adapters.Console.Layout

// ============================================================================
// T008 — Exhaustive-match smoke tests for common Layout DUs.
//
// Purpose: ensure every case of each DU is reachable. A bare `_ ->` wildcard
// that masked a forgotten case would cause this test to pass trivially — the
// exhaustive multi-arm match forces every case to appear at least once in
// compiled code, so adding a new case without updating this test will produce
// a compiler warning (incomplete-match) that fails the build under
// TreatWarningsAsErrors=true.
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T008a — Orientation: all cases are reachable`` () =
    let allCases = [Vertical; Horizontal]
    allCases
    |> List.forall (fun o ->
        match o with
        | Vertical   -> true
        | Horizontal -> true)

[<Property(MaxTest = 1)>]
let ``T008b — CrossAxisAlignment: all cases are reachable`` () =
    let allCases = [Start; Center; End_; Stretch]
    allCases
    |> List.forall (fun a ->
        match a with
        | Start   -> true
        | Center  -> true
        | End_    -> true
        | Stretch -> true)

[<Property(MaxTest = 1)>]
let ``T008c — Overflow: all cases are reachable`` () =
    let allCases = [Clip; Strict; WrapNext]
    allCases
    |> List.forall (fun ov ->
        match ov with
        | Clip     -> true
        | Strict   -> true
        | WrapNext -> true)
