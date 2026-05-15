# Phase 1 Data Model: F# Console Adapter Library

**Feature**: `001-console-adapter-lib`
**Date**: 2026-05-16
**Depends on**: [research.md](./research.md)

This document specifies the type-level contract — the *capability* surface —
of the adapter. The *syntactic* surface (record-DU / CE / builder
functions) is gated on the R2 dual-debate; the types below are shape-agnostic
and apply equally to all three candidates.

All types live under namespace `Fugue.Adapters.Console`. Smart constructors
are `private` where they enforce invariants. No type exposes a Spectre type
(FR-003).

---

## 1. `RenderError` — typed failure DU

The single error type returned across the adapter boundary. Each case is
named for a concrete failure mode observed in the inventory; no `Other of
string` catch-all (would invite untyped drift).

```fsharp
type RenderError =
    /// Markup string contained an unbalanced or unknown tag.
    /// Currently raised by Spectre as `InvalidOperationException`.
    | InvalidMarkup       of input: string * detail: string
    /// Style.Parse failed (e.g., "bold #zz" — unknown colour hex).
    | InvalidStyleSpec    of spec: string * detail: string
    /// Terminal width reported as ≤ 0 AND the primitive has no
    /// defined fallback (most primitives DO have one — see FR-008).
    | DegenerateWidth     of reportedWidth: int
    /// A composition node was given a child it cannot accept
    /// (e.g., a `Panel` containing nothing — by-construction
    /// preventable, included as a defensive case for the
    /// raw-DU public surface).
    | EmptyComposition    of node: string
    /// Underlying Spectre.Console raised an unexpected exception.
    /// The exception is captured as a string (not as `exn` —
    /// `exn` leaks .NET types into the public API).
    | RenderFailed        of primitive: string * message: string
```

**Invariants**:
- `RenderError` MUST be returned via `Result.Error` — never thrown.
- Each case carries enough context for a `printfn "%A"` to be useful in
  test failure output (named primitive, offending input).
- Adding a new case is a MINOR semver bump for the adapter library; renaming
  or removing a case is MAJOR.

---

## 2. `Style` — typed text styling

Replaces 279+ markup-string call-sites and the ad-hoc `Style.Parse` uses.
Smart-constructed; `Style.create` validates colour hex format and
decoration combinations.

```fsharp
type Colour =
    /// Default terminal foreground.
    | Default
    /// 24-bit RGB. Smart-constructed via `Colour.rgb`.
    | Rgb of r: byte * g: byte * b: byte
    /// Named theme slot — resolved against the active theme at render
    /// time. Hides the existing `Repl.fs` "nocturne → #1e3a5a" lookup
    /// and similar branches. See "Canonical theme slots" below for the
    /// authoritative list shipped at v1.
    | Theme of slot: string

type Decoration =
    | Bold
    | Italic
    | Dim
    | Underline
    | Strikethrough

type Style =
    private {
        Foreground:  Colour
        Background:  Colour
        Decorations: Set<Decoration>
    }

module Style =
    /// Default style: no foreground/background override, no decoration.
    val empty       : Style

    /// Smart constructor — validates colour hex if `Theme` slot name
    /// uses one, validates decoration set is non-conflicting.
    val create      : fg: Colour -> bg: Colour -> deco: Decoration seq
                   -> Result<Style, RenderError>

    /// Pipeline-friendly modifiers.
    val withFg      : Colour -> Style -> Style
    val withBg      : Colour -> Style -> Style
    val withDeco    : Decoration -> Style -> Style

    /// Bridge: parse a Spectre-style markup hint string ("bold #ff0000",
    /// "dim", "italic underline"). Returns `Error InvalidStyleSpec` on
    /// failure. EXISTS PURELY TO EASE PORTING — every successful port
    /// SHOULD migrate the call-site away from this onto typed `create`.
    val ofMarkupHint : string -> Result<Style, RenderError>
```

**Invariants**:
- `Style` is a `private`-field record. Callers cannot construct an
  invalid `Style` value. *(In the public `.fsi` this is expressed as
  `[<Sealed>] type Style` with read-only `member` accessors — see
  `contracts/Fugue.Adapters.Console.fsi`; the two notations describe the
  same shape.)*
- `Decorations` is a `Set` — adding the same decoration twice is a no-op,
  not an error.
- `ofMarkupHint` is the bridge for "critical finding 1" of research.md
  inventory — it accepts existing markup hints during the port window so
  not every call-site has to flip at once. It is NOT the recommended
  permanent API.

#### Canonical theme slots (v1)

The slot table below is the **single source of truth** for theme-slot
names. T029 in tasks.md MUST implement exactly this set inside the
renderer's private resolver; quickstart.md §10 references this section
when describing the slot taxonomy. Adding a slot is a v-MINOR change
(new public name); renaming or removing one is v-MAJOR.

| Slot | Default colour (`default` theme) | Nocturne colour | Used for |
|---|---|---|---|
| `accent`         | `#00afff` (bright cyan)  | `#1e3a5a` (deep blue)    | Highlights, primary callouts |
| `warning`        | `#d7af00` (yellow)       | `#b58900` (muted yellow) | Approval prompts, non-fatal notices |
| `error`          | `#d70000` (red)          | `#cb4b16` (muted red)    | Tool errors, exit messages |
| `dim-accent`     | `#808080` (grey)         | `#586e75` (slate)        | Spinner, status-bar dim text |
| `diff-add`       | `#00af00` (green)        | `#859900` (muted green)  | Diff `+` lines |
| `diff-remove`    | `#d70000` (red)          | `#cb4b16` (muted red)    | Diff `-` lines |
| `diff-hunk`      | `#d7af00` (yellow)       | `#b58900` (muted yellow) | Diff `@@` hunk markers |

Unknown slot names resolve to `Colour.Default` (no error — slot space is
open-ended; misspellings degrade gracefully).

---

## 3. `SafeText` — escape-tracked user input

Wraps the 100+ `Markup.Escape` call-sites (research.md "critical finding
2") and makes "I forgot to escape this user string" a type error.

```fsharp
type SafeText = private SafeText of string

module SafeText =
    /// Wrap arbitrary user input — applies `Markup.Escape` exactly once.
    /// Idempotent: `ofUser (unwrap (ofUser s)) = ofUser s`.
    val ofUser    : string -> SafeText

    /// Wrap a string that the *caller* has already escaped (or knows is
    /// markup-free). Use sparingly — the `ofUser` path is the safe default.
    val ofLiteral : string -> SafeText

    /// Read out the escaped string (for serialisers / tests).
    val unwrap    : SafeText -> string
```

**Invariants**:
- `SafeText` cannot be constructed except via the two named functions.
  Direct DU construction is private.
- `ofUser` is idempotent (see property-based test below).
- `unwrap (ofUser s) = Markup.Escape(s)` — verified by snapshot.

**Property test** (FsCheck):
```fsharp
[<Property>]
let ``ofUser is idempotent`` (s: string) =
    let once  = SafeText.ofUser s |> SafeText.unwrap
    let twice = SafeText.ofUser once |> SafeText.unwrap
    once = twice
```

---

## 4. `Primitive` — leaf render nodes

Maps 1:1 against the inventory's top-six types (`Markup`, `Text`, `Rule`,
`Padder`-base, raw ANSI passthrough, line break).

```fsharp
type Primitive =
    /// Styled text. Most common primitive — replaces direct `Markup` /
    /// `Text` construction with `[red]...[/]` strings.
    | Styled of style: Style * content: SafeText
    /// Raw markup hint string (BRIDGE — replaces a direct
    /// `Markup "[red]hi[/]"` call). Caller asserts markup balance;
    /// adapter validates at render time and returns InvalidMarkup on bad
    /// input.
    | Markup of hint: string
    /// Pre-rendered ANSI byte sequence (escape passthrough — replaces
    /// `Surface.write rawAnsiString` direct calls inside rendering code).
    | RawAnsi of bytes: string
    /// Forced line break (CR+LF).
    | LineBreak
    /// Horizontal rule (replaces `Rule()` construction in MarkdownRender).
    | Rule of style: Style
```

**Invariants**:
- All cases carry typed `Style` or `SafeText` where applicable — no raw
  markup strings except in the explicit `Markup` bridge case.
- `Markup` case is intended for incremental port; it carries a code-review
  smell (every use is a hint to migrate to `Styled`).

---

## 5. `Composition` — recursive layout tree

Replaces the `Rows` / `Panel` / `Padder` / `Align` / `Columns` / `Grid`
composition tree. Recursive because compositions nest (Padder inside
Panel inside Rows is common in `Render.fs` bubble mode).

```fsharp
type Border =
    | None_
    | Square
    | Rounded
    | Heavy

type Alignment =
    | LeftAlign
    | CentreAlign
    | RightAlign

type Composition =
    /// Leaf — wraps a single Primitive.
    | Leaf      of Primitive
    /// Vertically stacked children (Spectre `Rows`).
    | Stack     of Composition list
    /// Inner content padded by N cells left/right and M rows top/bottom.
    | Padded    of left: int * right: int * top: int * bottom: int
                 * inner: Composition
    /// Border-decorated panel, optional header.
    | Panel     of border: Border * header: SafeText option
                 * inner: Composition
    /// Horizontal columns. Width hints expressed as ratios that sum
    /// to ≤ 1.0 (validated by smart constructor on `Composition.columns`).
    | Columns   of children: (float * Composition) list
    /// Alignment within the available width.
    | Aligned   of Alignment * inner: Composition
    /// Markdown-style table with header row + body rows.
    /// (Specialised — markdown rendering's only complex case.)
    | Table     of headers: Composition list
                 * rows:    Composition list list

module Composition =
    /// Smart constructors that validate degenerate inputs.
    val stack    : Composition seq    -> Composition
    val padded   : int -> int -> int -> int -> Composition -> Result<Composition, RenderError>
    val panel    : Border -> SafeText option -> Composition -> Composition
    val columns  : (float * Composition) seq
                 -> Result<Composition, RenderError>  // Error: ratios > 1.0
    val aligned  : Alignment -> Composition -> Composition
    val table    : Composition seq -> Composition seq seq
                 -> Result<Composition, RenderError>  // Error: ragged rows
```

**Invariants**:
- `Padded` / `Columns` / `Table` smart constructors reject degenerate
  inputs at *build time*, not render time. Once a `Composition` value
  exists, rendering it cannot fail for structural reasons (only for
  `InvalidMarkup` inside a leaf or `DegenerateWidth` at the context level).
- `Composition` is immutable. Compositions can be cached / memoised.

---

## 6. `RenderContext` — externalised capabilities

Describes the host terminal. Threaded into the renderer; never owned by
the adapter (Assumption in spec.md: "host process retains ownership").

```fsharp
type RenderContext = {
    /// Terminal width in cells. ≤ 0 triggers Edge Case "Width 0 or
    /// negative" — primitives fall back to their defined minimum.
    Width:         int
    /// Whether ANSI colour escapes may be emitted (FR-007).
    ColourEnabled: bool
    /// Active theme name — used by `Colour.Theme slot` resolution.
    /// "default", "nocturne", "monochrome", etc.
    ThemeName:     string
}

module RenderContext =
    /// Probe the host once at session start; pass the result downstream.
    /// THIS FUNCTION CALLS `System.Console` — the only IO probe in the
    /// adapter, isolated to one function for trivial mocking in tests.
    val probe : unit -> RenderContext

    /// Test-only constructor.
    val create : width: int -> colourEnabled: bool -> theme: string -> RenderContext
```

**Invariants**:
- `probe` is the *only* function in the adapter that reads `System.Console`.
- Test code uses `create` directly; `probe` is never called from tests.

---

## 7. `Renderer` — evaluation seam

The bridge to `Fugue.Surface`. Produces values; does NOT touch
`Console.Out` (FR-009, R4).

```fsharp
module Renderer =
    /// Evaluate a Composition into the byte/escape stream the Surface
    /// actor expects. The single function `Surface.fs::spectre` is
    /// reimagined here, behind a `Result`-shaped boundary.
    val toRawAnsi  : RenderContext -> Composition
                  -> Result<string, RenderError>

    /// Convenience: evaluate to a `DrawOp.RawAnsi` ready to hand to the
    /// actor.  Equivalent to `toRawAnsi |> Result.map DrawOp.RawAnsi`.
    val toDrawOp   : RenderContext -> Composition
                  -> Result<Fugue.Surface.DrawOp, RenderError>

    /// Convenience for the common case: evaluate AND post to the actor
    /// via `Surface.batch`.  Returns the Result so callers can decide
    /// whether to log / surface render errors.
    val render     : RenderContext -> Composition -> Result<unit, RenderError>
```

**Invariants**:
- `toRawAnsi` is pure (modulo Spectre's internal allocations) — given the
  same `RenderContext` and `Composition`, the output is byte-stable.
  This is the property the Verify snapshot tests rest on (R3).
- `render` is the *only* function with side effects, and its only side
  effect is `Surface.batch` (no `Console.Out`, no global mutation).

---

## State transitions

The adapter is stateless. There are no lifecycle states.

The *port window* — which call-sites have been migrated, which haven't — is
tracked outside the adapter (in `quickstart.md` as a checklist, and in PR
descriptions). The adapter itself does not track which version of which
call-site exists.

---

## Type-shape summary

| Type | Shape | Why |
|---|---|---|
| `RenderError` | DU | exhaustive matching at call sites; named cases > stringly typed |
| `Colour`, `Decoration`, `Border`, `Alignment` | DU | small closed enumeration; trivially serialisable |
| `Style`, `SafeText`, `RenderContext` | record with private fields | invariants enforced by smart constructors |
| `Primitive`, `Composition` | DU (recursive for Composition) | leaf set is small + closed; composition tree is naturally recursive; pattern matching makes `Renderer.toRawAnsi` exhaustive — adding a new case forces every renderer arm to update (intentional friction) |

This shape is consistent with all three R2 candidates (record-DU / CE /
builder functions). The CE and builder candidates would expose a different
*syntax* for constructing the same `Composition` values; the wire
representation is shared.
