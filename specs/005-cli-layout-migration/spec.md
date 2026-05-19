# Feature Specification: Migrate Fugue.Cli UI to the Layout Framework

**Feature Branch**: `005-cli-layout-migration`

**Created**: 2026-05-19

**Status**: Draft

**Input**: User description: "Phase 4 — migrate the Fugue REPL's rendering paths from direct calls into the underlying terminal-rendering library to the in-repo F# adapter and Layout framework that the project itself built in Phase 1–3. This is a pure internal refactor: end users see no change, but every render path now travels through the typed F# API, dogfooding the framework in production and closing the loop on several long-standing rendering bugs."

## User Scenarios & Testing *(mandatory)*

### User Story 1 — End-user of the Fugue REPL sees no visible change across a full session (Priority: P1)

A returning Fugue user starts the REPL, types a message, sees the assistant stream a markdown response, watches a tool call render its result (Edit diff), checks the status bar, runs `/turns` to pick a past message, and exits. From their seat, nothing about the session looks different from the previous Fugue version — same colours, same spacing, same scroll behaviour, same speed.

**Why this priority**: This is the entire "do no harm" guarantee. A refactoring that visibly degrades the UX is a net loss regardless of how much internal cleanliness it achieved. Treating visual parity as a first-class user story (not a hidden non-functional requirement) keeps it on the task list and gives it test coverage equal to the new functionality.

**Independent Test**: A reviewer runs the pre-migration Fugue binary and the post-migration Fugue binary through the same scripted REPL session (3 messages, 1 tool invocation, 1 streaming markdown response, 1 `/turns` invocation) and compares the resulting terminal output character-for-character or screenshot-for-screenshot. The two sessions are visually equivalent within an explicitly defined tolerance (colour codes match exactly; whitespace and cursor positioning match exactly; only timestamps and other genuinely-volatile session content differ).

**Acceptance Scenarios**:

1. **Given** a user runs a complete REPL session, **When** they compare the terminal output to the pre-migration version, **Then** the rendered output is visually equivalent — same characters in same positions, same colours, same spacing.
2. **Given** a user watches the assistant stream a markdown response, **When** the response completes, **Then** the perceived smoothness of token-by-token streaming is at least as good as pre-migration (no perceptible stutter, no flicker, no extra full-frame redraws).
3. **Given** a user runs the `/turns` interactive picker, **When** they arrow through past messages, **Then** the selected-row highlighting and dispatch behaviour match pre-migration.
4. **Given** a user runs a command that fails with an exception (e.g. a tool error), **When** the exception renders, **Then** the stack trace formatting, colours, and link rendering match pre-migration.

---

### User Story 2 — Contributor opens any rendering file and sees pure adapter API, no underlying-library imports (Priority: P1)

A new contributor (or the same contributor 3 months later) opens any of the files that previously rendered directly into the underlying terminal library. They see only the project's own F# adapter API in use — typed primitives, smart constructors returning `Result`, Layout primitives where structure matters. They never have to learn the underlying library's idioms to read or modify these files.

**Why this priority**: Same priority level as Story 1 because the two together form the feature's minimum acceptable result: you cannot ship one without the other. Without this guarantee, the migration is incomplete — leftover direct calls become technical debt that re-accumulates over time and reintroduces the very coupling Phase 4 set out to eliminate.

**Independent Test**: A reviewer runs a text-search command for the underlying library's name across the REPL source tree and observes zero matches (excluding two designated bridge files that legitimately reference the adapter). A contributor reading any one of the eight previously-coupled files can describe what it does using only adapter terminology.

**Acceptance Scenarios**:

1. **Given** the migration is complete, **When** a contributor searches the REPL source tree for the underlying library's namespace, **Then** zero matches are returned outside the two designated bridge files.
2. **Given** a contributor opens any of the eight previously-coupled files, **When** they scan the imports, **Then** they see only F# adapter modules — no direct underlying-library imports.
3. **Given** a reviewer reads a migrated file, **When** they look for `Result`-returning constructors and typed primitive composition, **Then** every rendering path uses them — no direct passthrough into the underlying library at any depth.

---

### User Story 3 — Maintainer can point new contributors at the REPL as the canonical example of using the framework (Priority: P2)

The Fugue REPL becomes the working example of how to use the in-repo adapter and Layout framework in a real application. A maintainer answering a question on the framework's intended usage can point at any of the previously-coupled files and say "here is the production pattern", knowing the answer matches what the framework's author intended.

**Why this priority**: This is the dogfooding payoff. Once the framework is consumed by its own author in production, all the framework's design decisions get pressure-tested by actual use — gaps in the API, missing primitives, awkward composition patterns surface as friction at the call site, and the framework gets better in response. Without dogfooding, the framework stays an academic exercise.

**Independent Test**: A maintainer takes a hypothetical contributor question ("how should I render a labelled bordered region containing a status bar and a streaming content area?") and points at exactly one file in the REPL source tree where the answer is implemented in production. The maintainer's pointer takes under 30 seconds to find.

**Acceptance Scenarios**:

1. **Given** a contributor asks "how do I render X with the framework", **When** the maintainer responds, **Then** they can point at a specific file and line range in the REPL source that demonstrates the answer.
2. **Given** the framework's author reviews the migrated code, **When** they notice an awkward call-site pattern, **Then** they can confidently file a framework-side issue with the production call site as the evidence (rather than a synthetic test case).

---

### User Story 4 — At least one currently-open rendering bug becomes closer to fixable through the migration (Priority: P2)

A Fugue maintainer who has been carrying open rendering-bug tickets for months — scroll-region reset on refresh, model description overlapping the input prompt, multi-line output corrupting the ReadLine prompt area — picks one of these tickets after the migration and finds that the fix is now substantially simpler. Either the bug no longer reproduces (the migration eliminated its root cause), or it is now reproducible in a small, comprehensible block of code where the fix is straightforward.

**Why this priority**: This is the second dogfooding payoff and the one that tightens the loop between framework and product. The legacy code path's complexity was hiding rendering bugs in a way that made them expensive to fix. After the migration, the same bugs become tractable. Phase 4 does not commit to fixing all such bugs, but does commit to enabling at least one to either auto-close or become substantially simpler.

**Independent Test**: A maintainer selects one of the three documented currently-open rendering bugs (the scroll-region reset, the model-description overlap, the multi-line corruption), and either confirms the bug no longer reproduces post-migration, or demonstrates a fix in fewer than 30 lines of code in a single file.

**Acceptance Scenarios**:

1. **Given** the migration is complete, **When** the maintainer attempts to reproduce one of the three target rendering bugs, **Then** either the bug is gone, or the bug is now reproducible with a clear, localised path to a fix.
2. **Given** the maintainer attempts a fix, **When** they measure the fix's footprint, **Then** the fix touches at most one file and contains at most thirty lines of changed code.

---

### User Story 5 — Maintainer removes the legacy-render fallback once production confidence is established (Priority: P3)

A safety-net rollback path exists from the previous phase — an environment variable that lets a Fugue user fall back to the pre-migration rendering. After production confidence is established (a soak period during which no rendering regression is reported), the maintainer removes this fallback path entirely, simplifying the REPL code by ~50 lines and removing a permanent maintenance burden.

**Why this priority**: Lower priority because removing the fallback is the *last* step, contingent on the migration being demonstrably stable. Removing it too early forces re-introduction at the next regression; removing it too late accumulates code rot. The right time is "after the migration has shipped and at least one full release cycle has passed with zero rendering regressions reported".

**Independent Test**: A maintainer searches the REPL source for the documented fallback environment variable's name and finds zero matches.

**Acceptance Scenarios**:

1. **Given** the migration has been on `main` through one full release cycle with no rendering regressions reported, **When** the maintainer searches the REPL source for the legacy-render fallback environment variable, **Then** zero matches are returned.
2. **Given** the fallback is removed, **When** a user sets the environment variable to its previously-meaningful value, **Then** the variable is ignored (no error, no fallback) — the new path is the only path.

---

### Edge Cases

- **Streaming markdown response arrives in tiny incremental token bursts**: the migration must not introduce flicker, perceptible stutter, or full-frame redraws between tokens. The rendered text must accumulate as smoothly as it did pre-migration.
- **Terminal is resized mid-session** (SIGWINCH): the new rendering paths must reflow content correctly on the very next frame, with no torn output or stale dimensions used for one frame.
- **Output redirected to a non-TTY** (pipe to file, CI log capture): the new rendering paths must degrade gracefully — no ANSI cursor positioning escapes that visibly garble the captured output, or, if they must be emitted, the output remains parseable as text.
- **Terminal is a "minimal" environment** (Windows console with limited colour, a remote terminal with reduced capability): the colour and styling degradation matches pre-migration — no new "missing colour" or "missing border-char" regressions.
- **A tool error occurs mid-stream**: the rendered exception output must not corrupt the prompt area, the status bar, or the surrounding rendered content. This is the documented multi-line-corruption bug class.
- **The user types `/turns`, navigates with arrows, and presses Enter quickly enough to race the renderer**: the picker must not double-render or leave artefacts of the previous selection on screen.
- **The user runs the REPL on a very narrow terminal** (≤ 40 columns): layout primitives must degrade in a defined way — wrap text, truncate borders, or omit non-essential regions — rather than render off-screen or throw.
- **A long, hot session accumulates many rendered frames**: there must be no slow memory growth attributable to the new rendering paths (e.g. retained composition trees, unbounded scheduler queues).

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Every rendering call site in the eight previously-coupled REPL source files MUST be replaced with a call into the in-repo F# adapter API (typed primitives or Layout primitives); no direct call into the underlying terminal-rendering library MAY remain in those files after the migration.
- **FR-002**: A text-search for the underlying library's namespace across the REPL source tree MUST return zero matches, with the documented exception of two designated bridge files that legitimately import the adapter.
- **FR-003**: The Fugue REPL's user-visible output MUST be visually equivalent before and after the migration when exercised through a canonical scripted session (defined: at least 3 user messages, at least 1 tool invocation that produces a result, at least 1 streaming markdown response, at least 1 interactive picker invocation, at least 1 exception render).
- **FR-004**: Existing rendering performance characteristics MUST NOT regress: streaming token append latency, full-session memory growth, and full-frame redraw rate MUST remain within their pre-migration envelopes (defined: median per-frame render time MUST stay at or below the pre-migration baseline).
- **FR-005**: The single point of coupling from the framework package to a Fugue-internal surface library MUST be relocated out of the package and into a new dedicated bridge module inside the REPL source tree; the package's source MUST contain no project references to Fugue-internal code after this work.
- **FR-006**: The new bridge module MUST be tested (at minimum: one happy-path test and one error-path test for its public surface) as a precondition of the relocation merging.
- **FR-007**: A legacy-render fallback environment variable from the prior phase MUST be removed from the REPL source tree after one full release cycle has passed on `main` with no rendering regression reported.
- **FR-008**: At least one of three documented currently-open rendering bugs (scroll-region reset on refresh; model-description overlap with input prompt; multi-line output corrupting the ReadLine prompt area) MUST either be auto-closed by the migration or become fixable with a localised change of fewer than 30 lines in a single file.
- **FR-009**: The migration MUST NOT change the Fugue.Surface DrawOp interface; the Surface actor's contract remains stable and all migrated rendering paths flow through the existing `RawAnsi` constructor.
- **FR-010**: The full test suite MUST continue to pass with a total count at or above the pre-migration baseline on all three supported continuous-integration operating systems after the migration is merged.
- **FR-011**: The headless AOT-publish workflow MUST continue to produce clean publishes for all three supported runtime identifiers after the migration; no new trim or AOT warnings beyond the existing allow-listed set MAY appear.
- **FR-012**: A visual-regression detection mechanism MUST exist that can flag a rendering change before merge — exact mechanism is decided in the design phase, but the mechanism MUST be capable of catching the scenarios in the canonical scripted session (FR-003).
- **FR-013**: The migration MUST be sequenced as multiple pull requests, not a single big-bang merge, so that each PR is independently reviewable, each PR's blast radius is bounded, and any regression introduced by a single PR can be reverted without unwinding the entire migration.

### Key Entities

- **Rendering call site**: a location in the REPL source tree that currently constructs visual output. Phase 4 inventory: 28 such sites across 8 files (Surface.fs ×13, Render.fs ×5, MarkdownRender.fs ×3, DiffRender.fs ×2, StackTraceRender.fs ×2, Picker.fs ×1, StreamRender.fs ×1, Doctor.fs ×1).
- **Adapter API**: the in-repo F# wrapper over the underlying terminal-rendering library, comprising primitives (text, styled, colour, alignment, border) and the Layout framework primitives (Stack, Dock, Grid, Flex) plus a Scheduler.
- **Bridge module**: a new dedicated F# module inside the REPL source tree that owns the one remaining mapping from the adapter's abstract output (rendered ANSI) to the Fugue-internal draw-op representation consumed by the Surface actor.
- **Canonical scripted session**: a fixed sequence of REPL inputs and assertions that exercises the spectrum of rendering paths the migration touches; used as the visual-parity probe in FR-003 and FR-012.
- **Legacy-render fallback**: an environment variable left in the REPL source by the prior phase that allowed a rollback to the pre-migration rendering paths; scheduled for removal under FR-007.
- **Target rendering bug**: any one of three named open bugs (scroll-region reset, model-description overlap, multi-line corruption) — at least one of which the migration must auto-close or substantially simplify (FR-008).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A search for the underlying terminal-rendering library's namespace across the REPL source tree returns exactly zero matches outside the two designated bridge files.
- **SC-002**: A canonical scripted REPL session run against the post-migration binary produces output visually equivalent to the same session run against the pre-migration binary, with differences limited to genuinely-volatile content (timestamps, request identifiers).
- **SC-003**: The full repository test suite passes with a total count greater than or equal to the pre-migration baseline on all three supported continuous-integration operating systems.
- **SC-004**: The headless AOT-publish workflow completes cleanly for all three supported runtime identifiers, with no new trim or AOT warnings introduced.
- **SC-005**: The legacy-render fallback environment variable is removed from the REPL source tree after the soak period; a search for its name returns zero matches.
- **SC-006**: The framework package's source contains no project references to Fugue-internal code; the relocated bridge module is the only remaining mapping and lives entirely outside the package.
- **SC-007**: At least one of the three target rendering bugs is closed during the migration or in a follow-up pull request whose diff is fewer than 30 lines in a single file.
- **SC-008**: A new contributor reading any one of the eight migrated files can describe its rendering behaviour using only adapter API terminology, without needing to know the underlying library's idioms — verified by an ad-hoc onboarding check after the migration.
- **SC-009**: The migration ships as at least three distinct pull requests (per FR-013), each independently reviewable, each with a green continuous-integration run, each merged separately to `main` with a merge-commit history.

## Assumptions

- **Streaming-update model**: The Layout framework's existing frame-coalescing scheduler (running at the established interactive rate) handles streaming-token append by re-rendering the relevant region on each tick. The scheduler was built for exactly this case in the prior phase; adding new accumulation primitives is not warranted before the scheduler-based approach is shown to be insufficient. If streaming smoothness regresses under this assumption, the migration's design phase will revisit it.
- **Surface DrawOp ABI**: The migration retains the existing `RawAnsi`-constructor-only flow into the Surface actor. Extending the actor's input shape to accept the framework's abstract composition type is an optimisation not yet justified by consumer demand; the abstract composition is rendered to its ANSI representation immediately before the actor boundary.
- **Visual-regression detection mechanism**: The default mechanism is snapshot-style approval testing on a small number of canonical frames (the canonical scripted session output stripped of volatile content), captured from the pre-migration binary and asserted against post-migration output during continuous integration. A heavier-weight pseudo-terminal harness is deferred to a follow-up unless the snapshot approach proves inadequate during design.
- **Migration sequencing**: The migration ships as multiple cohesive pull requests grouped by call-site cohesion (per FR-013); the precise grouping is decided in the planning phase. The design phase considers at minimum: separate pull requests for the Surface actor, the boot/diagnostic surfaces, the streaming-markdown surfaces, the secondary rendering surfaces, and the cleanup (legacy-render removal). A single big-bang pull request is rejected up front for its review burden and blast radius.
- **Soak period for fallback removal**: One full Fugue release cycle without a rendering regression report on `main` is the soak period before removing the legacy-render fallback. The release cycle is the existing Fugue product cadence, not a Phase-4-specific timeline.
- **Performance baseline**: The Phase-3 benchmark established that the framework's render path completes well within an interactive-rate budget for a typical Fugue UI tree. That baseline is the reference for FR-004; the migration is expected to consume that budget, not exceed it.
- **Scope of "the eight files"**: The eight files are the documented inventory at the time of this specification. If the source tree changes between specification and implementation (a new render file appears, a file is split), the principle in FR-001 and FR-002 applies to the actual then-current inventory, not the documented list.
- **No new user-facing features**: The migration neither adds nor removes any user-visible capability. Any apparent capability change observed during review is a regression, not a feature, and must be reverted.
- **Bridge module testing scope**: Tests for the new bridge module cover its public surface (happy + error paths per the project's testing principle); they are not expected to re-cover the framework's already-tested rendering behaviour.

## Dependencies

- **Internal — completed**: The Layout framework primitives (Stack, Dock, Grid, Flex), the framework's scheduler, the framework's typed primitives, and the framework's existing test suite are all complete from the prior phase. The migration consumes them; it does not modify them.
- **Internal — out of scope but enabled by this work**: The deferred NuGet-publication feature (`specs/004-publish-adapters-console-nuget/`) has a precondition that the framework is dogfooded in production. The migration satisfies that precondition as a side effect, but the publication itself is not part of this feature.
- **External**: No external dependencies introduced or changed. The underlying terminal-rendering library is already a project dependency; this feature does not add or upgrade it.

## Out of Scope

- Visible UX changes (colours, layout, spacing, command behaviour) — explicitly forbidden by FR-003 and the assumption section.
- Performance optimisation of the new rendering paths beyond preserving the pre-migration envelope — speed-ups are welcomed but not required.
- Changes to the Surface actor's interface or to the underlying terminal-rendering library's version.
- Publishing the framework as a redistributable package (deferred feature; tracked separately).
- Adding a pseudo-terminal-based integration test harness (a separate, larger infrastructure investment; deferred unless the default visual-regression approach proves inadequate).
- Fixing all three documented rendering bugs (FR-008 commits to at least one; the others remain in the backlog).
- Onboarding documentation for the framework beyond the in-source examples that the migration itself produces.
