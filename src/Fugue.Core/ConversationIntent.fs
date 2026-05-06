module Fugue.Core.ConversationIntent

open System.Collections.Generic

let private affirmations =
    HashSet<string>(
        [| "yes"; "y"; "ok"; "okay"; "go"; "go ahead"; "do it"
           "proceed"; "sure"; "yep"; "yeah"; "confirmed"; "lgtm"
           "sounds good"; "looks good"; "do"; "doit"; "ship it"
           "да"; "давай"; "хорошо"; "ок"; "окей" |],
        System.StringComparer.OrdinalIgnoreCase)

let private negations =
    HashSet<string>(
        [| "no"; "n"; "nope"; "stop"; "wait"; "cancel"; "abort"
           "нет"; "стоп" |],
        System.StringComparer.OrdinalIgnoreCase)

type Intent =
    | Affirmation
    | Negation
    | NewPrompt

/// True if the last AI response looks like it presented a plan to execute.
let looksLikePlan (responseText: string) : bool =
    let t = responseText
    // Numbered-list plan: "1. " or "1) " within 10 lines from start
    let lines = t.Split('\n')
    let firstTen = lines |> Array.truncate 10
    let hasNumberedList = firstTen |> Array.exists (fun l ->
        let tr = l.TrimStart()
        (tr.StartsWith "1." || tr.StartsWith "1)") && tr.Length > 4)
    let hasPlanWord =
        t.Contains "Here is the plan" || t.Contains "here's the plan" ||
        t.Contains "Here's what I'll do" || t.Contains "I'll:" ||
        t.Contains "Steps:" || t.Contains "Plan:" ||
        t.Contains "Вот план" || t.Contains "Шаги:"
    hasNumberedList || hasPlanWord

let classify (hasPlanContext: bool) (input: string) : Intent =
    let trimmed = input.Trim()
    if trimmed.Length = 0 then NewPrompt
    elif hasPlanContext && affirmations.Contains(trimmed) then Affirmation
    elif negations.Contains(trimmed) then Negation
    else NewPrompt
