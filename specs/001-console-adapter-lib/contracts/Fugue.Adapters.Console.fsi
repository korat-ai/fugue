// =============================================================================
// PLANNING-PHASE CONTRACT SKETCH — NOT THE FINAL `.fsi` IN src/.
//
// This file describes the public API surface the adapter MUST expose,
// expressed as an F# signature file for reviewer convenience.  It is
// COMPILATION-CHECKED for syntactic plausibility only — implementation
// in `src/Fugue.Adapters.Console/` will produce the real `Console.fsi`.
//
// Scope: every type and function callable from outside the adapter project.
// Anything private to the implementation is not listed here.
//
// Invariants this contract enforces:
//   * No Spectre.Console type appears in any signature (FR-003).
//   * Every fallible operation returns Result<_, RenderError> (FR-004).
//   * `SafeText` is private-constructed (escapes are tracked at type level).
//   * `Style`, `RenderContext` are private records (smart-constructed).
//
// The R2 dual-debate may choose to expose ADDITIONAL surface (a CE builder,
// a fluent builder module) on top of this contract — but the contract below
// is the MINIMUM every R2 candidate MUST satisfy.
// =============================================================================

namespace Fugue.Adapters.Console

open Fugue.Surface

// -----------------------------------------------------------------------------
// Error type
// -----------------------------------------------------------------------------

type RenderError =
    | InvalidMarkup    of input: string * detail: string
    | InvalidStyleSpec of spec: string * detail: string
    | DegenerateWidth  of reportedWidth: int
    | EmptyComposition of node: string
    | RenderFailed     of primitive: string * message: string

// -----------------------------------------------------------------------------
// Styling
// -----------------------------------------------------------------------------

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

[<Sealed>]
type Style =
    member Foreground:  Colour
    member Background:  Colour
    member Decorations: Set<Decoration>

module Style =
    val empty        : Style
    val create       : fg: Colour -> bg: Colour -> deco: Decoration seq
                    -> Result<Style, RenderError>
    val withFg       : Colour -> Style -> Style
    val withBg       : Colour -> Style -> Style
    val withDeco     : Decoration -> Style -> Style
    val ofMarkupHint : string -> Result<Style, RenderError>

// -----------------------------------------------------------------------------
// Escape-tracked text
// -----------------------------------------------------------------------------

[<Sealed>]
type SafeText

module SafeText =
    val ofUser    : string -> SafeText
    val ofLiteral : string -> SafeText
    val unwrap    : SafeText -> string

// -----------------------------------------------------------------------------
// Primitives + composition tree
// -----------------------------------------------------------------------------

type Primitive =
    | Styled    of style: Style * content: SafeText
    | Markup    of hint: string
    | RawAnsi   of bytes: string
    | LineBreak
    | Rule      of style: Style

type Border    = | None_ | Square | Rounded | Heavy
type Alignment = | LeftAlign | CentreAlign | RightAlign

type Composition =
    | Leaf    of Primitive
    | Stack   of Composition list
    | Padded  of left: int * right: int * top: int * bottom: int
              * inner: Composition
    | Panel   of border: Border * header: SafeText option
              * inner: Composition
    | Columns of (float * Composition) list
    | Aligned of Alignment * inner: Composition
    | Table   of headers: Composition list * rows: Composition list list

module Composition =
    val stack    : Composition seq -> Composition
    val padded   : int -> int -> int -> int -> Composition -> Result<Composition, RenderError>
    val panel    : Border -> SafeText option -> Composition -> Composition
    val columns  : (float * Composition) seq -> Result<Composition, RenderError>
    val aligned  : Alignment -> Composition -> Composition
    val table    : Composition seq -> Composition seq seq -> Result<Composition, RenderError>

// -----------------------------------------------------------------------------
// Context
// -----------------------------------------------------------------------------

[<Sealed>]
type RenderContext =
    member Width:         int
    member ColourEnabled: bool
    member ThemeName:     string

module RenderContext =
    val probe  : unit -> RenderContext
    val create : width: int -> colourEnabled: bool -> theme: string -> RenderContext

// -----------------------------------------------------------------------------
// Renderer — the integration seam with Fugue.Surface
// -----------------------------------------------------------------------------

module Renderer =
    val toRawAnsi : RenderContext -> Composition -> Result<string, RenderError>
    val toDrawOp  : RenderContext -> Composition -> Result<DrawOp, RenderError>
    val render    : RenderContext -> Composition -> Result<unit, RenderError>
