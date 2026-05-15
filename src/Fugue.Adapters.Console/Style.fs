namespace Fugue.Adapters.Console

/// Foreground/background colour. `Theme` slots resolve against the active
/// theme name carried by `RenderContext` at render time. Canonical slot
/// list lives in data-model.md §2 "Canonical theme slots (v1)".
type Colour =
    | Default
    | Rgb of r: byte * g: byte * b: byte
    | Theme of slot: string

type Decoration =
    | Bold
    | Italic
    | Dim
    | Underline
    | Strikethrough

/// Smart-constructed text style. Private record so callers cannot construct
/// an invalid value; `[<Sealed>]` on the public surface (`Console.fsi`).
type Style =
    private {
        Foreground:  Colour
        Background:  Colour
        Decorations: Set<Decoration>
    }
    member this.GetForeground  = this.Foreground
    member this.GetBackground  = this.Background
    member this.GetDecorations = this.Decorations

module Style =
    /// Empty style: no foreground/background override, no decoration.
    let empty : Style =
        { Foreground = Colour.Default
          Background = Colour.Default
          Decorations = Set.empty }

    /// Validates a Theme-slot name is non-empty and contains only
    /// printable lowercase ASCII / `-` / digits. Returns the slot string
    /// or an InvalidStyleSpec error.
    let private validateSlot (slot: string) : Result<string, RenderError> =
        if System.String.IsNullOrWhiteSpace slot then
            Error (InvalidStyleSpec (slot, "theme slot must be non-empty"))
        elif slot
             |> Seq.forall (fun c ->
                 (c >= 'a' && c <= 'z')
                 || (c >= '0' && c <= '9')
                 || c = '-')
        then Ok slot
        else Error (InvalidStyleSpec (slot, "theme slot must be lowercase a-z, 0-9, or '-'"))

    let private validateColour (c: Colour) : Result<unit, RenderError> =
        match c with
        | Colour.Default | Colour.Rgb _ -> Ok ()
        | Colour.Theme s -> validateSlot s |> Result.map ignore

    /// Smart constructor. Returns Error on invalid theme-slot names.
    let create (fg: Colour) (bg: Colour) (deco: Decoration seq) : Result<Style, RenderError> =
        match validateColour fg with
        | Error e -> Error e
        | Ok () ->
            match validateColour bg with
            | Error e -> Error e
            | Ok () ->
                Ok { Foreground = fg
                     Background = bg
                     Decorations = deco |> Set.ofSeq }

    let withFg (c: Colour) (s: Style) : Style =
        { s with Foreground = c }

    let withBg (c: Colour) (s: Style) : Style =
        { s with Background = c }

    let withDeco (d: Decoration) (s: Style) : Style =
        { s with Decorations = s.Decorations |> Set.add d }

    /// Parse a Spectre-style markup hint string ("bold #ff0000",
    /// "dim italic", "italic underline"). Returns Error on malformed
    /// hints. Bridge for the port window — every successful port should
    /// migrate the call-site away from this onto typed `create`.
    let ofMarkupHint (hint: string) : Result<Style, RenderError> =
        if System.String.IsNullOrWhiteSpace hint then
            Ok empty
        else
            let tokens =
                hint.Split([| ' '; '\t' |], System.StringSplitOptions.RemoveEmptyEntries)
            let mutable fg = Colour.Default
            let mutable decos : Set<Decoration> = Set.empty
            let mutable err : RenderError option = None
            for tok in tokens do
                if err.IsNone then
                    match tok.ToLowerInvariant() with
                    | "bold"           -> decos <- decos.Add Bold
                    | "italic"         -> decos <- decos.Add Italic
                    | "dim"            -> decos <- decos.Add Dim
                    | "underline"      -> decos <- decos.Add Underline
                    | "strikethrough"  -> decos <- decos.Add Strikethrough
                    | "default"        -> fg <- Colour.Default
                    | t when t.StartsWith "#" && t.Length = 7 ->
                        let tryParse (h: string) =
                            try
                                Ok (System.Convert.ToByte (h, 16))
                            with _ ->
                                Error (InvalidStyleSpec (hint, "bad hex byte: " + h))
                        match tryParse (t.Substring(1, 2)), tryParse (t.Substring(3, 2)), tryParse (t.Substring(5, 2)) with
                        | Ok r, Ok g, Ok b -> fg <- Colour.Rgb (r, g, b)
                        | Error e, _, _ | _, Error e, _ | _, _, Error e -> err <- Some e
                    | other ->
                        err <- Some (InvalidStyleSpec (hint, "unknown style token: " + other))
            match err with
            | Some e -> Error e
            | None ->
                Ok { Foreground = fg
                     Background = Colour.Default
                     Decorations = decos }
