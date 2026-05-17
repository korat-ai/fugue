namespace Fugue.Adapters.Console

open System.IO

/// Renderer evaluates Composition values to byte/escape streams.
/// Pure (modulo Spectre's internal allocations); never touches
/// `Console.Out` directly. Callers route the output to the Fugue.Surface
/// actor themselves (via `DrawOp.RawAnsi`). The integration seam
/// described in research.md §R4.
///
/// NOTE: `Renderer.render` was dropped from the public API. `Surface.batch`
/// lives in `Fugue.Cli.Surface` (REPL-only), not in `Fugue.Surface`;
/// pulling this adapter into Fugue.Cli's namespace violates the "pure render
/// layer" assumption (spec.md Assumptions). Callers post the DrawOp
/// themselves via their own Surface/actor wiring.
module Renderer =

    // Local aliases to avoid namespace pollution when our F# DU cases share
    // names with Spectre's enums and types (Decoration, Style, Border, etc.).
    type private SpectreColour      = Spectre.Console.Color
    type private SpectreStyle       = Spectre.Console.Style
    type private SpectreDecoration  = Spectre.Console.Decoration
    type private SpectreText        = Spectre.Console.Text
    type private SpectreRule        = Spectre.Console.Rule
    type private SpectreSettings    = Spectre.Console.AnsiConsoleSettings
    type private SpectreOutput      = Spectre.Console.AnsiConsoleOutput
    type private SpectreSupport     = Spectre.Console.ColorSystemSupport
    type private IAC                = Spectre.Console.IAnsiConsole
    type private SpectreRows        = Spectre.Console.Rows
    type private SpectrePadder      = Spectre.Console.Padder
    type private SpectrePanel       = Spectre.Console.Panel
    type private SpectrePanelHeader = Spectre.Console.PanelHeader
    type private SpectreBoxBorder   = Spectre.Console.BoxBorder
    type private SpectreColumns     = Spectre.Console.Columns
    type private SpectreAlign       = Spectre.Console.Align
    type private SpectreHAlign      = Spectre.Console.HorizontalAlignment
    type private SpectreTable       = Spectre.Console.Table
    type private SpectreTableCol    = Spectre.Console.TableColumn
    type private SpectreMarkup      = Spectre.Console.Markup
    type private SpectrePadding     = Spectre.Console.Padding
    type private IRenderable        = Spectre.Console.Rendering.IRenderable

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
            ("default",  "accent"),       hex "#00afff"
            ("default",  "warning"),      hex "#d7af00"
            ("default",  "error"),        hex "#d70000"
            ("default",  "dim-accent"),   hex "#808080"
            ("default",  "diff-add"),     hex "#00af00"
            ("default",  "diff-remove"),  hex "#d70000"
            ("default",  "diff-hunk"),    hex "#d7af00"
            ("nocturne", "accent"),       hex "#1e3a5a"
            ("nocturne", "warning"),      hex "#b58900"
            ("nocturne", "error"),        hex "#cb4b16"
            ("nocturne", "dim-accent"),   hex "#586e75"
            ("nocturne", "diff-add"),     hex "#859900"
            ("nocturne", "diff-remove"),  hex "#cb4b16"
            ("nocturne", "diff-hunk"),    hex "#b58900"
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
        let fg = resolveColour themeName s.Foreground
        let bg = resolveColour themeName s.Background
        let combinedDecoration =
            s.Decorations
            |> Seq.fold
                (fun acc d -> acc ||| toSpectreDecoration d)
                SpectreDecoration.None
        SpectreStyle (
            foreground = System.Nullable fg,
            background = System.Nullable bg,
            decoration = System.Nullable combinedDecoration)

    /// Map our Border DU to Spectre's BoxBorder static properties.
    let private toSpectreBoxBorder (b: Border) : SpectreBoxBorder =
        match b with
        | Border.None_   -> SpectreBoxBorder.None
        | Border.Square  -> SpectreBoxBorder.Square
        | Border.Rounded -> SpectreBoxBorder.Rounded
        | Border.Heavy   -> SpectreBoxBorder.Heavy

    /// Map our Alignment DU to Spectre's HorizontalAlignment enum.
    let private toSpectreHAlign (a: Alignment) : SpectreHAlign =
        match a with
        | Alignment.LeftAlign   -> SpectreHAlign.Left
        | Alignment.CentreAlign -> SpectreHAlign.Center
        | Alignment.RightAlign  -> SpectreHAlign.Right

    /// Build an ephemeral Spectre AnsiConsole writing to a StringWriter,
    /// honouring `RenderContext` (width clamp + colour-enabled flag).
    let private withSpectre (ctx: RenderContext) (action: IAC -> unit) : string =
        let sw = new StringWriter ()
        let settings = SpectreSettings ()
        settings.Out <- SpectreOutput sw
        settings.ColorSystem <-
            if ctx.ColourEnabled then
                SpectreSupport.TrueColor
            else
                SpectreSupport.NoColors
        let console = Spectre.Console.AnsiConsole.Create settings
        console.Profile.Width <- max 1 ctx.Width
        action console
        sw.ToString ()

    /// Render a single primitive to its raw ANSI byte stream.
    let private renderPrimitive (ctx: RenderContext) (p: Primitive) : Result<string, RenderError> =
        match p with
        | Primitive.LineBreak ->
            Ok "\r\n"
        | Primitive.RawAnsi bytes ->
            // `bytes` is a non-nullable F# string; the null branch is dead code.
            // Principle II prevents C# from constructing DU cases directly, so
            // null can never flow in from outside the F# boundary.
            Ok bytes
        | Primitive.Styled (style, content) ->
            try
                let text    = SafeText.unwrap content
                let spectre = toSpectreStyle ctx.ThemeName style
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
                        ac.Write (SpectreMarkup hint))
                Ok output
            with
            | :? System.InvalidOperationException as ex ->
                Error (RenderError.InvalidMarkup (hint, ex.Message))
            | ex ->
                Error (RenderError.RenderFailed ("Markup", ex.Message))
        | Primitive.Rule style ->
            try
                let r = SpectreRule ()
                r.Style <- toSpectreStyle ctx.ThemeName style
                let output = withSpectre ctx (fun ac -> ac.Write r)
                Ok output
            with ex ->
                Error (RenderError.RenderFailed ("Rule", ex.Message))

    // Forward declaration — `toRenderable` and `toRawAnsi` are mutually
    // recursive (a composition may contain a composition).
    // We use a local recursive binding pair rather than module-level `and`
    // (which F# doesn't support for let-bound values in a module).

    /// Convert a `Primitive` directly into a Spectre `IRenderable` without
    /// going through the render-to-string path. This is required for composition
    /// nodes (Stack, Panel, etc.) which need to own the IRenderable tree so
    /// Spectre can handle layout internally.
    ///
    /// LineBreak and RawAnsi cannot be represented as IRenderable — they are
    /// pass-through and must be rendered at the leaf level. When a composition
    /// node encounters one, it renders the primitive to a string and wraps it
    /// in SpectreText (no markup parsing — plain text only).
    let private primitiveToRenderable (ctx: RenderContext) (prim: Primitive) : Result<IRenderable, RenderError> =
        match prim with
        | Primitive.LineBreak ->
            // Newlines inside a Rows/Panel etc. are expressed as empty text.
            Ok (SpectreText "\n" :> IRenderable)
        | Primitive.RawAnsi bytes ->
            // Raw ANSI bytes: use SpectreText so Spectre doesn't try to parse markup.
            // `bytes` is non-nullable; see renderPrimitive for null-guard rationale.
            Ok (SpectreText bytes :> IRenderable)
        | Primitive.Styled (style, content) ->
            let text    = SafeText.unwrap content
            let spectre = toSpectreStyle ctx.ThemeName style
            Ok (SpectreText (text, spectre) :> IRenderable)
        | Primitive.Markup hint ->
            try
                Ok (SpectreMarkup hint :> IRenderable)
            with
            | :? System.InvalidOperationException as ex ->
                Error (RenderError.InvalidMarkup (hint, ex.Message))
            | ex ->
                Error (RenderError.RenderFailed ("Markup", ex.Message))
        | Primitive.Rule style ->
            let r = SpectreRule ()
            r.Style <- toSpectreStyle ctx.ThemeName style
            Ok (r :> IRenderable)

    // ------------------------------------------------------------------
    // traverseM — collect Results over a list, fail on first error.
    // Accumulates in reverse with :: then reverses once, giving O(n) rather
    // than the O(n²) `lst @ [r]` pattern.  Used wherever we map a list of
    // Composition nodes to a list of IRenderable values.
    // ------------------------------------------------------------------
    let private traverseM (f: 'a -> Result<'b, RenderError>) (xs: 'a list) : Result<'b list, RenderError> =
        let folder acc x =
            match acc with
            | Error _ -> acc
            | Ok lst  ->
                match f x with
                | Error e -> Error e
                | Ok v    -> Ok (v :: lst)
        xs |> List.fold folder (Ok []) |> Result.map List.rev

    /// Convert a `Composition` value into a Spectre `IRenderable`, collecting
    /// the first render error encountered in any branch of the tree.
    let rec private toRenderable
        (ctx:  RenderContext)
        (comp: Composition)
        : Result<IRenderable, RenderError> =
        match comp with
        | Composition.Leaf prim ->
            primitiveToRenderable ctx prim

        | Composition.Stack children ->
            children
            |> traverseM (toRenderable ctx)
            |> Result.map (fun rs -> SpectreRows (rs |> List.toArray) :> IRenderable)

        | Composition.Padded (left, right, top, bottom, inner) ->
            match toRenderable ctx inner with
            | Error e -> Error e
            | Ok r    ->
                let padding = SpectrePadding (left, top, right, bottom)
                Ok (SpectrePadder (r, System.Nullable padding) :> IRenderable)

        | Composition.Panel (border, header, inner) ->
            match toRenderable ctx inner with
            | Error e -> Error e
            | Ok r    ->
                let p = SpectrePanel r
                p.Border <- toSpectreBoxBorder border
                match header with
                | None     -> ()
                | Some txt ->
                    p.Header <- SpectrePanelHeader (SafeText.unwrap txt, System.Nullable ())
                Ok (p :> IRenderable)

        | Composition.Columns children ->
            children
            |> traverseM (fun (_, child) -> toRenderable ctx child)
            |> Result.map (fun rs -> SpectreColumns (rs |> List.toArray) :> IRenderable)

        | Composition.Aligned (alignment, inner) ->
            match toRenderable ctx inner with
            | Error e -> Error e
            | Ok r    ->
                Ok (SpectreAlign (r, toSpectreHAlign alignment, System.Nullable ()) :> IRenderable)

        | Composition.Table (headers, rows) ->
            headers
            |> traverseM (toRenderable ctx)
            |> Result.bind (fun headerRenderables ->
                let t = SpectreTable ()
                for hr in headerRenderables do
                    t.AddColumn (SpectreTableCol hr) |> ignore
                // Build each body row, failing on first error.
                let mutable rowError : RenderError option = None
                for row in rows do
                    if rowError.IsNone then
                        match row |> traverseM (toRenderable ctx) with
                        | Error e      -> rowError <- Some e
                        | Ok cellRs    -> t.Rows.Add (cellRs |> List.toSeq) |> ignore
                match rowError with
                | Some e -> Error e
                | None   -> Ok (t :> IRenderable))

        | Composition.Foreign r ->
            // Bridge case: the user supplied an externally-defined IRenderable
            // via Renderable.fromSpectre. Unwrap the opaque handle and return
            // it directly — Spectre owns the rendering from here.
            // Exception boundary (FR-009): any throw from the backend is caught
            // and surfaced as RenderFailed rather than propagating to the caller.
            // backendObj returns obj (SC-004: Spectre type not in .fsi); downcast here.
            let backendNullable = Renderable.backendObj r
            let typeName        = Renderable.typeName r
            try
                // Null check first (backend stored as obj|null to avoid Spectre type in .fsi).
                // The null case means Renderable.fromSpectre was called with null — surface as
                // RenderFailed at render time per data-model.md §2.2 null-acceptance rule.
                let backend =
                    match backendNullable with
                    | null -> failwith $"Foreign IRenderable backend is null (type: {typeName})"
                    | b    -> b :?> IRenderable
                Ok backend
            with ex ->
                Error (RenderError.RenderFailed (typeName, ex.Message))

    /// Evaluate a Composition into the byte/escape stream the Surface
    /// actor expects. Pure (no side effects on Console.Out, no actor post).
    let toRawAnsi (ctx: RenderContext) (composition: Composition) : Result<string, RenderError> =
        // For Leaf we use the dedicated renderPrimitive path (which handles
        // LineBreak and RawAnsi as pass-through — no Spectre IRenderable needed).
        // For all other cases we build an IRenderable and render it in one pass.
        match composition with
        | Composition.Leaf prim ->
            renderPrimitive ctx prim
        | other ->
            match toRenderable ctx other with
            | Error e -> Error e
            | Ok r    ->
                try
                    let output = withSpectre ctx (fun ac -> ac.Write r)
                    Ok output
                with ex ->
                    Error (RenderError.RenderFailed ("Composition", ex.Message))

    /// Convenience: evaluate to a `DrawOp.RawAnsi` ready to hand to the actor.
    /// Caller posts the result via their existing `Surface` wrapper or
    /// directly via `MailboxProcessor.Post`.
    let toDrawOp (ctx: RenderContext) (composition: Composition) : Result<Fugue.Surface.DrawOp, RenderError> =
        toRawAnsi ctx composition
        |> Result.map Fugue.Surface.DrawOp.RawAnsi
