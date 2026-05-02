namespace Fugue.Surface

/// A logical region of the terminal layout.
/// StatusBarLine1/2 and ThinkingLine occupy the bottom rows of the scroll region.
/// InputArea rows are relative to the input prompt area, 0 = top of that area.
/// ScrollContent is the main scrollable area (free-form append).
/// Modal is a full-screen takeover (e.g. Picker).
[<RequireQualifiedAccess>]
type Region =
    | StatusBarLine1
    | StatusBarLine2
    | ThinkingLine
    | InputArea of row: int   // row index inside input area, 0 = top
    | ScrollContent           // freeform append below scroll cursor
    | Modal                   // full-screen takeover

[<RequireQualifiedAccess>]
type Color =
    | Default
    | Dim
    | Theme of name: string   // resolved by executor against active theme

type Style = {
    Bold:   bool
    Italic: bool
    Color:  Color
}

/// A single drawing command — pure value, no side effects.
[<RequireQualifiedAccess>]
type DrawOp =
    /// Write text into a region at current col with a style.
    | Paint       of region: Region * text: string * style: Style
    /// Clear the entire line for a region (fill with spaces).
    | EraseLine   of region: Region
    /// Move cursor to column within a region (0-based).
    | MoveTo      of region: Region * col: int
    /// Wrap a list of ops in cursor-save/restore (\x1b[s … \x1b[u).
    | SaveAndRestore of inner: DrawOp list
    /// Append text below the scroll cursor (advances row).
    | Append      of text: string
    /// Reset scroll region to full screen — call on shutdown.
    | ResetScrollRegion
    /// Canonical line break: emits CR + LF so the cursor lands at col 0
    /// of the next row regardless of the terminal's `onlcr` mode or
    /// scroll-region state. Use this instead of writing "\n" via RawAnsi —
    /// LF alone leaves the cursor at the previous column on terminals
    /// that don't auto-translate, breaking subsequent cursor-relative
    /// moves (e.g. the post-stream markdown rewind).
    | LineBreak
    /// Move cursor up `rows` rows in the same column (CUU = "\x1b[<n>A").
    /// Negative or zero is a no-op.
    | MoveCursorUp of rows: int
    /// Move cursor up `rows` rows AND to column 0 (CR + CUU). Atomic in
    /// the sense that the actor emits both halves with no other write
    /// interleaved.
    | MoveCursorUpToCol0 of rows: int
    /// Erase from the cursor to the end of the screen (ED = "\x1b[J").
    /// Honours the active scroll region — won't clear anything below it.
    | ClearToEndOfScreen
    /// Verbatim ANSI escape string. Escape hatch for callers (ReadLine,
    /// Picker) that build complex compound sequences with cursor-relative
    /// motion that doesn't fit the structured ops above. The actor writes
    /// it as-is — coalesce does NOT touch RawAnsi (no region key).
    | RawAnsi     of text: string

module Style =
    let normal : Style = { Bold = false; Italic = false; Color = Color.Default }
    let dim    : Style = { Bold = false; Italic = false; Color = Color.Dim    }
    let bold   : Style = { Bold = true;  Italic = false; Color = Color.Default }
