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
/// an invalid value.
type Style =
    private
        { fg: Colour
          bg: Colour
          deco: Set<Decoration> }
    member Background: Colour
    member Decorations: Set<Decoration>
    member Foreground: Colour

module Style =

    /// Empty style: no foreground/background override, no decoration.
    val empty: Style

    /// Smart constructor. Returns Error on invalid theme-slot names.
    val create:
        fg: Colour -> bg: Colour -> deco: Decoration seq -> Result<Style, RenderError>

    val withFg: c: Colour -> s: Style -> Style
    val withBg: c: Colour -> s: Style -> Style
    val withDeco: d: Decoration -> s: Style -> Style

    /// Parse a Spectre-style markup hint string ("bold #ff0000",
    /// "dim italic", "italic underline"). Returns Error on malformed
    /// hints. Bridge for the port window — every successful port should
    /// migrate the call-site away from this onto typed `create`.
    ///
    /// Supported subset: foreground colour name ("default") or `#rrggbb`;
    /// decoration keywords (bold/italic/dim/underline/strikethrough).
    /// The `on <colour>` Spectre background-colour form is NOT supported —
    /// use `Style.create` with an explicit `bg` argument for that.
    val ofMarkupHint: hint: string -> Result<Style, RenderError>
