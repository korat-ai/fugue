module Fugue.Core.ModelSchedule

open System

type TimeWindow = {
    Start    : TimeOnly
    End      : TimeOnly
    Days     : DayOfWeek Set   // empty = every day
    TimeZone : string          // IANA tz ID, e.g. "Europe/London"; "" = local
}

type ScheduledModel = {
    Window   : TimeWindow
    ModelId  : string
    Priority : int
}

type ModelScheduleConfig = {
    Schedule : ScheduledModel list
    Fallback : string
}

let resolveModel (cfg: ModelScheduleConfig) (now: DateTimeOffset) : string =
    cfg.Schedule
    |> List.filter (fun s ->
        let tz =
            if String.IsNullOrEmpty s.Window.TimeZone then TimeZoneInfo.Local
            else try TimeZoneInfo.FindSystemTimeZoneById s.Window.TimeZone with _ -> TimeZoneInfo.Local
        let local = TimeZoneInfo.ConvertTime(now, tz)
        let tod = TimeOnly.FromDateTime(local.DateTime)
        let dow = local.DayOfWeek
        (s.Window.Days.IsEmpty || Set.contains dow s.Window.Days)
        && tod >= s.Window.Start
        && tod < s.Window.End)
    |> List.sortByDescending (fun s -> s.Priority)
    |> List.tryHead
    |> Option.map (fun s -> s.ModelId)
    |> Option.defaultValue cfg.Fallback
