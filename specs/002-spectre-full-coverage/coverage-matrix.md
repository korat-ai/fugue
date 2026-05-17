# Coverage Matrix: Spectre.Console 0.49.x

**Generated**: 2026-05-16 (Phase 2 baseline)
**Source**: Spectre.Console 0.49.1, Spectre.Console.Testing 0.49.1
**Enumeration method**: `System.Reflection.Assembly.GetExportedTypes()` against
`~/.nuget/packages/spectre.console/0.49.1/lib/net8.0/Spectre.Console.dll` and
`~/.nuget/packages/spectre.console.testing/0.49.1/lib/net8.0/Spectre.Console.Testing.dll`.
Compiler-generated types (names starting with `<`) and nested types (reported as separate
export entries: `Emoji+Known`, `Spinner+Known`) are excluded from the count — they are
classified under their parent's row.

## How to read this matrix

- **covered**: an adapter wrapper exists or is planned in the listed phase. FR-019 enforces
  that a render-evidence test passes for each covered type.
- **excluded**: deliberately out of scope, with a one-line justification. No adapter wrapper
  will be produced.
- **pending**: classification not yet decided. MUST be empty at Phase 2 P5 PR merge (SC-001).

## Summary

| Status | Count |
|---|---|
| covered | 60 |
| excluded | 178 |
| pending | 0 |
| **Total public types** | **238** |

Breakdown by assembly:
- `Spectre.Console.dll`: 214 exported types → 55 covered, 159 excluded, 0 pending
- `Spectre.Console.Testing.dll`: 14 exported types → 4 covered, 10 excluded, 0 pending
- `Spectre.Console.Json.dll`: 10 exported types → 1 covered, 9 excluded, 0 pending (Phase 2 P2 new dependency — see data-model.md §0.2; initial matrix counted only the public-facing `JsonText`; T079 Polish audit adds 9 internal AST types as excluded)

---

## Spectre.Console namespace (163 types)

| Type | Kind | Status | Phase / Justification |
|---|---|---|---|
| `Align` | sealed class | covered | Phase 1 — `Composition.Aligned` DU case |
| `AlignableExtensions` | static class | excluded | C# fluent-extension; F# adapter uses direct constructors, not extension chaining |
| `AnsiConsole` | static class | covered | Phase 2 (P5) — `Console.ansiConsole` factory / `Console.create` wrapper |
| `AnsiConsoleExtensions` | static class | excluded | C# fluent-extension; adapter exposes typed F# functions instead |
| `AnsiConsoleFactory` | sealed class | excluded | `[Obsolete]` — "Consider using AnsiConsole.Create instead."; replaced by `AnsiConsole.Create` which Phase 2 (P5) wraps |
| `AnsiConsoleOutput` | sealed class | covered | Phase 1 — used internally by `Renderer.withSpectre`; not in public `.fsi` but consumed by the adapter |
| `AnsiConsoleSettings` | sealed class | covered | Phase 1 — used internally by `Renderer.withSpectre`; not in public `.fsi` but consumed by the adapter |
| `AnsiSupport` | enum | excluded | Internal terminal-capability enum; superseded by `RenderContext.colourEnabled` |
| `BarChart` | sealed class | covered | Phase 2 (P2) — `DataDisplays.BarChart` smart constructor |
| `BarChartExtensions` | static class | excluded | C# fluent-extension; F# adapter uses smart-constructor functions |
| `BarChartItem` | sealed class | covered | Phase 2 (P2) — `DataDisplays.BarChartItem` record |
| `BoxBorder` | abstract class | covered | Phase 1 — `Border` DU maps to `BoxBorder.None/Square/Rounded/Heavy`; internal to renderer |
| `BreakdownChart` | sealed class | covered | Phase 2 (P2) — `DataDisplays.BreakdownChart` smart constructor |
| `BreakdownChartExtensions` | static class | excluded | C# fluent-extension |
| `BreakdownChartItem` | sealed class | covered | Phase 2 (P2) — `DataDisplays.BreakdownChartItem` record |
| `Calendar` | sealed class | covered | Phase 2 (P2) — `DataDisplays.Calendar` smart constructor |
| `CalendarEvent` | sealed class | covered | Phase 2 (P2) — `DataDisplays.CalendarEvent` record |
| `CalendarExtensions` | static class | excluded | C# fluent-extension |
| `Canvas` | sealed class | excluded | Pixel-level bitmap canvas; no Fugue use-case identified; flag for future if needed |
| `Capabilities` | sealed class | excluded | Internal terminal-capability bag; exposed via `IReadOnlyCapabilities`; not needed in adapter public surface |
| `CharExtensions` | static class | excluded | Low-level Unicode cell-width helper (`GetCellWidth`); not part of rendering abstraction |
| `CircularTreeException` | sealed class | excluded | Exception thrown by Spectre when a `Tree` node is added circularly; adapter prevents this at smart-constructor time (returns `Result.Error InvalidArgument`) so the exception is never observed |
| `Color` | struct | covered | Phase 1 — `Colour` DU (`Default` / `Rgb` / `Theme`) |
| `ColorSystem` | enum | excluded | Internal terminal colour-depth enum; superseded by `RenderContext.colourEnabled` |
| `ColorSystemSupport` | enum | covered | Phase 1 — used internally in `Renderer.withSpectre` to map `colourEnabled`; not in public `.fsi` |
| `ColumnExtensions` | static class | excluded | C# fluent-extension |
| `Columns` | sealed class | covered | Phase 1 — `Composition.Columns` DU case |
| `ConfirmationPrompt` | sealed class | covered | Phase 2 (P4) — `Prompts.confirmationPrompt` wrapper |
| `ConfirmationPromptExtensions` | static class | excluded | C# fluent-extension |
| `ControlCode` | sealed class | excluded | Raw ANSI escape-sequence factory for cursor control; too low-level for the adapter abstraction; `Primitive.RawAnsi` is the escape-passthrough bridge |
| `CursorDirection` | enum | excluded | Used only with `IAnsiConsoleCursor`; cursor management is below the adapter's abstraction layer |
| `CursorExtensions` | static class | excluded | C# fluent cursor-control extension; out of scope (cursor management deferred) |
| `Decoration` | enum | covered | Phase 1 — `Decoration` DU (`Bold`/`Italic`/`Dim`/`Underline`/`Strikethrough`) |
| `DownloadedColumn` | sealed class | excluded | Progress column showing download byte-count; covered indirectly through `ProgressColumn` (Phase 2 P3); concrete column subclasses are pass-through in the adapter |
| `ElapsedTimeColumn` | sealed class | excluded | Progress column subclass — same rationale as `DownloadedColumn` |
| `Emoji` | static class | excluded | Static lookup table of ~3500 emoji Unicode constants; no adapter wrapping needed — callers embed emoji strings directly in `SafeText.ofLiteral` |
| `ExceptionExtensions` | static class | excluded | C# fluent-extension on `Exception`; adapter wraps exception rendering via `Console.renderException` (Phase 2 P5), not this extension class |
| `ExceptionFormats` | enum | covered | Phase 2 (P5) — `Console.ExceptionFormat` F# DU (maps `Default`/`ShortenTypes`/`ShortenMethods`/`ShortenPaths`/`ShowLinks`) |
| `ExceptionSettings` | sealed class | covered | Phase 2 (P5) — `Console.ExceptionSettings` record (colours, format flags) |
| `ExceptionStyle` | sealed class | covered | Phase 2 (P5) — part of `Console.ExceptionSettings` wrapper |
| `ExpandableExtensions` | static class | excluded | C# fluent-extension |
| `FigletFont` | sealed class | covered | Phase 2 (P2) — `DataDisplays.FigletFont` wrapper (load from file / stream) |
| `FigletText` | sealed class | covered | Phase 2 (P2) — `DataDisplays.FigletText` smart constructor |
| `FigletTextExtensions` | static class | excluded | C# fluent-extension |
| `Grid` | sealed class | covered | Phase 2 (P2) — `DataDisplays.Grid` smart constructor (validates non-ragged rows) |
| `GridColumn` | sealed class | covered | Phase 2 (P2) — `DataDisplays.GridColumn` record |
| `GridExtensions` | static class | excluded | C# fluent-extension |
| `GridRow` | sealed class | covered | Phase 2 (P2) — internal to `DataDisplays.Grid`; not exposed separately in `.fsi` |
| `HasBorderExtensions` | static class | excluded | C# fluent-extension |
| `HasBoxBorderExtensions` | static class | excluded | C# fluent-extension |
| `HasCultureExtensions` | static class | excluded | C# fluent-extension |
| `HasJustificationExtensions` | static class | excluded | C# fluent-extension |
| `HasTableBorderExtensions` | static class | excluded | C# fluent-extension |
| `HasTreeNodeExtensions` | static class | excluded | C# fluent-extension |
| `HorizontalAlignment` | enum | covered | Phase 1 — `Alignment` DU (`LeftAlign`/`CentreAlign`/`RightAlign`) |
| `IAlignable` | interface | excluded | Internal Spectre trait interface; not part of the adapter's public surface |
| `IAnsiConsole` | interface | covered | Phase 2 (P5) — `Console.IConsole` F# interface (or opaque wrapper) for direct-write scenarios; consumed internally in Phase 1 |
| `IAnsiConsoleCursor` | interface | excluded | Cursor-management interface; cursor control is below the adapter's abstraction layer |
| `IAnsiConsoleInput` | interface | excluded | Low-level stdin interface; prompts are wrapped at a higher level (Phase 2 P4) |
| `IAnsiConsoleOutput` | interface | excluded | Low-level stdout interface; output routing is internal to the adapter |
| `IBarChartItem` | interface | excluded | Internal Spectre trait; adapter uses the concrete `BarChartItem` record |
| `IBreakdownChartItem` | interface | excluded | Internal Spectre trait; adapter uses the concrete `BreakdownChartItem` record |
| `IColumn` | interface | excluded | Internal Spectre column-layout trait; not part of the adapter's public surface |
| `IExclusivityMode` | interface | excluded | Internal Spectre concurrency-exclusion interface (used by Live/Status/Progress internally); the adapter enforces single-session via `Result.Error ConcurrentLiveSession` at a higher level |
| `IExpandable` | interface | excluded | Internal Spectre trait |
| `IHasBorder` | interface | excluded | Internal Spectre trait |
| `IHasBoxBorder` | interface | excluded | Internal Spectre trait |
| `IHasCulture` | interface | excluded | Internal Spectre trait |
| `IHasJustification` | interface | excluded | Internal Spectre trait |
| `IHasTableBorder` | interface | excluded | Internal Spectre trait |
| `IHasTreeNodes` | interface | excluded | Internal Spectre trait |
| `IHasVisibility` | interface | excluded | Internal Spectre trait |
| `IMultiSelectionItem<T>` | interface | excluded | Internal Spectre prompt-choice trait; adapter uses `Composition`-typed choices directly (FR-014) |
| `IOverflowable` | interface | excluded | Internal Spectre trait |
| `IPaddable` | interface | excluded | Internal Spectre trait |
| `IProfileEnricher` | interface | excluded | Console-profile enrichment interface; terminal-capability configuration is internal to the adapter |
| `IPrompt<T>` | interface | excluded | Internal Spectre generic prompt trait; adapter wraps the concrete prompt types (Phase 2 P4) |
| `IReadOnlyCapabilities` | interface | excluded | Read-only view of terminal capabilities; superseded by `RenderContext` |
| `ISelectionItem<T>` | interface | excluded | Internal Spectre prompt-choice trait |
| `InteractionSupport` | enum | excluded | Terminal interaction-capability enum; superseded by `RenderContext` and `Result.Error NoInteractiveConsole` |
| `Justify` | enum | excluded | Horizontal text-justify enum used by Table cells internally; adapter exposes `Alignment` for layout-level alignment |
| `Known` | static class (nested in `Emoji`) | excluded | Nested type under `Emoji`; same rationale — emoji constants need no wrapper |
| `Known` | static class (nested in `Spinner`) | covered | Phase 2 (P3) — `Live.Spinner.known` module exposes named spinner presets |
| `Layout` | sealed class | excluded | Multi-pane split-screen widget. No current Fugue use-case identified; deferred to Phase 3 (Fugue-specific Layout framework tracked as a separate Spec Kit feature per plan.md). Re-evaluate at Phase 3 kickoff: either Phase 3's framework wraps `Layout` (promote to `covered`), or Phase 3 builds its own primitives independently (leave excluded). Classified by Phase 1 design 2026-05-16 — see data-model.md §7.1. |
| `LayoutExtensions` | static class | excluded | C# fluent-extension for `Layout` |
| `LiveDisplay` | sealed class | covered | Phase 2 (P3) — `Live.liveDisplay` session wrapper |
| `LiveDisplayContext` | sealed class | covered | Phase 2 (P3) — opaque context token passed to the update callback |
| `LiveDisplayExtensions` | static class | excluded | C# fluent-extension |
| `Markup` | sealed class | covered | Phase 1 — `Primitive.Markup` bridge case (used internally) |
| `MultiSelectionPrompt<T>` | sealed class | covered | Phase 2 (P4) — `Prompts.multiSelectionPrompt` wrapper |
| `MultiSelectionPromptExtensions` | static class | excluded | C# fluent-extension |
| `Overflow` | enum | excluded | Text-overflow enum used in low-level text primitives; `SafeText` / `Style` cover the common case |
| `OverflowableExtensions` | static class | excluded | C# fluent-extension |
| `PaddableExtensions` | static class | excluded | C# fluent-extension |
| `Padder` | sealed class | covered | Phase 1 — `Composition.Padded` DU case |
| `Padding` | struct | covered | Phase 1 — used internally in `Renderer.toRenderable` for `Padded` case |
| `PaddingExtensions` | static class | excluded | C# fluent-extension |
| `Panel` | sealed class | covered | Phase 1 — `Composition.Panel` DU case |
| `PanelExtensions` | static class | excluded | C# fluent-extension |
| `PanelHeader` | sealed class | covered | Phase 1 — used internally in `Renderer.toRenderable` for `Panel` header |
| `Paragraph` | sealed class | excluded | Multi-line wrapped text widget; `Primitive.Styled` with line-breaks covers the Fugue use-cases; no distinct adapter type needed |
| `PercentageColumn` | sealed class | excluded | Progress column subclass — adapter wraps the abstract `ProgressColumn` interface for custom columns (Phase 2 P3); concrete subclasses are pass-through |
| `PercentageColumnExtensions` | static class | excluded | C# fluent-extension |
| `Profile` | sealed class | excluded | Mutable terminal-profile bag (width, colour, capabilities); superseded by `RenderContext` |
| `ProfileEnrichment` | sealed class | excluded | Spectre's profile-enrichment coordinator; internal infrastructure |
| `Progress` | sealed class | covered | Phase 2 (P3) — `Live.progress` session wrapper |
| `ProgressBarColumn` | sealed class | excluded | Concrete `ProgressColumn` subclass; adapter renders the abstract `ProgressColumn` at the session layer |
| `ProgressBarColumnExtensions` | static class | excluded | C# fluent-extension |
| `ProgressColumn` | abstract class | covered | Phase 2 (P3) — `Live.ProgressColumn` F# wrapper (abstract base for custom progress columns) |
| `ProgressContext` | sealed class | covered | Phase 2 (P3) — opaque context token for `Progress` session updates |
| `ProgressExtensions` | static class | excluded | C# fluent-extension |
| `ProgressTask` | sealed class | covered | Phase 2 (P3) — `Live.ProgressTask` wrapper exposing `increment`/`value` |
| `ProgressTaskExtensions` | static class | excluded | C# fluent-extension |
| `ProgressTaskSettings` | sealed class | excluded | Mutable settings bag for `ProgressTask`; adapter smart constructor inlines these fields |
| `ProgressTaskState` | sealed class | excluded | Internal Spectre state dictionary for `ProgressTask`; not exposed in adapter |
| `Recorder` | class | excluded | Spectre's own output-capture helper; superseded in the adapter by `TestConsole` (Phase 2 P5) for tests and by `Renderer.toRawAnsi` for production capture |
| `RecorderExtensions` | static class | excluded | C# fluent-extension for `Recorder` |
| `Region` | sealed class | excluded | Named screen-region type for `Layout`; out of scope unless `Layout` is covered |
| `RemainingTimeColumn` | sealed class | excluded | Concrete `ProgressColumn` subclass; same rationale as other concrete subclasses |
| `RemainingTimeColumnExtensions` | static class | excluded | C# fluent-extension |
| `RenderableExtensions` | static class | excluded | C# fluent-extension on `IRenderable`; adapter does not expose `IRenderable` in its public surface |
| `Rows` | sealed class | covered | Phase 1 — `Composition.Stack` DU case |
| `Rule` | sealed class | covered | Phase 1 — `Primitive.Rule` DU case |
| `RuleExtensions` | static class | excluded | C# fluent-extension |
| `SelectionMode` | enum | excluded | Internal Spectre enum (leaf/recursive) for `SelectionPrompt`; adapter smart constructor exposes this as a named DU case |
| `SelectionPrompt<T>` | sealed class | covered | Phase 2 (P4) — `Prompts.selectionPrompt` wrapper |
| `SelectionPromptExtensions` | static class | excluded | C# fluent-extension |
| `Size` | sealed class | excluded | Width×Height value type used internally; not part of the adapter's public surface |
| `Spinner` | abstract class | covered | Phase 2 (P3) — `Live.Spinner` F# wrapper (abstract base; lets callers pass custom spinners) |
| `SpinnerColumn` | sealed class | excluded | Concrete `ProgressColumn` subclass; same rationale as other concrete subclasses |
| `SpinnerColumnExtensions` | static class | excluded | C# fluent-extension |
| `Status` | sealed class | covered | Phase 2 (P3) — `Live.status` session wrapper |
| `StatusContext` | sealed class | covered | Phase 2 (P3) — opaque context token for `Status` session updates |
| `StatusContextExtensions` | static class | excluded | C# fluent-extension |
| `StatusExtensions` | static class | excluded | C# fluent-extension |
| `StringExtensions` | static class | excluded | Low-level string helpers (`EscapeMarkup`, `RemoveMarkup`, `GetCellWidth`, `Mask`); `SafeText.ofUser` wraps `EscapeMarkup`; the rest have no direct adapter use-case |
| `Style` | sealed class | covered | Phase 1 — `Style` record with `Colour` and `Decoration` |
| `StyleExtensions` | static class | excluded | C# fluent-extension |
| `Table` | sealed class | covered | Phase 1 — `Composition.Table` DU case |
| `TableBorder` | abstract class | excluded | Abstract border-style base; the adapter's `Border` DU covers the four cases Fugue needs (`None_/Square/Rounded/Heavy`); other concrete borders are excluded |
| `TableColumn` | sealed class | covered | Phase 1 — used internally in `Renderer.toRenderable` for `Table` headers |
| `TableColumnExtensions` | static class | excluded | C# fluent-extension |
| `TableExtensions` | static class | excluded | C# fluent-extension |
| `TableRow` | sealed class | excluded | Mutable row container; used only inside `TableRowCollection`; adapter builds rows via `Composition.table` smart constructor |
| `TableRowCollection` | sealed class | excluded | Mutable row-collection type on `Table`; internal to Spectre's `Table` implementation |
| `TableTitle` | sealed class | excluded | Title/caption widget for `Table`; adapter may expose this via a `Table` option field in a future minor; not required for Phase 2 |
| `TaskDescriptionColumn` | sealed class | excluded | Concrete `ProgressColumn` subclass; same rationale as other concrete subclasses |
| `Text` | sealed class | covered | Phase 1 — `Primitive.Styled` case maps to `Spectre.Console.Text` internally |
| `TextPath` | sealed class | covered | Phase 2 (P2) — `DataDisplays.TextPath` smart constructor |
| `TextPathExtensions` | static class | excluded | C# fluent-extension |
| `TextPrompt<T>` | sealed class | covered | Phase 2 (P4) — `Prompts.textPrompt` wrapper |
| `TextPromptExtensions` | static class | excluded | C# fluent-extension |
| `TransferSpeedColumn` | class | excluded | Concrete `ProgressColumn` subclass for bandwidth display; same rationale as other concrete subclasses |
| `Tree` | sealed class | covered | Phase 2 (P2) — `DataDisplays.Tree` smart constructor |
| `TreeExtensions` | static class | excluded | C# fluent-extension |
| `TreeGuide` | abstract class | covered | Phase 2 (P2) — `DataDisplays.TreeGuide` F# DU or wrapper exposing the four guide styles |
| `TreeNode` | sealed class | covered | Phase 2 (P2) — `DataDisplays.TreeNode` smart constructor (part of `Tree` building) |
| `TreeNodeExtensions` | static class | excluded | C# fluent-extension |
| `ValidationResult` | sealed class | covered | Phase 2 (P4) — internal to prompt validation; adapter maps `string -> Result<'T, string>` to `ValidationResult` at the Spectre boundary |
| `VerticalAlignment` | enum | excluded | Used by `Layout`, which is itself excluded (deferred to Phase 3 per data-model.md §7.1). No current Fugue use-case. |
| `VerticalOverflow` | enum | excluded | Low-level text overflow enum; not needed at the adapter's abstraction level |
| `VerticalOverflowCropping` | enum | excluded | Low-level overflow-cropping enum |
| `VisibilityExtensions` | static class | excluded | C# fluent-extension |

---

## Spectre.Console.Advanced namespace (1 type)

| Type | Kind | Status | Phase / Justification |
|---|---|---|---|
| `AnsiConsoleExtensions` | static class | excluded | "Advanced" namespace is Spectre's own marker for low-level / unsupported surface. The two methods (`WriteAnsi`, `ToAnsi`) write raw ANSI byte sequences directly — equivalent to `Primitive.RawAnsi`, which the adapter already handles. Not exposed. |

---

## Spectre.Console.Extensions namespace (1 type)

| Type | Kind | Status | Phase / Justification |
|---|---|---|---|
| `AlignExtensions` | static class | excluded | C# fluent extension adding vertical-alignment (`TopAligned`, `MiddleAligned`, `BottomAligned`) and explicit `Width`/`Height` overrides to `Align`. The adapter's `Composition.Aligned` covers horizontal alignment; vertical-alignment and explicit size overrides have no current Fugue use-case. |

---

## Spectre.Console.Json namespace (10 types — separate NuGet package)

This namespace is shipped in the `Spectre.Console.Json` NuGet package
(NOT `Spectre.Console.dll`). Phase 2 P2 introduces this package as a
new `PackageReference` in `Fugue.Adapters.Console.fsproj` to support the
`DataDisplays.Json` primitive (decision recorded in
[data-model.md §0.2](./data-model.md)).

The package exports 10 public types: 1 user-facing widget (`JsonText`) and
9 internal JSON-AST types (`IJsonParser`, `JsonArray`, `JsonBoolean`,
`JsonMember`, `JsonNull`, `JsonObject`, `JsonString`, `JsonSyntax`) plus
one C# fluent-extension (`JsonTextExtensions`). Only `JsonText` has a
Fugue use-case; the AST types are implementation details of the `JsonText`
widget's internal parser and are excluded.

| Type | Kind | Status | Phase / Justification |
|---|---|---|---|
| `IJsonParser` | interface | excluded | Internal JSON-AST parser interface used by `JsonText` implementation; no Fugue use-case |
| `JsonArray` | class | excluded | Internal JSON-AST node type; implementation detail of `JsonText` renderer |
| `JsonBoolean` | class | excluded | Internal JSON-AST node type |
| `JsonMember` | class | excluded | Internal JSON-AST node type (key-value pair in object) |
| `JsonNull` | class | excluded | Internal JSON-AST node type |
| `JsonObject` | class | excluded | Internal JSON-AST node type |
| `JsonString` | class | excluded | Internal JSON-AST node type |
| `JsonSyntax` | class | excluded | Internal JSON-AST syntax element used by the `JsonText` renderer |
| `JsonText` | sealed class | covered | Phase 2 (P2) — `DataDisplays.Json` smart constructor; requires `Spectre.Console.Json 0.49.*` PackageReference (added in P2 PR) |
| `JsonTextExtensions` | static class | excluded | C# fluent-extension on `JsonText`; adapter exposes typed F# constructor directly |

---

## Spectre.Console.Rendering namespace (49 types)

| Type | Kind | Status | Phase / Justification |
|---|---|---|---|
| `Ascii2TableBorder` | sealed class | excluded | Concrete `TableBorder` subclass; Fugue's `Border` DU covers the four borders it needs |
| `AsciiBoxBorder` | sealed class | excluded | Concrete `BoxBorder` subclass; `Border.Square` covers the ASCII-box use-case |
| `AsciiDoubleHeadTableBorder` | sealed class | excluded | Concrete `TableBorder` subclass |
| `AsciiTableBorder` | sealed class | excluded | Concrete `TableBorder` subclass |
| `AsciiTreeGuide` | sealed class | excluded | Concrete `TreeGuide` subclass; covered indirectly via `DataDisplays.TreeGuide` DU |
| `BoldLineTreeGuide` | sealed class | excluded | Concrete `TreeGuide` subclass |
| `BoxBorderPart` | enum | excluded | Internal enum for `BoxBorder` rendering parts; not part of the adapter's public surface |
| `BoxExtensions` | static class | excluded | C# fluent-extension on `BoxBorder` |
| `DoubleBoxBorder` | sealed class | excluded | Concrete `BoxBorder` subclass |
| `DoubleEdgeTableBorder` | sealed class | excluded | Concrete `TableBorder` subclass |
| `DoubleLineTreeGuide` | sealed class | excluded | Concrete `TreeGuide` subclass |
| `DoubleTableBorder` | sealed class | excluded | Concrete `TableBorder` subclass |
| `HeavyBoxBorder` | sealed class | excluded | Concrete `BoxBorder` subclass; `Border.Heavy` covers this |
| `HeavyEdgeTableBorder` | sealed class | excluded | Concrete `TableBorder` subclass |
| `HeavyHeadTableBorder` | sealed class | excluded | Concrete `TableBorder` subclass |
| `HeavyTableBorder` | sealed class | excluded | Concrete `TableBorder` subclass |
| `HorizontalTableBorder` | sealed class | excluded | Concrete `TableBorder` subclass |
| `IAnsiConsoleEncoder` | interface | excluded | Internal Spectre render-pipeline interface; not part of the adapter's public surface |
| `IHasDirtyState` | interface | excluded | Internal Spectre dirty-flag trait for live-refresh optimization |
| `IRenderHook` | interface | excluded | Internal Spectre render-pipeline hook interface |
| `IRenderable` | interface | covered | Phase 2 (P1) — opaque `Renderable.CustomRenderable` bridge (FR-008): factory accepts `IRenderable`, returns an opaque adapter value embeddable in `Composition` |
| `JustInTimeRenderable` | abstract class | excluded | Internal Spectre base class for lazily-evaluated renderables; no Fugue use-case |
| `LineTreeGuide` | sealed class | excluded | Concrete `TreeGuide` subclass |
| `MarkdownTableBorder` | sealed class | excluded | Concrete `TableBorder` subclass |
| `Measurement` | struct | excluded | Internal struct returned by `IRenderable.Measure`; not part of the adapter's public surface |
| `MinimalDoubleHeadTableBorder` | sealed class | excluded | Concrete `TableBorder` subclass |
| `MinimalHeavyHeadTableBorder` | sealed class | excluded | Concrete `TableBorder` subclass |
| `MinimalTableBorder` | sealed class | excluded | Concrete `TableBorder` subclass |
| `NoBoxBorder` | sealed class | excluded | Concrete `BoxBorder` subclass; `Border.None_` covers this |
| `NoTableBorder` | sealed class | excluded | Concrete `TableBorder` subclass |
| `RenderHookScope` | sealed class | excluded | Internal Spectre IDisposable scope for temporarily adding a render hook; not exposed |
| `RenderOptions` | class | excluded | Internal render-options bag passed to `IRenderable.Render`; not part of the adapter's public surface |
| `RenderPipeline` | sealed class | excluded | Internal Spectre render pipeline orchestrator |
| `Renderable` | abstract class | excluded | Internal Spectre abstract base for built-in renderables; adapter uses `IRenderable` interface, not this base class |
| `RoundedBoxBorder` | sealed class | excluded | Concrete `BoxBorder` subclass; `Border.Rounded` covers this |
| `RoundedTableBorder` | sealed class | excluded | Concrete `TableBorder` subclass |
| `Segment` | class | excluded | Internal Spectre low-level render primitive (text + style + control); below the adapter's abstraction level |
| `SegmentLine` | sealed class | excluded | Internal Spectre list of `Segment` values for one terminal line |
| `SegmentLineEnumerator` | sealed class | excluded | Internal Spectre enumerator |
| `SegmentLineIterator` | sealed class | excluded | Internal Spectre iterator |
| `SimpleHeavyTableBorder` | sealed class | excluded | Concrete `TableBorder` subclass |
| `SimpleTableBorder` | sealed class | excluded | Concrete `TableBorder` subclass |
| `SquareBoxBorder` | sealed class | excluded | Concrete `BoxBorder` subclass; `Border.Square` covers this |
| `SquareTableBorder` | sealed class | excluded | Concrete `TableBorder` subclass |
| `TableBorderExtensions` | static class | excluded | C# fluent-extension on `TableBorder` |
| `TableBorderPart` | enum | excluded | Internal enum for `TableBorder` rendering parts |
| `TablePart` | enum | excluded | Internal enum for `Table` layout parts |
| `TreeGuideExtensions` | static class | excluded | C# fluent-extension on `TreeGuide` |
| `TreeGuidePart` | enum | excluded | Internal enum for `TreeGuide` rendering parts |

---

## Spectre.Console.Testing namespace (14 types)

| Type | Kind | Status | Phase / Justification |
|---|---|---|---|
| `CallbackCommandInterceptor` | sealed class | excluded | Testing helper for `Spectre.Console.Cli` command interception; out of scope (Fugue uses `System.CommandLine`, not Spectre.Cli) |
| `CommandAppFailure` | sealed class | excluded | Result type for `CommandAppTester` (Cli testing); out of scope |
| `CommandAppResult` | sealed class | excluded | Result type for `CommandAppTester` (Cli testing); out of scope |
| `CommandAppTester` | sealed class | excluded | Test harness for `Spectre.Console.Cli` apps; out of scope |
| `FakeTypeRegistrar` | sealed class | excluded | DI-registrar fake for `Spectre.Console.Cli` tests; out of scope |
| `FakeTypeResolver` | sealed class | excluded | DI-resolver fake for `Spectre.Console.Cli` tests; out of scope |
| `StringExtensions` | static class | excluded | Cli-test string helper; out of scope |
| `StyleExtensions` | static class | excluded | Cli-test style assertion helper; out of scope |
| `TestCapabilities` | sealed class | covered | Phase 2 (P5) — consumed internally by `TestConsole` wrapper |
| `TestConsole` | sealed class | covered | Phase 2 (P5) — `Console.TestConsole` wrapper for deterministic test capture (FR-017) |
| `TestConsoleExtensions` | static class | excluded | C# fluent-extension; adapter exposes typed F# test-console functions directly |
| `TestConsoleInput` | sealed class | covered | Phase 2 (P5) — consumed internally by `Console.TestConsole` to inject simulated input |
| `TestFailedException` | sealed class | excluded | Exception thrown by Cli test assertions; out of scope |
| `TypeRegistrarBaseTests` | sealed class | excluded | xUnit base test class for `ITypeRegistrar` implementations (Cli); out of scope |

---

## Spectre.Console.Cli namespace (excluded in full — 60 types)

The `Spectre.Console.Cli` assembly ships a separate `Spectre.Console.Cli.dll` and exposes 60
types across four namespaces:

- `Spectre.Console.Cli` (40 types) — command framework: `Command`, `CommandApp`, `CommandSettings`, argument/option attributes, DI registrar interfaces, etc.
- `Spectre.Console.Cli.Help` (15 types) — help-page styling: `HelpProvider`, style records, `IHelpProvider`, etc.
- `Spectre.Console.Cli.Internal.Configuration` (1 type) — `DefaultCommandConfigurator` (accidentally public; internal implementation detail)
- `Spectre.Console.Cli.Unsafe` (4 types) — unsafe configurator extensions

**Justification for blanket exclusion**: Fugue uses `System.CommandLine` (AOT source-gen friendly) for CLI argument parsing, not `Spectre.Console.Cli`. Wrapping a second command-routing framework would conflict with the existing `Fugue.Cli.Aot/Program.fs` parser and add ~60 types of adapter surface with zero Fugue call-sites. The `Spectre.Console.Cli.*` namespace is a separate concern from console rendering and is excluded in its entirety.

Note: The `Spectre.Console.Cli` types do **not** appear in the per-row count above (the 228 total counts only `Spectre.Console.dll` + `Spectre.Console.Testing.dll`). They are listed here for completeness so the matrix is fully exhaustive with respect to the 0.49.1 package family.

---

## Excluded types — justification index

| Justification pattern | Count | Examples |
|---|---|---|
| C# fluent-extension (adapter uses typed F# constructors instead) | 53 | `AlignableExtensions`, `BarChartExtensions`, all `*Extensions` static classes |
| Internal Spectre trait interface (not in adapter public surface) | 16 | `IAlignable`, `IColumn`, `IHasBorder`, `IHasTreeNodes`, `IRenderHook`, etc. |
| Concrete `TableBorder`/`BoxBorder`/`TreeGuide` subclass (covered by DU) | 23 | `AsciiTableBorder`, `DoubleBoxBorder`, `RoundedBoxBorder`, `AsciiTreeGuide`, etc. |
| Internal Spectre rendering primitive (below adapter abstraction) | 9 | `Segment`, `SegmentLine`, `RenderOptions`, `Measurement`, `TableBorderPart`, etc. |
| Concrete `ProgressColumn` subclass (adapter wraps abstract base) | 6 | `DownloadedColumn`, `ElapsedTimeColumn`, `PercentageColumn`, `RemainingTimeColumn`, `SpinnerColumn`, `TaskDescriptionColumn` |
| Superseded by `RenderContext` | 5 | `AnsiSupport`, `ColorSystem`, `InteractionSupport`, `Profile`, `Capabilities` |
| `Spectre.Console.Cli`-related (out of scope, wrong CLI framework) | 14 | All `Spectre.Console.Testing.*Cli*` types; full `Spectre.Console.Cli` assembly |
| `[Obsolete]` | 1 | `AnsiConsoleFactory` |
| No Fugue use-case / too low-level | 9 | `Canvas`, `ControlCode`, `CursorDirection`, `Emoji`, `Paragraph`, `Recorder`, `CharExtensions`, `StringExtensions`, `Size` |

---

## Notes

- Generated by enumeration of `Spectre.Console.dll` and `Spectre.Console.Testing.dll` at version
  0.49.1 (the resolved version in the NuGet cache as of 2026-05-16). The `.fsproj` pins
  `0.49.*` so the next patch (if any) is upgrade-workflow.md covered.
- `Spectre.Console.Cli` (60 types) is excluded in full and listed under its own section; it is
  NOT included in the 228-type total to avoid double-counting with the assembly enumeration.
- The two `Known` nested types (`Emoji+Known`, `Spinner+Known`) are exported by the reflection
  API as separate entries; they are classified under their parent concept here.
- `Json` does not exist in `Spectre.Console 0.49.1` itself; it lives in `Spectre.Console.Json`,
  a separate NuGet package. **Phase 1 design resolution** (data-model.md §0.2): add the
  `Spectre.Console.Json 0.49.*` PackageReference to `Fugue.Adapters.Console.fsproj` in the P2
  PR. The matrix initially listed only `JsonText` (1 type); the T079 Polish audit found the
  assembly actually exports 10 types — the 9 additional entries (`IJsonParser`, `JsonArray`,
  `JsonBoolean`, `JsonMember`, `JsonNull`, `JsonObject`, `JsonString`, `JsonSyntax`,
  `JsonTextExtensions`) are internal AST infrastructure excluded with justification above.
- `Layout` was the only `pending` entry at the close of the matrix's initial enumeration. It is
  now **classified as `excluded`** per Phase 1 design 2026-05-16 (data-model.md §7.1) —
  deferred to Phase 3 (Fugue-specific Layout framework, a separate Spec Kit feature). The
  `VerticalAlignment` enum (previously "out of scope unless `Layout` is covered") is similarly
  noted as cleanly `excluded`.
- `Regeneration`: to re-enumerate after a Spectre version bump, load the new DLL via the same
  `System.Reflection.Assembly.LoadFrom` + `GetExportedTypes()` script used to generate this
  file (see `/tmp/enumerate-spectre2.fsx` — not committed; re-create from this file's header if
  needed). A committed `scripts/regen-coverage-matrix.fsx` is a FR-018 follow-up for a future PR.
- `pending` count MUST be 0 at the close of Phase 2 P5 PR. **Status as of Phase 1 design
  (2026-05-16): 0 pending rows.** SC-001 satisfied at the design phase; PR-merge re-check is
  still required after each user-story PR lands.
