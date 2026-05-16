module Fugue.Adapters.Console.Tests.FoundationTests

open FsCheck.Xunit
open Fugue.Adapters.Console

// ============================================================================
// NOTE on test framework choice:
// `[<Property>]` (FsCheck.Xunit) is the ONLY xunit-discoverable attribute in
// this assembly. Plain `[<Fact>]` (xunit) is *not* discovered — root cause not
// found despite identical fsproj/package setup to `tests/Fugue.Tests/`. Until
// the discovery issue is resolved (see follow-up TODO in tasks.md), Fact-style
// tests are written as single-iteration properties via `[<Property(MaxTest=1)>]`,
// returning bool. This is uglier but functionally equivalent and ships now.
// ============================================================================

// ---------- SafeText (T011, M2 absorbed) ----------
//
// NOTE: `ofUser` is NOT idempotent (Markup.Escape doubles `[` → `[[`, then
// `[[` → `[[[[`). The correct invariant: re-wrapping an already-escaped
// string through `ofLiteral` is stable. `ofUser` should be called exactly
// once per raw user string; that's what the type system encodes.

[<Property>]
let ``ofLiteral over the unwrap of ofUser is stable`` (s: string) =
    let userEscaped = SafeText.unwrap (SafeText.ofUser s)
    let literalAgain = SafeText.unwrap (SafeText.ofLiteral userEscaped)
    userEscaped = literalAgain

[<Property>]
let ``SafeText.ofUser never throws on arbitrary string`` (s: string) =
    let _ : SafeText = SafeText.ofUser s
    true

[<Property(MaxTest = 1)>]
let ``SafeText.ofUser null is empty`` () =
    let t : string | null = null
    SafeText.unwrap (SafeText.ofUser t) = ""

[<Property(MaxTest = 1)>]
let ``SafeText.ofLiteral null is empty`` () =
    let t : string | null = null
    SafeText.unwrap (SafeText.ofLiteral t) = ""

// ---------- Style.create (T012) ----------

[<Property(MaxTest = 1)>]
let ``Style.create empty inputs returns empty-equivalent style`` () =
    match Style.create Colour.Default Colour.Default [] with
    | Ok s ->
        s.Foreground = Colour.Default
        && s.Background = Colour.Default
        && Set.isEmpty s.Decorations
    | Error _ -> false

[<Property(MaxTest = 1)>]
let ``Style.create with valid theme slot succeeds`` () =
    match Style.create (Colour.Theme "error") Colour.Default [ Decoration.Bold ] with
    | Ok s ->
        s.Foreground = Colour.Theme "error"
        && Set.contains Decoration.Bold s.Decorations
    | Error _ -> false

[<Property(MaxTest = 1)>]
let ``Style.create with empty theme slot returns InvalidStyleSpec`` () =
    match Style.create (Colour.Theme "") Colour.Default [] with
    | Error (RenderError.InvalidStyleSpec _) -> true
    | _ -> false

[<Property(MaxTest = 1)>]
let ``Style.create with whitespace-only theme slot returns InvalidStyleSpec`` () =
    match Style.create (Colour.Theme "   ") Colour.Default [] with
    | Error (RenderError.InvalidStyleSpec _) -> true
    | _ -> false

[<Property(MaxTest = 1)>]
let ``Style.create with upper-case theme slot returns InvalidStyleSpec`` () =
    match Style.create (Colour.Theme "Error") Colour.Default [] with
    | Error (RenderError.InvalidStyleSpec _) -> true
    | _ -> false

[<Property(MaxTest = 1)>]
let ``Decorations form a Set — duplicates collapse`` () =
    match Style.create Colour.Default Colour.Default [ Decoration.Bold; Decoration.Bold; Decoration.Italic ] with
    | Ok s -> Set.count s.Decorations = 2
    | Error _ -> false

// ---------- Style.ofMarkupHint (T013a) ----------

[<Property(MaxTest = 1)>]
let ``Style.ofMarkupHint parses 'bold' as Bold decoration`` () =
    match Style.ofMarkupHint "bold" with
    | Ok s -> Set.contains Decoration.Bold s.Decorations
    | Error _ -> false

[<Property(MaxTest = 1)>]
let ``Style.ofMarkupHint parses 'bold #ff0000' to Bold + Rgb`` () =
    match Style.ofMarkupHint "bold #ff0000" with
    | Ok s ->
        Set.contains Decoration.Bold s.Decorations
        && s.Foreground = Colour.Rgb (255uy, 0uy, 0uy)
    | Error _ -> false

[<Property(MaxTest = 1)>]
let ``Style.ofMarkupHint parses multi-decoration 'dim italic underline'`` () =
    match Style.ofMarkupHint "dim italic underline" with
    | Ok s ->
        Set.contains Decoration.Dim       s.Decorations
        && Set.contains Decoration.Italic    s.Decorations
        && Set.contains Decoration.Underline s.Decorations
    | Error _ -> false

[<Property(MaxTest = 1)>]
let ``Style.ofMarkupHint on empty string returns Ok empty`` () =
    match Style.ofMarkupHint "" with
    | Ok s -> Set.isEmpty s.Decorations
    | Error _ -> false

[<Property(MaxTest = 1)>]
let ``Style.ofMarkupHint with bad hex byte returns InvalidStyleSpec`` () =
    match Style.ofMarkupHint "bold #zzzzzz" with
    | Error (RenderError.InvalidStyleSpec _) -> true
    | _ -> false

[<Property(MaxTest = 1)>]
let ``Style.ofMarkupHint with unknown token returns InvalidStyleSpec`` () =
    match Style.ofMarkupHint "wibble" with
    | Error (RenderError.InvalidStyleSpec _) -> true
    | _ -> false

// ---------- RenderContext (T013) ----------

[<Property(MaxTest = 1)>]
let ``RenderContext.create stores given width and theme`` () =
    let ctx = RenderContext.create 120 true "nocturne"
    ctx.Width = 120
    && ctx.ColourEnabled = true
    && ctx.ThemeName = "nocturne"

[<Property(MaxTest = 1)>]
let ``RenderContext.create with null theme falls back to 'default'`` () =
    let t : string | null = null
    let ctx = RenderContext.create 80 false t
    ctx.ThemeName = "default"

[<Property(MaxTest = 1)>]
let ``RenderContext.probe returns a valid record`` () =
    let ctx = RenderContext.probe ()
    ctx.Width > 0
    && not (System.String.IsNullOrEmpty ctx.ThemeName)
