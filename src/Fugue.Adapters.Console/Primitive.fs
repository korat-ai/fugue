namespace Fugue.Adapters.Console

/// Leaf render nodes. Closed DU — every Renderer arm must handle all
/// 5 cases. See data-model.md §4.
type Primitive =
    /// Styled text — common case. Replaces direct `Markup`/`Text`.
    | Styled    of style: Style * content: SafeText
    /// Raw markup hint (BRIDGE — replaces `Markup "[red]hi[/]"`).
    /// Caller asserts markup balance; adapter validates at render time
    /// and returns InvalidMarkup on bad input.
    | Markup    of hint: string
    /// Pre-rendered ANSI byte sequence (escape passthrough).
    | RawAnsi   of bytes: string
    /// Forced line break (CR+LF).
    | LineBreak
    /// Horizontal rule.
    | Rule      of style: Style
