# Feature Specification: Full Spectre.Console F# Coverage

**Feature Branch**: `002-spectre-full-coverage`

**Created**: 2026-05-16

**Status**: Draft

**Input**: User description: "Phase 2: расширить Fugue.Adapters.Console до полного покрытия публичного API Spectre.Console 0.49.* идиоматичным F# wrapper'ом. Phase 1 (PR #942) покрыл MVP: Markup, Style, Decoration, Color, SafeText, Padder, Panel, Columns, Align, Table, Rule. Закрыть остальное: Tree, Json, BarChart, BreakdownChart, Calendar, FigletText, Grid, TextPath, Live/LiveDisplay, Status, Progress, Spinners, TextPrompt, SelectionPrompt, MultiSelectionPrompt, ConfirmationPrompt, AnsiConsole/IAnsiConsole/TestConsole, exception rendering, IRenderable как opaque type для custom renderables. Те же принципы что Phase 1: typed primitives, Result-returning smart constructors, escape-tracked SafeText, никаких exposed C# типов в публичных signatures, per-module .fsi sealing. После Phase 2 будет Phase 3: высокоуровневый Layout-фреймворк поверх этих примитивов под Fugue UX."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Custom-renderable bridge (Priority: P1)

A Fugue contributor porting an existing REPL render-site (one of 54 sites using `IRenderable` directly from Spectre.Console) needs to keep their own custom renderable type (e.g. a Markdown-rendered code block, a streaming token row) and still pass it into adapter compositions (`Composition.Panel`, `Composition.Stack`, etc.) without leaking `Spectre.Console.Rendering.IRenderable` through the adapter's public surface.

**Why this priority**: This unblocks the entire downstream REPL port. Without an opaque renderable bridge, contributors cannot move any nontrivial REPL site (which routinely mixes typed primitives with hand-written `IRenderable` implementations) onto the adapter. All other gaps can be worked around with direct Spectre calls in narrow places; this one cannot. It's also the only gap that touches existing Phase 1 surface (adds a new DU case to `Primitive` or a new wrapper type), so it must land first to set the pattern.

**Independent Test**: Take a single existing REPL site that defines a custom `IRenderable` (e.g. one of the rows in `Picker.fs` or a `MarkdownRender.fs` block helper), wrap that custom renderable in the new adapter bridge type, embed it inside a `Composition.Panel`, render to ANSI string via `Renderer.toRawAnsi`, and assert the output contains both the panel border and the custom renderable's content. No `Spectre.Console.*` namespace appears in the call-site's signatures.

**Acceptance Scenarios**:

1. **Given** an existing F# function returning `Spectre.Console.Rendering.IRenderable`, **When** the contributor wraps it via the new adapter bridge and embeds inside `Composition.Panel`, **Then** the rendered ANSI output is byte-identical to the same arrangement built with Spectre directly (modulo whitespace at composition seams).
2. **Given** a custom renderable that throws an exception during render, **When** evaluated through the adapter, **Then** the call returns `Result.Error (RenderFailed (name, message))` rather than propagating the exception out of the adapter boundary.
3. **Given** a contributor writing a NEW custom renderable specifically against the adapter, **When** they implement the adapter's renderable interface (or extend its DU), **Then** they can do so without referencing the `Spectre.Console` namespace at all in their module.

---

### User Story 2 - Data-display primitives (Priority: P2)

A Fugue contributor porting a render-site that outputs structured data (JSON payloads, hierarchical file trees, bar charts, calendars, formatted file paths) needs typed F# wrappers for Spectre's data-display widgets, with the same `Result`-returning smart-constructor discipline as Phase 1.

**Why this priority**: After P1 unblocks the bridge, data-display widgets are the broadest single class of unported uses (~30 call sites today). Closing them as a group is high-leverage: every widget shares the same "renders static data into a `Composition`-compatible shape" pattern, so once one is done idiomatically, the rest follow mechanically. None of these widgets need IO or interaction, which keeps the design scope tight.

**Independent Test**: For each new primitive (`Tree`, `Json`, `BarChart`, `BreakdownChart`, `Calendar`, `FigletText`, `Grid`, `TextPath`), write a property test that round-trips a representative value through the adapter and asserts: (a) render returns `Ok`, (b) output contains representative data fragments, (c) smart constructor rejects degenerate inputs (empty trees, malformed JSON, negative chart values, ragged grids) with `Result.Error InvalidArgument`.

**Acceptance Scenarios**:

1. **Given** a JSON string representing a tool-result payload, **When** wrapped in the new `Primitive.Json` and rendered through a composition, **Then** the output contains syntax-coloured (when colour enabled) or plain (when disabled) JSON respecting the active `RenderContext`.
2. **Given** a tree of nested file paths, **When** built via the adapter's `Tree` smart constructor with mixed nodes and rendered, **Then** the output shows the hierarchy with Unicode line-drawing characters that match Spectre's native `Tree` output byte-for-byte at the same width.
3. **Given** a `Grid` smart constructor called with rows of unequal column count, **When** evaluated, **Then** the constructor returns `Result.Error (InvalidArgument ("Grid", "ragged rows: expected N columns, got M"))` BEFORE any render is attempted.

---

### User Story 3 - Live/streaming displays (Priority: P3)

A Fugue contributor porting the LLM-streaming render path (today calling Spectre's `Live`, `LiveDisplay`, `Status`, `Progress` directly) needs typed F# wrappers that integrate with the adapter's existing pure-render model. Specifically, a way to drive an iterative refresh loop that updates a composition over time (token-by-token streaming, file-operation progress, indeterminate spinner during LLM thinking) without exposing Spectre's mutable session objects through the adapter surface.

**Why this priority**: Live UI is structurally distinct from static rendering — it has lifetime (start/stop), state (current refresh), and concurrency concerns (background updates while user types). It's used in fewer call sites (~5 today) but is critical for the most visible REPL UX (streaming responses). Designing it correctly is a separate problem from data-display primitives, so it gets its own user story and likely its own design pass in the plan.

**Independent Test**: Port the existing "spinner while LLM is thinking" render-site onto the adapter's new live primitive. Assert: (a) spinner starts/stops in response to async signals via a typed F# API (no `IDisposable` leaked, no `IAnsiConsoleCursor` exposed), (b) replacing the spinner with a `Composition` after the LLM responds works through the same adapter API (no special-casing), (c) cancellation (Ctrl+C, timeout) tears the live session down without leaving the terminal in a corrupt cursor state.

**Acceptance Scenarios**:

1. **Given** an async `IAsyncEnumerable<string>` of LLM tokens, **When** driven through a Live wrapper that updates a `Composition` per token, **Then** terminal output appends tokens visibly without flicker and tears down cleanly when the stream completes.
2. **Given** a `Progress` session with multiple named tasks running in parallel, **When** any task updates its completion percentage, **Then** the corresponding bar redraws independently without re-rendering siblings.
3. **Given** an indeterminate `Status` session running for >5s, **When** the user sends SIGINT, **Then** the session disposes its cursor and the next adapter call renders to a clean terminal state.

---

### User Story 4 - Interactive prompts (Priority: P4)

A Fugue contributor needs typed F# wrappers for Spectre's interactive prompts (`TextPrompt`, `SelectionPrompt`, `MultiSelectionPrompt`, `ConfirmationPrompt`) so existing slash-command interactions (e.g. `/model set`, `/profile pick`, approval-mode confirmations) and future ones can be implemented against the adapter rather than direct Spectre calls.

**Why this priority**: Interactive prompts are IO-coupled (they read from stdin) and therefore architecturally distinct from the rest of the adapter. They have lower call-site count (~3 today, ~10 expected after Phase 3) and are largely confined to a few `Repl.fs` slash-command handlers. Closing them after Live ensures the IO model decisions made for Live (cancellation, terminal-state recovery) apply cleanly here.

**Independent Test**: Replace one existing direct-Spectre `SelectionPrompt` call-site (e.g. model picker) with the adapter's wrapper. Assert: (a) prompt UX is byte-identical to before, (b) cancellation via Ctrl+C returns `Result.Error UserCancelled` rather than throwing, (c) the wrapper accepts `Composition`-typed choice labels (so a choice can be a styled string with icons, not just a plain string).

**Acceptance Scenarios**:

1. **Given** a list of `Composition` values as choices for a `SelectionPrompt`, **When** the user presses Enter on the highlighted choice, **Then** the wrapper returns `Result.Ok choiceValue` typed as the original choice.
2. **Given** a `TextPrompt` with a validator function `string -> Result<'T, string>`, **When** the user enters invalid input, **Then** Spectre's native re-prompt loop runs with the validator's error message inline (no exception thrown).
3. **Given** a `ConfirmationPrompt` interrupted by SIGINT, **When** the interrupt fires, **Then** the wrapper returns `Result.Error UserCancelled` and the terminal cursor is left at a clean line.

---

### User Story 5 - Console infrastructure & testing (Priority: P5)

A Fugue contributor needs the adapter to expose enough of Spectre's console-infrastructure surface (`AnsiConsole` factory configuration, `IAnsiConsole` for direct writes when needed, `TestConsole` for unit tests, exception rendering helpers) that production sites can fully migrate away from direct Spectre access AND test sites can drive the adapter deterministically without spinning up a real terminal.

**Why this priority**: Console infrastructure is plumbing — it doesn't add user-facing features, but without it the migration story is incomplete: there will always be 5-6 call sites in `Surface.fs` / `Render.fs` that need a real `IAnsiConsole` for capture, write-direct, or settings tweaks. Closing those last sites is what flips the constitution Principle II audit from "mostly clean" to "compiler-enforced clean". `TestConsole` exposure also enables Phase 3 (custom Layout framework) to test against a deterministic console without inventing new test infrastructure.

**Independent Test**: Migrate the remaining direct-`AnsiConsole` call-sites in `Surface.fs` and `Render.fs` onto the adapter. Run `rg "Spectre\.Console" src/Fugue.Cli/ src/Fugue.Surface/` and confirm zero results (the adapter is the only place that touches Spectre). Adapter test suite uses the new `TestConsole` wrapper for all rendering tests.

**Acceptance Scenarios**:

1. **Given** the adapter's `IAnsiConsole`-wrapper exposes `Write(Composition)` / `Capture(Composition) -> string`, **When** a production site needs to render directly to the user's terminal (not into a `Composition` tree), **Then** it can do so without referencing `Spectre.Console.*` types.
2. **Given** the adapter's `TestConsole` wrapper is used inside a property test, **When** a `Composition` is rendered through it, **Then** the captured output is byte-identical to what would have appeared on a real terminal at the same width and colour setting.
3. **Given** an unhandled exception in the host program, **When** rendered via the adapter's exception-rendering helper, **Then** the output mirrors Spectre's native exception output (stack frames, syntax-coloured types, line numbers) at the same `RenderContext`.

---

### Edge Cases

- **Cancellation mid-Live**: User cancels (Ctrl+C / token) during a `Live`, `Status`, `Progress`, or interactive `*Prompt` session. The adapter MUST restore the cursor, scroll region, and any alternate-screen state cleanly; the next adapter call must render to a normal terminal.
- **Non-TTY environment (CI, pipes, headless)**: Live/Status/Progress/Prompts fall back to non-interactive behaviour: `Status` collapses to a single "done" line, `Progress` emits a final tally without per-frame redraws, prompts return a deterministic default or `Result.Error NoInteractiveConsole`.
- **Colour downgrade**: A `RenderContext` with `colourEnabled = false` must produce the same structural output (panel borders, layout, content) as colour-enabled rendering, with all colour ANSI sequences omitted (not "approximated to monochrome"). Verified per-primitive in property tests.
- **Concurrent Live sessions**: Two adapter `Live` instances active simultaneously on the same adapter `Console`. Spectre's behaviour is undefined; the adapter MUST reject the second concurrent `start` call with `Result.Error ConcurrentLiveSession` on the same adapter `Console` (no implicit serialisation).
- **Custom-renderable bridge throws**: A user-provided custom renderable raises an exception during `Render`. The adapter catches it at the boundary and returns `Result.Error (RenderFailed (typeName, message))` — never propagates out.
- **Smart-constructor degenerate input**: Empty `Tree`, malformed `Json`, negative `BarChart` values, ragged `Grid` rows, zero-column `Table`, empty `SelectionPrompt` choices, etc. — each rejected by its smart constructor with `Result.Error (InvalidArgument (node, detail))` BEFORE render.
- **Very large input**: `FigletText` with a 100-character input, `Tree` with 10000 nodes, `Json` with deeply nested payload. Adapter MUST NOT silently truncate; it either renders (possibly slowly) or returns `Result.Error InvalidArgument` with a documented size threshold. (Phase 1 `Table` size handling is unchanged by Phase 2 and audited separately.)

## Requirements *(mandatory)*

### Functional Requirements

**Surface completeness (compiler-enforced):**

- **FR-001**: Adapter MUST expose an F# wrapper for every public type in `Spectre.Console 0.49.*` not already in Phase 1, OR explicitly justify its exclusion in `data-model.md` with reasoning (one of: superseded by a Phase 1 primitive; obsolete attribute on the upstream type; throws unconditionally on non-TTY consoles; requires reflection or runtime code-gen that this adapter cannot safely host).
- **FR-002**: No `Spectre.Console.*` (or any `Spectre.*` sub-namespace) type MAY appear in the adapter's public signatures (canonical no-leak rule). Verified by `.fsi` review and by `rg 'Spectre\.' src/Fugue.Adapters.Console/*.fsi` returning zero results, except the single intentional bridge carved out by FR-008. SC-004 measures this for adapter `.fsi` files; SC-002 measures the downstream REPL codebase (different scope — see SC-002 note).
- **FR-003**: Every adapter public type MUST be sealed via per-module `.fsi`, following the pattern established in PR #942 (records `private`, DU constructors closed unless documented as user-extension points).
- **FR-004**: Adapter MUST preserve full backward compatibility with Phase 1 public API. No renames, no removed cases, no breaking signature changes. Verified by running the existing 83 adapter tests with their assertions intact; pattern-match exhaustiveness updates required by new `RenderError` cases in `Errors.fs` are scaffolding changes, not assertion changes.

**Smart-constructor discipline:**

- **FR-005**: Every primitive or composition that can fail structurally (invalid argument, degenerate input) MUST expose a smart constructor returning `Result<'T, RenderError>`. Direct DU construction MUST be prevented at the type level (DU cases `private`, OR record fields `private`).
- **FR-006**: `RenderError` DU MUST be extended with new cases ONLY when no existing case fits semantically. Each new case is a MINOR adapter-semver bump; renaming or removing cases is MAJOR. New cases needed by Phase 2 (anticipated): `UserCancelled`, `NoInteractiveConsole`, `ConcurrentLiveSession`.
- **FR-007**: User-supplied strings entering the adapter (prompt placeholders, table cell contents, tree node labels, JSON inline strings) MUST be wrapped in `SafeText` at the smart-constructor boundary. Internal helpers MUST NOT accept raw `string` for user-content fields.

**Renderable bridge (US1):**

- **FR-008**: Adapter MUST expose a typed F# wrapper that lets callers embed any externally-defined renderable into a `Composition` tree WITHOUT exposing the underlying Spectre interface type in public signatures. The wrapper accepts only the renderable as an opaque value via a dedicated factory function whose argument is the Spectre type but whose return is the adapter's opaque wrapper. See FR-002 for the general no-leak rule; this FR carves out the single intentional bridge from that rule.
- **FR-009**: Exceptions thrown by user-supplied renderables during rendering MUST be caught at the adapter boundary and converted to `Result.Error (RenderFailed (typeName, message))`. No Spectre exception type leaks across the adapter boundary.

**Live UI lifetime (US3):**

- **FR-010**: Live sessions (`Live`, `Status`, `Progress`) MUST expose lifetime via a typed F# API: an explicit `start` returning a session token, a `update` accepting a `Composition`, an explicit `stop` / `dispose`. No raw `IDisposable` leaks; no callback-with-mutable-state pattern.
- **FR-011**: Cancellation (CancellationToken passed to `start`) MUST tear down the session cleanly: cursor restored, scroll region cleared, alternate-screen exited if entered.
- **FR-012**: Two concurrent Live sessions on the same `IAnsiConsole` MUST be rejected: the second `start` call returns `Result.Error ConcurrentLiveSession`.

**Interactive prompts (US4):**

- **FR-013**: Prompt wrappers (`TextPrompt`, `SelectionPrompt`, `MultiSelectionPrompt`, `ConfirmationPrompt`) MUST return `Async<Result<'T, RenderError>>` (or `Task<Result<...>>`), never `'T` directly. Cancellation returns `Result.Error UserCancelled`. Non-TTY behaviour: see FR-016.
- **FR-014**: `SelectionPrompt` / `MultiSelectionPrompt` choice labels MUST accept `Composition` values (not just plain strings), so choices can be styled (icon + text, dim suffix, etc.).
- **FR-015**: `TextPrompt` validators MUST be typed F# functions `string -> Result<'T, string>` returning the parsed value on success and a user-visible error message on failure.

**Non-TTY behaviour (cross-cutting):**

- **FR-016**: When the active `RenderContext` indicates non-interactive environment (no TTY, redirected stdout, etc.), Live/Status/Progress MUST degrade to non-redrawing fallback (single final state line), and interactive prompts MUST return `Result.Error NoInteractiveConsole` without blocking on stdin read.

**Testing infrastructure (US5):**

- **FR-017**: Adapter MUST expose a `TestConsole` wrapper (over `Spectre.Console.Testing.TestConsole`) that gives test code an `IAnsiConsole`-equivalent for capturing rendered output deterministically. Existing 83 adapter tests SHOULD migrate onto this wrapper as part of Phase 2 (not required for new tests).

**Coverage verification:**

- **FR-018**: A new file `specs/002-spectre-full-coverage/coverage-matrix.md` MUST track every public type in `Spectre.Console 0.49.*` with one of three statuses: `covered` (Phase 1 or Phase 2 wrapper), `excluded` (with justification), or `pending` (must be empty at PR-merge time).
- **FR-019**: A new property test MUST verify that for every public type in the coverage matrix marked `covered`, an adapter wrapper exists and `Renderer.toRawAnsi` produces non-empty output for a representative value of that type.

### Key Entities

- **`IRenderable` opaque bridge**: An adapter type wrapping any externally-defined Spectre renderable. Single factory function takes the renderable and returns an opaque adapter value embeddable in any `Composition`.
- **Data-display primitives**: New cases on `Primitive` (or new typed wrappers) for `Tree`, `Json`, `BarChart`, `BreakdownChart`, `Calendar`, `FigletText`, `Grid`, `TextPath`. Each has its own smart constructor with structural validation.
- **Live session**: Typed F# representation of an active Spectre `Live`/`Status`/`Progress` session. Carries start/update/stop API plus cancellation token; opaque to callers (no underlying Spectre type exposed).
- **Prompt request**: Typed F# representation of a pending interactive prompt: title, type-parameterised choices/validator, cancellation token, target console.
- **`TestConsole` wrapper**: F# wrapper over `Spectre.Console.Testing.TestConsole` exposing deterministic output capture for tests.
- **Coverage matrix**: Append-only document tracking adoption status of every public Spectre type, evaluated against `Spectre.Console.dll`'s public surface at the resolved version.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% of public types in `Spectre.Console 0.49.*` are accounted for in the coverage matrix (`covered` or `excluded` with justification). Zero `pending` entries at PR merge.
- **(Deferred to Phase 3 — REPL port wave)**: **SC-002**: After the Phase 3 REPL port wave lands, `rg 'Spectre\.Console' src/Fugue.Cli/ src/Fugue.Surface/` returns zero matches (the adapter is the only consumer of Spectre across the production codebase). Phase 2 PR-close only guarantees the adapter is *capable* of replacing direct Spectre access, not that the REPL has been ported. Measurement scope differs from SC-004 (which audits adapter `.fsi` internals); see FR-002 for the canonical no-leak rule.
- **SC-003**: Adapter test suite grows from 83 tests (Phase 1) to ≥250 tests (Phase 2), with the same totality + property + snapshot + perf-budget structure as Phase 1. Suite still runs in under 5 seconds on the project CI matrix (`dotnet test -c Release --no-build`, single cold run, ubuntu-latest / macos-14 / windows-latest). Budget applies **per leg**, not aggregate — each of the three OS legs must independently satisfy ≤ 5 s.
- **SC-004**: Zero `Spectre.*` types appear in any adapter `.fsi` file **except** the single intentional bridge `Renderable.fromSpectre : Spectre.Console.Rendering.IRenderable -> Renderable` carved out by FR-008. Verified by `rg 'Spectre\.' src/Fugue.Adapters.Console/*.fsi` returning at most that one signature match (doc-comment matches in `.fsi` files are OK; only type-signature exposure counts). Cross-refs FR-002 (the canonical no-leak rule); SC-002 measures the downstream REPL scope separately.
- **SC-005**: Phase 1 backward compatibility: all 83 existing Phase 1 tests pass with their assertions intact after Phase 2; pattern-match exhaustiveness updates for new `RenderError` cases are scaffolding changes, not assertion changes.
- **SC-006**: AOT publish of the headless `fugue-aot` binary stays clean (zero new trim/AOT warnings) — adapter is JIT-only and is NOT included in the headless closure, so this should hold by construction; verify in CI.
- **SC-007**: Every public adapter type from Phase 2 has at least one property test exercising its smart constructor (both `Ok` and `Error` paths) and at least one snapshot or totality test exercising its renderer path.

## Assumptions

- **Phase 1 surface stays as-is**. Existing 83 tests, `.fsi` sealing, smart-constructor patterns, and `RenderError` DU are the baseline. Phase 2 extends; it does not refactor Phase 1 unless a Phase 2 wrapper genuinely conflicts.
- **`Spectre.Console 0.49.*` is the target version.** A future Spectre bump (0.50.x or 1.0.x) is out of scope; the `coverage-matrix.md` is pinned to whatever `0.49.x` was resolved at PR-merge time, and bumps are handled by the existing `upgrade-workflow.md` documented in PR #942.
- **Adapter remains JIT-only.** All Phase 2 additions go into the existing `Fugue.Adapters.Console.fsproj`, which is referenced only from `Fugue.Cli` / `Fugue.Surface` (JIT), never from `Fugue.Cli.Aot`. No AOT-friendliness concessions needed.
- **Phase 3 (Layout framework) is out of scope.** Phase 2 produces primitives, not high-level layouts. Phase 3 will build atop these and is tracked as a separate Spec Kit feature.
- **Direct Spectre access is forbidden post-Phase-2.** Once Phase 2 lands and porting is complete, any new `open Spectre.Console` in `src/Fugue.Cli/` or `src/Fugue.Surface/` is a build-time failure (enforced by a custom Ionide/G-Research analyzer rule, OR by an `rg`-based CI guard — choice deferred to plan.md).
- **Interactive prompts use Spectre's native re-prompt loop for validator failures.** The adapter wraps it; it does not reimplement input/echo/cursor management.
- **Live sessions use Spectre's native concurrency model.** The adapter enforces single-session-per-console at the wrapper layer; Spectre's own thread safety guarantees inside a session are inherited unchanged.
- **`IRenderable` opaque bridge wraps existing Spectre renderables.** Defining brand-new renderables in pure F# without referencing Spectre's interface is NOT a Phase 2 requirement (deferred until a real use case emerges).
- **Exception rendering reuses Spectre's `GetExceptionFormatter` extension.** The adapter exposes it as a typed F# function; reimplementing exception layout is out of scope.
- **Tests are written using the same `[<Property(MaxTest=1)>]` workaround documented in `Helpers.fs`** until the upstream xunit Fact-discovery issue (Neftedollar/FsLangMCP#100, korat-ai/fugue follow-up TBD) is resolved.
- **`engineering-fsharp-developer` and `engineering-fsharp-autotester` subagents will do most of the implementation work**, following the same delegation pattern that produced PR #942.
- **PR splitting follows user-story boundaries with one exception: US3 (Live) and US5 (Console) ship as a single combined PR because `Live.run` writes directly to the `Console` wrapper introduced in US5, and US4 prompts require US5's `TestConsole` for IO scripting in tests. Net PR count: 4 (Setup+US1; US2; US3+US5 combined; US4) plus a Polish PR. See `tasks.md` PR-strategy section for the authoritative breakdown.**
