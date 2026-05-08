module Fugue.Tests.ModelSchedulePropertyTests

/// Property-style tests for ModelSchedule.resolveModel.
///
/// Verifies the four key invariants:
///   1. Determinism — same config + time → same model
///   2. Empty schedule → fallback
///   3. No matching window → fallback
///   4. Window containment invariant holds

open System
open Xunit
open FsUnit.Xunit
open Fugue.Core.ModelSchedule

let private mkWindow startH startM endH endM days tz =
    { Start    = TimeOnly(startH, startM)
      End      = TimeOnly(endH, endM)
      Days     = days
      TimeZone = tz }

let private utcAt year month day hour minute =
    DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero)

[<Fact>]
let ``resolveModel is deterministic — calling twice with same inputs returns same result`` () =
    let cfg =
        { Fallback = "fallback"
          Schedule =
            [ { Window   = mkWindow 9 0 17 0 Set.empty "UTC"
                ModelId  = "work-model"
                Priority = 10 } ] }
    let times =
        [ utcAt 2024 1 15 9 30     // Monday 09:30 UTC — inside window
          utcAt 2024 1 15 17 30    // Monday 17:30 UTC — outside window
          utcAt 2024 1 20 12 0     // Saturday noon UTC
          utcAt 2026 6 15 0 0 ]    // midnight
    for t in times do
        resolveModel cfg t |> should equal (resolveModel cfg t)

[<Fact>]
let ``resolveModel returns fallback when schedule is empty`` () =
    let cfg = { Schedule = []; Fallback = "default-model" }
    let times =
        [ utcAt 2024 1 15 9 0
          utcAt 2024 6 21 13 0
          utcAt 2025 12 31 23 59 ]
    for t in times do
        resolveModel cfg t |> should equal "default-model"

[<Fact>]
let ``resolveModel returns fallback when no windows match any of the tested times`` () =
    // A window from 12:00 to 12:00 is zero-length — no time ever satisfies start <= t < end
    let impossibleWindow = mkWindow 12 0 12 0 Set.empty "UTC"
    let cfg =
        { Fallback  = "fallback"
          Schedule  = [ { Window = impossibleWindow; ModelId = "never"; Priority = 1 } ] }
    let times =
        [ utcAt 2024 1 15 11 59
          utcAt 2024 1 15 12 0
          utcAt 2024 1 15 12 1
          utcAt 2024 1 15 0 0 ]
    for t in times do
        resolveModel cfg t |> should equal "fallback"

[<Fact>]
let ``window containment invariant — a time strictly inside start..end satisfies the predicate`` () =
    let window = mkWindow 8 0 18 0 Set.empty "UTC"
    let noon = TimeOnly(12, 0)
    (noon >= window.Start && noon < window.End) |> should equal true

[<Fact>]
let ``window containment invariant — a time before start does not satisfy the predicate`` () =
    let window = mkWindow 9 0 17 0 Set.empty "UTC"
    let early = TimeOnly(8, 59)
    (early >= window.Start && early < window.End) |> should equal false

[<Fact>]
let ``window containment invariant — a time equal to end does not satisfy the predicate`` () =
    let window = mkWindow 9 0 17 0 Set.empty "UTC"
    let atEnd = TimeOnly(17, 0)
    (atEnd >= window.Start && atEnd < window.End) |> should equal false

[<Fact>]
let ``resolveModel picks the highest-priority matching window`` () =
    // Both windows cover 00:00–23:59 UTC any day.
    let allDay = mkWindow 0 0 23 59 Set.empty "UTC"
    let cfg =
        { Fallback = "fallback"
          Schedule =
            [ { Window = allDay; ModelId = "low-priority";  Priority = 1  }
              { Window = allDay; ModelId = "high-priority"; Priority = 99 } ] }
    // Noon on any day is inside [00:00, 23:59).
    let noon = utcAt 2024 6 15 12 0
    resolveModel cfg noon |> should equal "high-priority"

[<Fact>]
let ``resolveModel result is always one of the declared model ids or the fallback`` () =
    let allDay = mkWindow 0 0 23 59 Set.empty "UTC"
    let cfg =
        { Fallback = "fallback"
          Schedule =
            [ { Window = allDay; ModelId = "model-a"; Priority = 5  }
              { Window = allDay; ModelId = "model-b"; Priority = 10 } ] }
    let validResults = Set.ofList [ "model-a"; "model-b"; "fallback" ]
    let times =
        [ utcAt 2024 1 1 0 0
          utcAt 2024 6 15 12 0
          utcAt 2025 12 31 23 58 ]
    for t in times do
        let result = resolveModel cfg t
        Set.contains result validResults |> should equal true

[<Fact>]
let ``resolveModel respects day-of-week filter — Monday window does not match Saturday`` () =
    let mondayWindow =
        { Start    = TimeOnly(9, 0)
          End      = TimeOnly(17, 0)
          Days     = Set.ofList [ DayOfWeek.Monday ]
          TimeZone = "UTC" }
    let cfg =
        { Fallback  = "fallback"
          Schedule  = [ { Window = mondayWindow; ModelId = "work-model"; Priority = 10 } ] }
    // 2024-01-20 is Saturday.
    let saturday = utcAt 2024 1 20 12 0
    resolveModel cfg saturday |> should equal "fallback"
