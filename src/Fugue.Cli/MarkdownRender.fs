module Fugue.Cli.MarkdownRender

open System.Text
open Markdig
open Markdig.Syntax
open Markdig.Syntax.Inlines
open Spectre.Console
open Spectre.Console.Rendering

let pipeline : MarkdownPipeline =
    MarkdownPipelineBuilder()
        .UseGridTables()
        .UsePipeTables()
        .UseEmphasisExtras()
        .Build()

let private escape (text: string) : string =
    Markup.Escape text

let rec private renderInline (sb: StringBuilder) (inl: Inline) : unit =
    match inl with
    | :? LiteralInline as lit ->
        sb.Append(escape (lit.Content.ToString())) |> ignore
    | :? EmphasisInline as em ->
        let tag =
            match em.DelimiterCount with
            | 1 -> "italic"
            | 2 -> "bold"
            | _ -> "bold italic"
        sb.Append(sprintf "[%s]" tag) |> ignore
        for child in (em :> seq<Inline>) do renderInline sb child
        sb.Append "[/]" |> ignore
    | :? CodeInline as ci ->
        sb.Append(sprintf "[teal]%s[/]" (escape ci.Content)) |> ignore
    | :? LinkInline as li ->
        let label =
            let inner = StringBuilder()
            for child in (li :> seq<Inline>) do renderInline inner child
            inner.ToString()
        sb.Append(sprintf "[link=%s]%s[/]" (escape (li.Url |> Option.ofObj |> Option.defaultValue "")) label) |> ignore
    | :? LineBreakInline ->
        sb.Append '\n' |> ignore
    | :? ContainerInline as ci ->
        for child in (ci :> seq<Inline>) do renderInline sb child
    | _ ->
        sb.Append(escape (inl.ToString() |> Option.ofObj |> Option.defaultValue "")) |> ignore

let private inlineToMarkup (block: ContainerInline) : string =
    let sb = StringBuilder()
    for child in (block :> seq<Inline>) do renderInline sb child
    sb.ToString()

let private inlineToMarkupOpt (block: ContainerInline | null) : string =
    match block with
    | null -> ""
    | inl -> inlineToMarkup inl

let rec private renderBlock (block: Block) : IRenderable =
    match block with
    | :? HeadingBlock as h ->
        let style =
            match h.Level with
            | 1 -> "bold underline"
            | 2 -> "bold"
            | _ -> "bold dim"
        let body = inlineToMarkupOpt h.Inline
        Markup(sprintf "[%s]%s[/]" style body) :> IRenderable
    | :? ParagraphBlock as p ->
        Markup(inlineToMarkupOpt p.Inline) :> IRenderable
    | :? FencedCodeBlock as f ->
        let lines =
            f.Lines.Lines
            |> Seq.take f.Lines.Count
            |> Seq.map (fun l -> l.ToString())
            |> Seq.toArray
        let rows =
            lines
            |> Array.mapi (fun i ln ->
                let nr = "[dim]" + (string (i + 1)).PadLeft(3) + "[/]"
                let body = sprintf "[dim]%s[/]" (escape ln)
                Columns([ Markup(nr) :> IRenderable; Markup(body) :> IRenderable ]) :> IRenderable)
        Padder(Rows(rows)).PadLeft(2) :> IRenderable
    | :? Markdig.Syntax.CodeBlock as c ->
        let txt = c.Lines.ToString()
        Padder(Markup(sprintf "[dim]%s[/]" (escape txt))).PadLeft(2) :> IRenderable
    | :? Markdig.Syntax.QuoteBlock as q ->
        let inner =
            q
            |> Seq.cast<Block>
            |> Seq.map renderBlock
            |> Seq.toArray
        Padder(Rows(inner)).PadLeft(2) :> IRenderable
    | :? Markdig.Syntax.ListBlock as lb ->
        let items =
            lb
            |> Seq.cast<Block>
            |> Seq.mapi (fun i item ->
                let prefix = if lb.IsOrdered then string (i + 1) + ". " else "• "
                let inner : IRenderable =
                    match item with
                    | :? Markdig.Syntax.ListItemBlock as li ->
                        li
                        |> Seq.cast<Block>
                        |> Seq.map renderBlock
                        |> Seq.toArray
                        |> Rows
                        :> IRenderable
                    | _ -> Markup("") :> IRenderable
                Columns([ Markup(prefix) :> IRenderable; inner ]) :> IRenderable)
            |> Seq.toArray
        Rows(items) :> IRenderable
    | :? ThematicBreakBlock ->
        Rule() :> IRenderable
    | _ ->
        Markup(escape (block.ToString() |> Option.ofObj |> Option.defaultValue "")) :> IRenderable

/// Convert markdown source text to a single composite Spectre IRenderable.
let toRenderable (markdown: string) : IRenderable =
    let doc = Markdown.Parse(markdown, pipeline)
    let parts =
        doc
        |> Seq.cast<Block>
        |> Seq.map renderBlock
        |> Seq.toArray
    if parts.Length = 1 then parts.[0]
    else Rows(parts) :> IRenderable
