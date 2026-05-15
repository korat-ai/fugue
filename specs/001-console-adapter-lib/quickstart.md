# Quickstart — From `Spectre.Console` to `Fugue.Adapters.Console`

**Audience**: a Fugue contributor porting one or more existing rendering
call-sites from direct `Spectre.Console` use onto the new adapter.

**Coverage target**: this doc maps 100% of the rendering call patterns
observed in the existing codebase at the time the adapter ships v1
(SC-005). Patterns are listed in inventory-frequency order — start at the
top, that's where 80% of the port work lives.

> **Status**: pre-implementation draft. Sections marked **[shape-pending]**
> wait on the R2 dual-debate (see [research.md](./research.md) §R2). The
> *values* the snippets construct are stable; only the *syntax* for
> constructing them may change.

---

## 0. Setup

Add the new project reference once per Fugue.Cli call-site cluster you
plan to port:

```xml
<ItemGroup>
  <ProjectReference Include="..\Fugue.Adapters.Console\Fugue.Adapters.Console.fsproj" />
</ItemGroup>
```

Open the namespace at the top of the file being ported:

```fsharp
open Fugue.Adapters.Console
```

Then **remove** the existing Spectre opens *file by file*, only after the
last Spectre call-site in that file is ported. If a file still has one
Spectre call, keep the existing `open Spectre.Console` until that last
site is migrated.

---

## 1. Markup string → typed styled text (279 sites, top priority)

### Before

```fsharp
Markup($"[red]error:[/] {Markup.Escape err.Message}")
|> AnsiConsole.Write
```

### After (typed style — recommended path)

**[shape-pending]** — exact syntax depends on R2 outcome. Both options
produce the same `Composition` value:

```fsharp
// Candidate A (record-DU primitives):
let comp =
    Composition.Leaf (
        Primitive.Styled (
            Style.create (Colour.Theme "error") Colour.Default [Decoration.Bold]
                |> Result.defaultValue Style.empty,
            SafeText.ofUser err.Message))

// Candidate C (pipelineable builders) — equivalent value:
let comp =
    Console.styledText "error: " (Style.empty |> Style.withFg (Colour.Theme "error") |> Style.withDeco Decoration.Bold)
    |> Console.append (Console.text (SafeText.ofUser err.Message))
```

Render via the existing Surface actor:

```fsharp
Renderer.render ctx comp |> ignore   // ctx = RenderContext.probe () once per session
```

### After (markup-hint bridge — temporary, port-window only)

If migrating the typed-style call is too invasive for this PR, use the
bridge primitive — it still routes through the adapter and gets the
escape-on-user-input guarantee:

```fsharp
let comp =
    Composition.Leaf (
        Primitive.Markup $"[red]error:[/] {SafeText.ofUser err.Message |> SafeText.unwrap}")
```

The bridge primitive is a **code smell** — flag it in PR review and open
a follow-up to typed-style migration.

---

## 2. `Markup.Escape` → `SafeText.ofUser` (100+ sites)

### Before

```fsharp
Markup($"[cyan]@{Markup.Escape filename}[/]")
```

### After

```fsharp
let safe = SafeText.ofUser filename
// safe is escape-tracked; you cannot accidentally double-escape it
let comp =
    Composition.Leaf (Primitive.Styled (cyanStyle, safe))
```

**Why this matters**: in the current code, missing a `Markup.Escape` is a
latent injection vector — a filename containing `[red]rm -rf[/]` would
render as styled text. With `SafeText`, forgetting to escape is a *type
error*: `Primitive.Styled` takes `SafeText`, not `string`.

---

## 3. `Text` (plain renderable) → `Styled` with `Style.empty` (63 sites)

### Before

```fsharp
Text($"> {text} [{turn}]") :> IRenderable
```

### After

```fsharp
Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofUser $"> {text} [{turn}]"))
```

If the call-site was deliberately bypassing Spectre's markup parser (to
render literal `[` and `]` without escaping ceremony), `SafeText.ofUser`
gives the same behaviour for free — `[` and `]` in user input become
`[[` / `]]` after `ofUser`.

---

## 4. `Rows` → `Stack` (37 sites)

### Before

```fsharp
Rows [|
    Markup "[green]+ line1[/]"
    Markup "[red]- line2[/]"
    Markup "[yellow]@@ hunk[/]"
|]
```

### After

```fsharp
Composition.Stack [
    Composition.Leaf (Primitive.Styled (Style.empty |> Style.withFg greenSlot,  SafeText.ofLiteral "+ line1"))
    Composition.Leaf (Primitive.Styled (Style.empty |> Style.withFg redSlot,    SafeText.ofLiteral "- line2"))
    Composition.Leaf (Primitive.Styled (Style.empty |> Style.withFg yellowSlot, SafeText.ofLiteral "@@ hunk"))
]
```

Where `greenSlot = Colour.Theme "diff-add"` etc — see "Theme slots", §10.

---

## 5. `Padder().PadLeft(n).PadRight(m)` → `Composition.padded` (12 sites)

### Before

```fsharp
Padder(Markup("[dim]" + Markup.Escape s + "[/]")).PadLeft(2).PadRight(2)
```

### After

```fsharp
Composition.padded 2 2 0 0 (
    Composition.Leaf (Primitive.Styled (dimStyle, SafeText.ofUser s)))
|> Result.defaultValue (Composition.Leaf (Primitive.Markup ""))  // degenerate case
```

`padded` returns `Result` because negative padding is rejected at build
time (see [data-model.md](./data-model.md) §5).

---

## 6. `Panel` → `Composition.panel` (5 sites — bubble mode only)

### Before

```fsharp
Panel(inner,
      Border = BoxBorder.Rounded,
      Header = PanelHeader(" Claude ")) :> IRenderable
```

### After

```fsharp
Composition.panel
    Border.Rounded
    (Some (SafeText.ofLiteral " Claude "))
    inner
```

---

## 7. `Align(inner, HorizontalAlignment.Right)` → `Composition.aligned` (13 sites)

### Before

```fsharp
Align(bubble, HorizontalAlignment.Right) :> IRenderable
```

### After

```fsharp
Composition.aligned Alignment.RightAlign bubble
```

---

## 8. `Table` (markdown) → `Composition.table` (19 sites — `MarkdownRender.fs` only)

### Before

```fsharp
let t = Table()
t.AddColumn(TableColumn("Name").Width(20)) |> ignore
t.AddColumn(TableColumn("Value")) |> ignore
for k, v in pairs do
    t.AddRow(Markup k, Markup v) |> ignore
t :> IRenderable
```

### After

```fsharp
let headers = [
    Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "Name"))
    Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "Value"))
]
let rows =
    pairs
    |> List.map (fun (k, v) -> [
        Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofUser k))
        Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofUser v))
    ])
Composition.table headers rows
```

`table` returns `Result` — ragged rows (rows with a different column
count than headers) are rejected at build time.

---

## 9. `Rule()` → `Primitive.Rule` (1 site — markdown thematic break)

### Before

```fsharp
Rule() :> IRenderable
```

### After

```fsharp
Composition.Leaf (Primitive.Rule Style.empty)
```

---

## 10. Theme slots (replaces `Color.*` and most hex literals)

### Before

```fsharp
let dimEsc = if state.ActiveTheme = "nocturne" then "\x1b[38;5;24m" else "\x1b[90m"
// ... used inline in markup strings
```

### After

```fsharp
// In Fugue.Adapters.Console there's no equivalent constant.
// Instead, the renderer resolves Colour.Theme "dim-accent" against
// the active theme name in RenderContext at render time.
let style = Style.empty |> Style.withFg (Colour.Theme "dim-accent")
```

Theme slots SHOULD be named for *purpose* (`error`, `warning`, `diff-add`,
`dim-accent`), not for *appearance* (`red`, `grey`). The canonical v1 slot
list and the per-theme colour mapping live in
[`data-model.md` §2 "Canonical theme slots (v1)"](./data-model.md). When
porting a call-site, look up the right slot there — do not invent new
slot names locally. (Adding a slot is fine; it's a v-MINOR change and a
data-model.md edit, not a per-call-site invention.)

---

## 11. The `Surface.fs::spectre` helper → `Renderer.render`

### Before

```fsharp
Surface.spectre (fun ac -> ac.MarkupLine "[bold]hello[/]")
```

### After

```fsharp
let comp =
    Composition.Stack [
        Composition.Leaf (Primitive.Styled (Style.empty |> Style.withDeco Decoration.Bold,
                                            SafeText.ofLiteral "hello"))
        Composition.Leaf Primitive.LineBreak
    ]
Renderer.render ctx comp |> ignore
```

`Renderer.render` IS the new `spectre` — it owns the ephemeral
`AnsiConsole` instance, the `StringWriter`, and the post-to-actor call.
The `Surface.fs::spectre` function disappears in the final port wave.

---

## 12. Per-file port-window checklist

A file is considered "ported" when:

- [ ] All `open Spectre.Console*` lines are removed from that file.
- [ ] `Markup.Escape` is removed from that file (replaced by `SafeText.ofUser`).
- [ ] No use of `Primitive.Markup` bridge remains *unless* a follow-up
      issue is linked in a comment.
- [ ] The file's existing test (if any) still passes.
- [ ] The Verify snapshots in `tests/Fugue.Adapters.Console.Tests/VisualParity/`
      that cover this file's primitives are green.

The contributor SHOULD record the port in this doc's appendix (see
`Appendix A` below — empty until first port lands).

---

## Appendix A — Port log (filled by port PRs, not by adapter v1)

| Date | Source file | Sites ported | Bridges remaining | PR |
|------|-------------|-------------:|------------------:|----|
| _(empty until first port)_ | | | | |
