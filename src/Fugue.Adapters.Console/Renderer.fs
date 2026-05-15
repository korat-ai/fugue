namespace Fugue.Adapters.Console

open System.IO

/// Renderer evaluates Composition values to byte/escape streams.
/// Pure (modulo Spectre's internal allocations); never touches
/// `Console.Out` directly. Callers route the output to the Fugue.Surface
/// actor themselves (via `DrawOp.RawAnsi`). The integration seam
/// described in research.md §R4.
module Renderer =

    // Local aliases to avoid namespace pollution when iterating over
    // our F# DUs that share names with Spectre's enums (Decoration, Style).
    type private SpectreColour     = Spectre.Console.Color
    type private SpectreStyle      = Spectre.Console.Style
    type private SpectreDecoration = Spectre.Console.Decoration
    type private SpectreText       = Spectre.Console.Text
    type private SpectreRule       = Spectre.Console.Rule
    type private SpectreSettings   = Spectre.Console.AnsiConsoleSettings
    type private SpectreOutput     = Spectre.Console.AnsiConsoleOutput
    type private SpectreSupport    = Spectre.Console.ColorSystemSupport
    type private IAC               = Spectre.Console.IAnsiConsole

    // ------------------------------------------------------------------
    // Canonical theme slot table — single source of truth lives in
    // data-model.md §2 "Canonical theme slots (v1)". When changing the
    // slot list, update BOTH files. 7 slots × 2 themes = 14 entries.
    // ------------------------------------------------------------------
    let private themeTable : Map<string * string, SpectreColour> =
        let hex (h: string) =
            // Spectre 0.49 has no Color.FromHex — parse manually.
            // Expected format: "#rrggbb".
            let b s = System.Convert.ToByte (s, 16)
            SpectreColour (b (h.Substring (1, 2)), b (h.Substring (3, 2)), b (h.Substring (5, 2)))
        Map.ofList [
            ("default", "accent"),       hex "#00afff"
            ("default", "warning"),      hex "#d7af00"
            ("default", "error"),        hex "#d70000"
            ("default", "dim-accent"),   hex "#808080"
            ("default", "diff-add"),     hex "#00af00"
            ("default", "diff-remove"),  hex "#d70000"
            ("default", "diff-hunk"),    hex "#d7af00"
            ("nocturne", "accent"),      hex "#1e3a5a"
            ("nocturne", "warning"),     hex "#b58900"
            ("nocturne", "error"),       hex "#cb4b16"
            ("nocturne", "dim-accent"),  hex "#586e75"
            ("nocturne", "diff-add"),    hex "#859900"
            ("nocturne", "diff-remove"), hex "#cb4b16"
            ("nocturne", "diff-hunk"),   hex "#b58900"
        ]

    /// Resolve our `Colour` against the active theme name to a Spectre Color.
    /// Unknown theme slot returns `Color.Default` (graceful degrade — slot
    /// space is open-ended per data-model invariant).
    let private resolveColour (themeName: string) (c: Colour) : SpectreColour =
        match c with
        | Colour.Default ->
            SpectreColour.Default
        | Colour.Rgb (r, g, b) ->
            SpectreColour (r, g, b)
        | Colour.Theme slot ->
            match Map.tryFind (themeName, slot) themeTable with
            | Some col -> col
            | None     -> SpectreColour.Default

    /// Translate an F# Decoration into Spectre's Decoration enum value.
    let private toSpectreDecoration (d: Decoration) : SpectreDecoration =
        match d with
        | Decoration.Bold          -> SpectreDecoration.Bold
        | Decoration.Italic        -> SpectreDecoration.Italic
        | Decoration.Dim           -> SpectreDecoration.Dim
        | Decoration.Underline     -> SpectreDecoration.Underline
        | Decoration.Strikethrough -> SpectreDecoration.Strikethrough

    /// Build a Spectre.Console.Style from our typed Style.
    let private toSpectreStyle (themeName: string) (s: Style) : SpectreStyle =
        let fg = resolveColour themeName s.GetForeground
        let bg = resolveColour themeName s.GetBackground
        let combinedDecoration =
            s.GetDecorations
            |> Seq.fold
                (fun acc d -> acc ||| toSpectreDecoration d)
                SpectreDecoration.None
        SpectreStyle (
            foreground = System.Nullable fg,
            background = System.Nullable bg,
            decoration = System.Nullable combinedDecoration)

    /// Build an ephemeral Spectre AnsiConsole writing to a StringWriter,
    /// honouring `RenderContext` (width clamp + colour-enabled flag).
    let private withSpectre (ctx: RenderContext) (action: IAC -> unit) : string =
        let sw = new StringWriter ()
        let settings = SpectreSettings ()
        settings.Out <- SpectreOutput sw
        settings.ColorSystem <-
            if ctx.GetColourEnabled then
                SpectreSupport.TrueColor
            else
                SpectreSupport.NoColors
        let console = Spectre.Console.AnsiConsole.Create settings
        console.Profile.Width <- max 1 ctx.GetWidth
        action console
        sw.ToString ()

    /// Render a single primitive to its byte stream.
    let private renderPrimitive (ctx: RenderContext) (p: Primitive) : Result<string, RenderError> =
        match p with
        | Primitive.LineBreak ->
            Ok "\r\n"
        | Primitive.RawAnsi bytes ->
            let s =
                match (bytes: string | null) with
                | null -> ""
                | b    -> b
            Ok s
        | Primitive.Styled (style, content) ->
            try
                let text    = SafeText.unwrap content
                let spectre = toSpectreStyle ctx.GetThemeName style
                let output  =
                    withSpectre ctx (fun ac ->
                        ac.Write (SpectreText (text, spectre)))
                Ok output
            with ex ->
                Error (RenderError.RenderFailed ("Styled", ex.Message))
        | Primitive.Markup hint ->
            try
                let output =
                    withSpectre ctx (fun ac ->
                        ac.Write (Spectre.Console.Markup hint))
                Ok output
            with
            | :? System.InvalidOperationException as ex ->
                Error (RenderError.InvalidMarkup (hint, ex.Message))
            | ex ->
                Error (RenderError.RenderFailed ("Markup", ex.Message))
        | Primitive.Rule style ->
            try
                let r = SpectreRule ()
                r.Style <- toSpectreStyle ctx.GetThemeName style
                let output = withSpectre ctx (fun ac -> ac.Write r)
                Ok output
            with ex ->
                Error (RenderError.RenderFailed ("Rule", ex.Message))

    /// Evaluate a Composition into the byte/escape stream the Surface
    /// actor expects. Pure (no side effects on Console.Out, no actor post).
    let toRawAnsi (ctx: RenderContext) (composition: Composition) : Result<string, RenderError> =
        match composition with
        | Composition.Leaf prim -> renderPrimitive ctx prim

    /// Convenience: evaluate to a `DrawOp.RawAnsi` ready to hand to the actor.
    /// Caller posts the result via their existing `Surface` wrapper or
    /// directly via `MailboxProcessor.Post`.
    let toDrawOp (ctx: RenderContext) (composition: Composition) : Result<Fugue.Surface.DrawOp, RenderError> =
        toRawAnsi ctx composition
        |> Result.map Fugue.Surface.DrawOp.RawAnsi
