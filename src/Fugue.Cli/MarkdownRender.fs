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

let mutable private emojiNormalise = false

let initEmoji (enabled: bool) = emojiNormalise <- enabled

let mutable private activeTheme = ""

let initTheme (theme: string) = activeTheme <- theme

let private escape (text: string) : string =
    Markup.Escape text

let rec private renderInline (sb: StringBuilder) (inl: Inline) : unit =
    match inl with
    | :? LiteralInline as lit ->
        let raw = lit.Content.ToString()
        let text = if emojiNormalise then Fugue.Core.EmojiMap.normalise raw else raw
        sb.Append(escape text) |> ignore
    | :? EmphasisInline as em ->
        let tag =
            match em.DelimiterCount with
            | 1 -> "italic"
            | 2 -> "bold"
            | _ -> "bold italic"
        sb.Append($"[{tag}]") |> ignore
        for child in (em :> seq<Inline>) do renderInline sb child
        sb.Append "[/]" |> ignore
    | :? CodeInline as ci ->
        let color = if activeTheme = "nocturne" then "#4a90d9" else "teal"
        sb.Append($"[{color}]{escape ci.Content}[/]") |> ignore
    | :? LinkInline as li ->
        let label =
            let inner = StringBuilder()
            for child in (li :> seq<Inline>) do renderInline inner child
            inner.ToString()
        let url = li.Url |> Option.ofObj |> Option.defaultValue ""
        sb.Append($"[link={escape url}]{label}[/]") |> ignore
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
            if activeTheme = "nocturne" then
                match h.Level with
                | 1 -> "bold underline #b8a060"
                | 2 -> "bold #b8a060"
                | _ -> "bold dim #b8a060"
            else
                match h.Level with
                | 1 -> "bold underline"
                | 2 -> "bold"
                | _ -> "bold dim"
        let body = inlineToMarkupOpt h.Inline
        Markup($"[{style}]{body}[/]") :> IRenderable
    | :? ParagraphBlock as p ->
        Markup(inlineToMarkupOpt p.Inline) :> IRenderable
    | :? FencedCodeBlock as f ->
        // Single Markup with \n separators avoids Spectre's per-row width-fill padding.
        // Markdig sometimes returns null Lines.Lines for empty blocks; guard via Count.
        let lang =
            f.Info |> Option.ofObj |> Option.defaultValue "" |> (fun s -> s.Trim().ToLowerInvariant())
        let lines =
            if f.Lines.Count = 0 || isNull (box f.Lines.Lines) then [||]
            else
                f.Lines.Lines
                |> Seq.truncate f.Lines.Count
                |> Seq.map (fun l -> l.ToString())
                |> Seq.toArray
        let lineStyle = if activeTheme = "nocturne" then "#5a7a99" else "dim"
        let body =
            lines
            |> Array.mapi (fun i ln ->
                let nr = (string (i + 1)).PadLeft(3)
                "[" + lineStyle + "]" + nr + "  " + escape ln + "[/]")
            |> String.concat "\n"
        let codeBlock = Padder(Markup(body)).PadLeft(2) :> IRenderable
        if lang <> "" then
            let badge = Markup($"[dim]╭─ {escape lang} ─[/]") :> IRenderable
            Rows([| badge; codeBlock |]) :> IRenderable
        else codeBlock
    | :? Markdig.Syntax.CodeBlock as c ->
        let txt = c.Lines.ToString()
        Padder(Markup($"[dim]{escape txt}[/]")).PadLeft(2) :> IRenderable
    | :? Markdig.Syntax.QuoteBlock as q ->
        let inner =
            q
            |> Seq.cast<Block>
            |> Seq.map renderBlock
            |> Seq.toArray
        Padder(Rows(inner)).PadLeft(2) :> IRenderable
    | :? Markdig.Syntax.ListBlock as lb ->
        // Render each list item as a single Markup line "prefix text".
        // Multi-paragraph items: stack the rest indented below.
        let items =
            lb
            |> Seq.cast<Block>
            |> Seq.mapi (fun i item ->
                let prefix = if lb.IsOrdered then string (i + 1) + ". " else "• "
                match item with
                | :? Markdig.Syntax.ListItemBlock as li ->
                    let blocks = li |> Seq.cast<Block> |> Seq.toArray
                    match blocks with
                    | [||] -> Markup(prefix) :> IRenderable
                    | _ ->
                        let firstLine =
                            match blocks.[0] with
                            | :? ParagraphBlock as p -> Markup(prefix + inlineToMarkupOpt p.Inline) :> IRenderable
                            | other ->
                                // Non-paragraph first block (nested list etc.): render with prefix on its own line.
                                Rows([| Markup(prefix) :> IRenderable; renderBlock other |]) :> IRenderable
                        if blocks.Length = 1 then firstLine
                        else
                            let rest =
                                blocks
                                |> Array.skip 1
                                |> Array.map renderBlock
                            let restPadded = Padder(Rows(rest)).PadLeft(2) :> IRenderable
                            Rows([| firstLine; restPadded |]) :> IRenderable
                | _ -> Markup("") :> IRenderable)
            |> Seq.toArray
        Rows(items) :> IRenderable
    | :? Markdig.Extensions.Tables.Table as tbl ->
        let table = new Spectre.Console.Table()
        let mutable headerSeen = false
        for row in tbl |> Seq.cast<Markdig.Extensions.Tables.TableRow> do
            let cellTexts =
                row
                |> Seq.cast<Markdig.Extensions.Tables.TableCell>
                |> Seq.map (fun cell ->
                    let buf = StringBuilder()
                    for blk in cell |> Seq.cast<Block> do
                        match blk with
                        | :? ParagraphBlock as p -> buf.Append(inlineToMarkupOpt p.Inline) |> ignore
                        | _ -> ()
                    buf.ToString())
                |> Seq.toArray
            if not headerSeen && row.IsHeader then
                for text in cellTexts do
                    table.AddColumn(text) |> ignore
                headerSeen <- true
            else
                if not headerSeen then
                    // No explicit header — synthesise empty columns from this row's width.
                    for _ in cellTexts do table.AddColumn("") |> ignore
                    headerSeen <- true
                table.AddRow(cellTexts) |> ignore
        table :> IRenderable
    | :? Markdig.Syntax.HtmlBlock as hb ->
        // Render HTML block as plain dim text — Spectre can't display arbitrary HTML.
        let txt =
            if hb.Lines.Count = 0 || isNull (box hb.Lines.Lines) then ""
            else
                hb.Lines.Lines
                |> Seq.truncate hb.Lines.Count
                |> Seq.map (fun l -> l.ToString())
                |> String.concat "\n"
        Markup("[dim]" + escape txt + "[/]") :> IRenderable
    | :? ThematicBreakBlock ->
        Rule() :> IRenderable
    | _ ->
        Markup(escape (block.ToString() |> Option.ofObj |> Option.defaultValue "")) :> IRenderable

/// Convert markdown source text to a single composite Spectre IRenderable.
/// Math pre-processing runs before Markdig so LaTeX notation renders as Unicode.
let toRenderable (markdown: string) : IRenderable =
    let doc = Markdown.Parse(MathRender.preprocess markdown, pipeline)
    let parts =
        doc
        |> Seq.cast<Block>
        |> Seq.map renderBlock
        |> Seq.toArray
    if parts.Length = 1 then parts.[0]
    else Rows(parts) :> IRenderable
