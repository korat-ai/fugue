module Fugue.Cli.DiffRender

open Spectre.Console
open Spectre.Console.Rendering

let private hasHunkHeader (lines: string array) : bool =
    lines |> Array.exists (fun l -> l.StartsWith "@@ " && l.Contains "@@")

let private plusMinusLines (lines: string array) : int =
    lines
    |> Array.sumBy (fun l ->
        if l.StartsWith "+" || l.StartsWith "-" then 1 else 0)

let looksLikeDiff (text: string | null) : bool =
    match text with
    | null -> false
    | text when text = "" -> false
    | text ->
        let lines =
            text.Split('\n')
            |> Array.truncate 50
        hasHunkHeader lines || plusMinusLines lines >= 2

let private styleLine (l: string) : IRenderable =
    if l.StartsWith "+" then
        Markup(sprintf "[green]%s[/]" (Markup.Escape l)) :> _
    elif l.StartsWith "-" then
        Markup(sprintf "[red]%s[/]" (Markup.Escape l)) :> _
    elif l.StartsWith "@@" then
        Markup(sprintf "[magenta dim]%s[/]" (Markup.Escape l)) :> _
    else
        Markup(sprintf "[dim]%s[/]" (Markup.Escape l)) :> _

let toRenderable (text: string | null) : IRenderable =
    let lines =
        match text with
        | null -> [||]
        | text -> text.Split('\n')
    let renderables = lines |> Array.map styleLine
    if renderables.Length = 0 then Markup("") :> _
    else Rows(renderables) :> _
