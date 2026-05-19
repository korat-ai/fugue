# Surface.fs Spectre.Console call-site inventory

Captured 2026-05-19 on branch 005-cli-layout-migration, pre-migration state.

The `rg 'Spectre\.Console|AnsiConsole'` command matches 13 lines in Surface.fs
(lines 5, 6, 66, 68, 71, 73, 74, 75, 85, 96, 97, 102, 109). Six are code lines;
seven are comment lines. All 13 must become zero for the G3 progress invariant.

| # | Line | Function / context | Spectre API used | Rendering purpose | Target adapter primitive | Status |
|---|------|-------------------|------------------|-------------------|--------------------------|--------|
| 1 | 5 | module-level open | `open Spectre.Console` | Brings `AnsiConsoleSettings`, `AnsiConsoleOutput`, `AnsiConsole`, `Markup`, `TableBorder`, `TableColumn`, `Table`, `IAnsiConsole` into scope | Remove; use `open Fugue.Adapters.Console` instead | migrated |
| 2 | 6 | module-level open | `open Spectre.Console.Rendering` | Brings `IRenderable` into scope for `writeRenderable`/`writeRenderableLine` params | Remove; fully-qualify `Spectre.Console.Rendering.IRenderable` in param types | migrated |
| 3 | 66 | `spectre` (doc comment) | `Spectre.AnsiConsole` (comment) | Documents internal rendering strategy | Update comment to describe adapter-based rendering | migrated |
| 4 | 68 | `spectre` (doc comment) | `AnsiConsole` (comment) | Same | Update comment | migrated |
| 5 | 71 | `spectre` (function) | `IAnsiConsole` type param | Internal helper: takes `IAnsiConsole -> unit` action, renders to StringWriter, posts ANSI | Replace function body: build Composition via `Renderable.fromSpectre`/`Composition.ofRenderable`, render via `Renderer.toRawAnsi (RenderContext.probe ())` | migrated |
| 6 | 73 | `spectre` body | `AnsiConsoleSettings()` | Ephemeral Spectre console construction | Eliminated with spectre body replacement | migrated |
| 7 | 74 | `spectre` body | `AnsiConsoleOutput(sw)` | Routes Spectre output to StringWriter | Eliminated with spectre body replacement | migrated |
| 8 | 75 | `spectre` body | `AnsiConsole.Create settings` | Creates ephemeral console instance | Eliminated with spectre body replacement | migrated |
| 9 | 85 | `writeRenderableLine` (doc comment) | `AnsiConsole.Write + WriteLine` (comment) | Documents API being wrapped | Update comment to describe adapter flow | migrated |
| 10 | 96 | `escape` (doc comment) | `Spectre.Console.Markup.Escape` (comment) | Documents delegation to Spectre's escape | Update: now delegates to `SafeText.ofUser` | migrated |
| 11 | 97 | `escape` (doc comment) | `open Spectre.Console` (FR-011 comment) | FR-011 gate explanation | Update: now "FR-011 gate via SafeText.ofUser" | migrated |
| 12 | 102 | `markup` (doc comment) | `open Spectre.Console` (FR-011 comment) | FR-011 gate explanation | Update comment | migrated |
| 13 | 109 | `roundedTable` (doc comment) | `open Spectre.Console` (FR-011 comment) | FR-011 gate explanation | Update comment | migrated |

## Migration strategy per function

- **`spectre`** (private): Removed. Callers converted directly to adapter calls.
- **`markupLine`**: `Primitive.Markup` → `Renderer.toRawAnsi (RenderContext.probe ())` → `Surface.write`
- **`writeRenderableLine`**: `Renderable.fromSpectre r |> Composition.ofRenderable` → render → `write` + `DrawOp.LineBreak`
- **`writeRenderable`**: Same without extra line break
- **`escape`**: `SafeText.ofUser s |> SafeText.unwrap`
- **`markup`**: `Primitive.Markup markupStr` → render → `write`
- **`roundedTable`**: Build `Spectre.Console.Table` internally, wrap via `Renderable.fromSpectre`, render, write

## T018 — LayoutHost wiring decision

`LayoutHost.mount` is referenced in a Repl.fs comment (line 24) as future intent
but not yet called. The dispatch brief asks to wire it as the "production canonical
mount point." Analysis shows:

- `LayoutHost.mount` creates a `LayoutScheduler` driven by a producer closure.
- The scheduler fires on each `Scheduler.trigger` call and renders a `Composition`
  to the Surface actor.
- The current REPL does NOT have a streaming Composition producer — all rendering
  goes through imperative `Surface.write*` calls.
- Wiring `LayoutHost.mount` requires a `producer: unit -> Result<Composition, RenderError>`
  that represents the current render state. This producer is the streaming markdown
  region — not yet migrated (that is PR P3: MarkdownRender.fs + StreamRender.fs).

**Ambiguity**: The dispatch brief says "wire `LayoutHost.mount producer 16` as the
production canonical mount point that feeds the migrated Surface paths." But no
producer closure over mutable state exists yet (streaming is P3). Wiring a stub
no-op producer would add dead machinery with no real effect. The `.fsi` signature
notes "Callers: 1. Repl.fs — mounts one scheduler per REPL session."

**Decision required from user (CEO escalation)**: See return summary.

## T018 decision (2026-05-19)

**Chosen**: Option (b) — defer to P3 (T043).

Rationale: T018 and T043 in tasks.md describe the same `Repl.fs` line. `LayoutHost.mount` requires a producer callback that only meaningfully exists once streaming markdown is migrated. A no-op stub (Option a) adds dead machinery for ≥ 6 weeks. Out-of-scope (Option c) is what (b) effectively means but said more clearly.

Confirmed by CEO via /speckit-implement reviewer escalation.
