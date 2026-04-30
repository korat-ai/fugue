module Fugue.Tests.ModelScheduleTests

open System
open Xunit
open FsUnit.Xunit
open Fugue.Core.ModelSchedule

let private monday9am =
    DateTimeOffset(2024, 1, 15, 9, 0, 0, TimeSpan.Zero)   // 2024-01-15 is Monday

let private monday6pm =
    DateTimeOffset(2024, 1, 15, 18, 0, 0, TimeSpan.Zero)

let private saturday10am =
    DateTimeOffset(2024, 1, 20, 10, 0, 0, TimeSpan.Zero)   // 2024-01-20 is Saturday

let private mkWindow start end' days =
    { Start = TimeOnly.Parse start; End = TimeOnly.Parse end'; Days = days; TimeZone = "UTC" }

[<Fact>]
let ``resolveModel returns fallback when schedule is empty`` () =
    let cfg = { Schedule = []; Fallback = "model-a" }
    resolveModel cfg monday9am |> should equal "model-a"

[<Fact>]
let ``resolveModel picks matching window`` () =
    let cfg =
        { Fallback  = "slow-model"
          Schedule  =
            [ { Window = mkWindow "08:00" "18:00" (Set.ofList [DayOfWeek.Monday]); ModelId = "fast-model"; Priority = 10 } ] }
    resolveModel cfg monday9am |> should equal "fast-model"

[<Fact>]
let ``resolveModel falls back when outside window hours`` () =
    let cfg =
        { Fallback  = "slow-model"
          Schedule  =
            [ { Window = mkWindow "08:00" "18:00" (Set.ofList [DayOfWeek.Monday]); ModelId = "fast-model"; Priority = 10 } ] }
    resolveModel cfg monday6pm |> should equal "slow-model"

[<Fact>]
let ``resolveModel falls back on wrong day`` () =
    let cfg =
        { Fallback  = "slow-model"
          Schedule  =
            [ { Window = mkWindow "08:00" "18:00" (Set.ofList [DayOfWeek.Monday; DayOfWeek.Friday]); ModelId = "fast-model"; Priority = 10 } ] }
    resolveModel cfg saturday10am |> should equal "slow-model"

[<Fact>]
let ``empty Days set matches every day`` () =
    let cfg =
        { Fallback  = "slow-model"
          Schedule  =
            [ { Window = mkWindow "08:00" "18:00" Set.empty; ModelId = "fast-model"; Priority = 10 } ] }
    resolveModel cfg saturday10am |> should equal "fast-model"

[<Fact>]
let ``higher priority wins on overlap`` () =
    let cfg =
        { Fallback  = "slow-model"
          Schedule  =
            [ { Window = mkWindow "08:00" "18:00" Set.empty; ModelId = "mid-model"; Priority = 5  }
              { Window = mkWindow "09:00" "12:00" Set.empty; ModelId = "fast-model"; Priority = 10 } ] }
    resolveModel cfg monday9am |> should equal "fast-model"
