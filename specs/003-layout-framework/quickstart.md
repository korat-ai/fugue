# Quickstart — Phase 3 Layout Framework

**Audience**: a Fugue contributor building or porting TUI layouts using the
Phase 3 primitives (`Stack`, `Dock`, `LayoutGrid`, `Flex`, `LayoutScheduler`).

**Coverage target**: this document maps the call pattern each Phase 3 user
story unblocks. Each section shows the F# code a contributor would write,
the conceptual rendered output, and — where a Phase 2 predecessor exists —
a brief "before vs. after" comparison.

> **Status**: Phase 1 design (`003-layout-framework`).
> Implementation pending — Phase 3 will land as five PRs (one per
> user story), starting with **P1 Stack** (it proves the lowering
> architecture end-to-end on the smallest surface).

---

## 0. Setup

No new NuGet packages in Phase 3. The layout modules live in the existing
`Fugue.Adapters.Console.fsproj` under the `Layout/` sub-folder. Add the
project reference once (it is already present if the file already depends
on Phase 1/2 adapter):

```xml
<ItemGroup>
  <ProjectReference Include="..\Fugue.Adapters.Console\Fugue.Adapters.Console.fsproj" />
</ItemGroup>
```

Open both namespaces at the top of any file that uses Phase 3 layout:

```fsharp
open Fugue.Adapters.Console         // Phase 1/2: Composition, RenderContext, RenderError, SafeText
open Fugue.Adapters.Console.Layout  // Phase 3: Stack, Dock, LayoutGrid, Flex, Scheduler.*
```

Build a `RenderContext` with the new `height` parameter (Phase 3 extends
`RenderContext.create` — see `data-model.md §1`):

```fsharp
// For tests: explicit dimensions.
let ctx = RenderContext.create 80 24 true null

// For production: probe the live terminal.
let ctx = RenderContext.probe ()
```

---

## 1. Hello Stack: vertical flow of three panels

**User Story 1, acceptance scenario 1** — a vertical stack of three
compositions with gap = 1 and left alignment.

### Code

```fsharp
// Three child compositions (could be panels, markup, rules, etc.)
let panel1 = Composition.panel Rounded (Some (SafeText.ofLiteral " Turn 1 ")) (Composition.Leaf (Primitive.text "First assistant turn"))
let panel2 = Composition.panel Rounded (Some (SafeText.ofLiteral " Turn 2 ")) (Composition.Leaf (Primitive.text "Second assistant turn"))
let panel3 = Composition.panel Rounded (Some (SafeText.ofLiteral " Turn 3 ")) (Composition.Leaf (Primitive.text "Third assistant turn"))

// Build the Stack layout: vertical, 1-row gap between children, left-aligned.
let layout =
    Stack.create Vertical 1 Start [panel1; panel2; panel3]

// Lower to Composition and render to ANSI.
match layout with
| Error e  -> printfn $"Layout error: {e}"
| Ok stack ->
    let composition = Composition.ofStack stack
    match Renderer.toRawAnsi ctx composition with
    | Error e     -> printfn $"Render error: {e}"
    | Ok ansiText -> printf "%s" ansiText
```

### Conceptual rendered output (width = 80)

```
╭ Turn 1 ──────────────────────────────────────────────────────────────────────╮
│ First assistant turn                                                          │
╰───────────────────────────────────────────────────────────────────────────────╯
                                                                                (blank row — gap = 1)
╭ Turn 2 ──────────────────────────────────────────────────────────────────────╮
│ Second assistant turn                                                         │
╰───────────────────────────────────────────────────────────────────────────────╯
                                                                                (blank row — gap = 1)
╭ Turn 3 ──────────────────────────────────────────────────────────────────────╮
│ Third assistant turn                                                          │
╰───────────────────────────────────────────────────────────────────────────────╯
```

### Before (without Stack)

Without Phase 3, a contributor rendering three successive panels in Fugue must
manually write each to the terminal in sequence:

```fsharp
// Before — manual sequential write, no gap, no alignment control
Surface.writeRenderable ctx panel1
Surface.writeRenderable ctx panel2
Surface.writeRenderable ctx panel3
```

There is no gap, no alignment, and no control over the cross-axis (panels
would expand to terminal width by Spectre default regardless of intent).
With `Stack`, all three concerns are explicit and validated.

---

## 2. Status bar: Dock pin

**User Story 2, acceptance scenario 1** — a one-line status composition
pinned to the bottom, with the main scrollable area filling the rest.

### Code

```fsharp
let statusComp =
    Composition.Leaf (Primitive.text "[ fugue v0.5.1 ] [ claude-sonnet-4-6 ] [ 8s ]")

let mainComp =
    // The streaming output area — could be a Stack of assistant turns.
    Composition.panel Rounded None (Composition.Leaf (Primitive.text "Streaming output…"))

// Dock: status pinned to bottom, main fills everything above.
let layout =
    Dock.create [
        DockEdge.Bottom, statusComp
        DockEdge.Fill,   mainComp
    ]

match layout with
| Error e -> printfn $"Dock error: {e}"
| Ok dock ->
    let composition = Composition.ofDock dock
    match Renderer.toRawAnsi ctx composition with
    | Error e     -> printfn $"Render error: {e}"
    | Ok ansiText -> printf "%s" ansiText
```

### Conceptual rendered output (width = 80, height = 24)

```
╭────────────────────────────────────────────────────────────────────────────╮  ← row 0
│ Streaming output…                                                          │
│                                                                            │
│                                      (rows 0–22 = Fill = mainComp)        │
│                                                                            │
╰────────────────────────────────────────────────────────────────────────────╯  ← row 22
[ fugue v0.5.1 ] [ claude-sonnet-4-6 ] [ 8s ]                                  ← row 23 = Bottom
```

### Before (without Dock)

The current status bar in `src/Fugue.Cli/StatusBar.fs` is positioned via
direct `DrawOp.MoveTo (Region.StatusBarLine1, 0)` + `DrawOp.Paint` sequences
emitted to the `RealExecutor` actor. The "main area rows 0..22" is implicitly
whatever Spectre renders before the cursor reaches the status row. There is no
explicit declarative contract between "how tall is my main area?" and "where
is my status bar?". Dock makes this contract explicit and checked at
construction time.

---

## 3. Two-column diff view: LayoutGrid

**User Story 3, acceptance scenario 2** — a 2-column, 2-row grid with
`Star 1 | Star 2` column sizing and equal row heights.

### Code

```fsharp
// Four cells: a/b in row 0, c/d in row 1.
let cellA = Composition.Leaf (Primitive.text "Left col / Row 0")
let cellB = Composition.Leaf (Primitive.text "Right col / Row 0 — wider")
let cellC = Composition.Leaf (Primitive.text "Left col / Row 1")
let cellD = Composition.Leaf (Primitive.text "Right col / Row 1 — wider")

// Columns: 1:2 ratio (left gets 1/3 of total width, right gets 2/3).
// Rows: equal star sizing (each row gets half the available height).
let layout =
    LayoutGrid.create
        [Star 1; Star 2]       // columns: ratio 1 : 2
        [Star 1; Star 1]       // rows: equal halves
        [[cellA; cellB]; [cellC; cellD]]

match layout with
| Error e    -> printfn $"Grid error: {e}"
| Ok lgrid   ->
    let composition = Composition.ofLayoutGrid lgrid
    match Renderer.toRawAnsi ctx composition with
    | Error e     -> printfn $"Render error: {e}"
    | Ok ansiText -> printf "%s" ansiText
```

### Conceptual rendered output (width = 90, height = 24)

```
│ Left col / Row 0        │ Right col / Row 0 — wider                         │ ← rows 0–11
│                         │                                                   │
│                         │ (col 0 = 30 cells; col 1 = 60 cells)              │
│─────────────────────────┼────────────────────────────────────────────────── │
│ Left col / Row 1        │ Right col / Row 1 — wider                         │ ← rows 12–23
│                         │                                                   │
│                         │ (each row = 12 rows tall)                         │
```

### Why LayoutGrid, not nested Stacks?

A horizontal Stack with two children yields a left-to-right pair but cannot
express `Auto` sizing (where the left column sizes to its widest label) or
`Star` sizing where multiple rows must have the same column width. A nested
Stack-of-Stacks solution would require the contributor to pre-compute
widths manually. `LayoutGrid` makes the sizing model declarative.

---

## 4. Input prompt with flexible middle: Flex

**User Story 4, acceptance scenario 1** — a horizontal Flex with a fixed
label on the left, a growing input box in the middle, and a fixed button on
the right.

### Code

```fsharp
let label =
    Composition.Leaf (Primitive.text "Prompt:")

let input =
    // In real use, this would be the ReadLine input composition.
    Composition.Leaf (Primitive.text "│ type here… ")

let submitBtn =
    Composition.Leaf (Primitive.text "[Enter]")

// FlexItems: fixed 10-cell label | grow-1 input | fixed 8-cell button.
let layoutResult = result {
    let! labelItem  = Flex.fixed_ 10 label
    let! inputItem  = Flex.grow 1.0 input
    let! submitItem = Flex.fixed_ 8 submitBtn
    return! Flex.create Horizontal [labelItem; inputItem; submitItem]
}

match layoutResult with
| Error e    -> printfn $"Flex error: {e}"
| Ok flex    ->
    let composition = Composition.ofFlex flex
    match Renderer.toRawAnsi ctx composition with
    | Error e     -> printfn $"Render error: {e}"
    | Ok ansiText -> printf "%s" ansiText
```

### Conceptual rendered output (width = 80)

```
Prompt:   │ type here…                                                [Enter]
←10 cols→ ←─────────────────── 62 cols (80 - 10 - 8) ──────────────→←8 cols→
```

### Grow ratio distribution (three grow-1 children at width = 90)

```fsharp
let a = Composition.Leaf (Primitive.text "Alpha")
let b = Composition.Leaf (Primitive.text "Beta")
let c = Composition.Leaf (Primitive.text "Gamma")

let layout = result {
    let! ia = Flex.grow 1.0 a
    let! ib = Flex.grow 1.0 b
    let! ic = Flex.grow 1.0 c
    return! Flex.create Horizontal [ia; ib; ic]
}
// → each child gets 30 columns (90 / 3 = 30, exact split)
```

---

## 5. REPL port preview (the integration story)

**User Story 5** — how `Repl.fs` would wire the Layout framework end-to-end.

This section is **pseudocode / preview**, not the final implementation. The
P5 PR will be the authoritative source. The intent is to show the
`LayoutScheduler` wiring and how `LayoutHost.mount` bridges Layout to Surface.

### Conceptual REPL layout tree

```
TopLevel (Dock)
├── DockEdge.Bottom  →  statusBar (one-line Composition)
├── DockEdge.Bottom  →  inputRegion (three-line Composition: divider + prompt + hint)
└── DockEdge.Fill    →  streamingArea (Stack of assistant turns, grows upward)
```

### Pseudocode: mounting a layout in Repl.fs

```fsharp
// src/Fugue.Cli/LayoutHost.fs (new in Phase 3, ~50 LOC)
// mounts a layout-producer into the Surface actor for a given region.
// Returns a handle that the REPL calls on each state change.

// Inside Repl.fs: session-init path (called once on REPL start).
let buildTopLevel (state: ReplState) : Result<Composition, RenderError> =
    result {
        // Status bar — always one line.
        let statusText = StatusBar.render state.ElapsedMs state.ModelName state.ToolCount
        let status = Composition.Leaf (Primitive.text statusText)

        // Input region — three rows: rule + prompt + hint.
        let inputDivider = Composition.Leaf (Primitive.text (String.replicate ctx.Width "─"))
        let inputPrompt  = Composition.Leaf (Primitive.text $"> {state.InputBuffer}")
        let inputHint    = Composition.Leaf (Primitive.text "(ctrl+c to cancel)")
        let! inputStack  = Stack.create Vertical 0 Start [inputDivider; inputPrompt; inputHint]
        let inputRegion  = Composition.ofStack inputStack

        // Streaming area — stack of all assistant turns so far.
        let turnComps = state.Turns |> List.map renderTurn
        let! turnStack = Stack.create Vertical 1 Start (if turnComps.IsEmpty then [Composition.Leaf (Primitive.text "")] else turnComps)
        let streamingArea = Composition.ofStack turnStack

        // Top-level dock.
        let! dock = Dock.create [
            DockEdge.Bottom, status
            DockEdge.Bottom, inputRegion
            DockEdge.Fill,   streamingArea
        ]
        return Composition.ofDock dock
    }

// Mount the scheduler: one per REPL session.
// `stateRef` is a reference cell updated by the streaming + input handlers.
let mountReplLayout (stateRef: ReplState ref) (surface: ISurface) =
    let producer () = buildTopLevel !stateRef
    let onFrame (comp: Composition) =
        match Renderer.toRawAnsi (RenderContext.probe ()) comp with
        | Ok ansi -> surface.Post (DrawOp.RawAnsi ansi)
        | Error e -> surface.Post (DrawOp.RawAnsi $"[layout error: {e}]")
    Scheduler.create producer 16 onFrame

// On each LLM token arrival: update state + trigger scheduler.
let onTokenAppended (sched: LayoutScheduler) (stateRef: ReplState ref) (token: string) =
    stateRef := { !stateRef with Turns = appendToken token !stateRef.Turns }
    Scheduler.trigger sched   // coalesced — at most one render per 16ms

// On SIGWINCH: RenderContext.probe() is called fresh inside producer, so
// the next scheduler tick automatically uses the new dimensions.
// No explicit resize handling needed at the layout level.
```

### What changes in Repl.fs after the P5 port

| Before (current) | After (Phase 3) |
|------------------|-----------------|
| `AnsiConsole.Write(MarkdownRender.toRenderable text)` | `Scheduler.trigger schedHandle` |
| `Surface.writeLine ctx text` (for status bar) | `stateRef := { !stateRef with StatusText = text }; Scheduler.trigger schedHandle` |
| `open Spectre.Console` at line 12 | removed |
| Direct cursor manipulation via `DrawOp.MoveTo` | handled inside `LayoutHost.onFrame` → Surface actor |

After the port: `rg "AnsiConsole" src/Fugue.Cli/Repl.fs` returns zero matches
(SC-002 for Repl.fs satisfied). `rg "Spectre.Console" src/Fugue.Cli/Repl.fs`
returns zero matches (FR-011).

---

## 6. Migration checklist for existing call-sites

A contributor porting an existing `Fugue.Cli` or `Fugue.Surface` call-site
to Phase 3 layout follows this pattern:

### Before / after patterns

| Before | After |
|--------|-------|
| `AnsiConsole.Write(someRenderable)` | Build a `Composition` via Phase 1/2 bridge, lower via Phase 3 layout, render via `Renderer.toRawAnsi`, post `DrawOp.RawAnsi` to actor |
| `AnsiConsole.MarkupLine(str)` | `SafeText.ofUser str` → `Composition.Leaf (Primitive.markup …)` → into a `Stack` → `Composition.ofStack` → `Renderer.toRawAnsi` |
| Manual side-by-side layout (`Console.SetCursorPosition`) | Replace with `Stack.create Horizontal gap align children` |
| Manual header + content + footer positioning | Replace with `Dock.create [Top, header; Bottom, footer; Fill, content]` |
| `Composition.Columns [(0.5, left); (0.5, right)]` | Replace with `LayoutGrid.create [Star 1; Star 1] [Auto] [[left; right]]` when row sizing also matters, or keep `Composition.Columns` for simple ratio-only splits |
| `DrawOp.RawAnsi (Renderer.toRawAnsi ctx comp)` in a hot streaming loop | Wrap in `Scheduler.create producer 16 onFrame` to coalesce at 60 Hz |

### Step-by-step port protocol

1. **Identify the region**: which logical area of the terminal does this call-site own?
   Map it to a `DockEdge` or layout role (Fill, Bottom, Left, etc.).

2. **Build the child `Composition` values**: use Phase 1/2 primitives
   (`SafeText`, `Primitive`, `Composition.panel`, `Composition.Leaf`, etc.)
   for each leaf. Keep user content going through `SafeText.ofUser`.

3. **Choose the layout primitive**:
   - Sequential vertical flow → `Stack.create Vertical`
   - Sequential horizontal flow → `Stack.create Horizontal`
   - Status bar / pinned chrome → `Dock.create`
   - Label + input + button row → `Flex.create Horizontal`
   - 2D table or diff pane → `LayoutGrid.create`

4. **Lower and render**:
   ```fsharp
   let comp = Composition.ofStack stack  // or ofDock / ofLayoutGrid / ofFlex
   let! ansi = Renderer.toRawAnsi ctx comp
   surface.Post (DrawOp.RawAnsi ansi)
   ```

5. **If streaming**: wrap in a `LayoutScheduler`. Update state, call
   `Scheduler.trigger`. Do NOT call `Renderer.toRawAnsi` directly on every
   token — the scheduler coalesces to 60 Hz automatically.

6. **Remove the `open Spectre.Console` line** from the file once all
   call-sites are ported. `rg "open Spectre.Console" <file>` returning
   zero matches is the done criterion per SC-002 / FR-011.

7. **Run the Phase 3 guard checks**:
   ```bash
   rg 'Spectre\.' src/Fugue.Cli/<portedFile>.fs   # must return 0
   rg 'AnsiConsole' src/Fugue.Cli/<portedFile>.fs # must return 0
   dotnet test                                     # all tests green
   dotnet publish src/Fugue.Cli.Aot -c Release -r osx-arm64 # AOT stays clean
   ```
