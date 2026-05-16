# Feature Specification: F# Console Adapter Library

**Feature Branch**: `001-console-adapter-lib`

**Created**: 2026-05-15

**Status**: Draft

**Input**: User description: "нужна библиотека-обертка над C#-либой для консоли — на которую потом перепишем потом текущую реализацию. Либа — отдельный FSPROJ"

## User Scenarios & Testing *(mandatory)*

### User Story 1 — Port an existing rendering site without ceremony (Priority: P1)

A Fugue contributor is changing one of the existing rendering call-sites that
today reaches directly into the C# console rendering dependency (mutable
builder pattern, throwing methods, nullable references). They want to swap
that call-site over to the new F# adapter library and keep the surrounding
F# logic — pipelines, `Result` chains, DUs — unchanged.

**Why this priority**: The user explicitly framed the library as *"на которую
потом перепишем текущую реализацию"* — the *point* of the library is to be a
drop-in target for existing rendering code. Without P1, the library has no
adoption path and Principle II of the constitution stays unsatisfied.

**Independent Test**: A contributor takes one existing rendering call-site
(for example, a status-bar paint or a banner render), rewrites it to use the
new adapter, and the resulting code (a) compiles with `TreatWarningsAsErrors`,
(b) produces the same visible output in the terminal, (c) contains zero
references to the underlying C# library's types.

**Acceptance Scenarios**:

1. **Given** a call-site that today uses the underlying C# library's
   `Markup`, `Panel`, or `Padder` types directly, **When** a contributor
   rewrites that call-site against the new adapter, **Then** the surrounding
   F# file contains zero `open`s and zero type references pointing into the
   underlying C# library.
2. **Given** a call-site that today throws on invalid markup input, **When**
   the contributor rewrites it against the new adapter, **Then** invalid
   input is returned as `Result.Error` (or `Option.None`) rather than as a
   runtime exception.
3. **Given** the rewritten call-site is committed, **When** the suite is run
   and the binary is launched, **Then** the terminal output is visually
   indistinguishable from the pre-port output (same characters, same
   styling, same layout).

---

### User Story 2 — Add a new rendering site without learning the C# library (Priority: P2)

A Fugue contributor — possibly someone new to the project — needs to render
something new (a notification panel, a diff view, a list with selection).
They open the adapter library and find an idiomatic F# API: composable
primitives, `Result`-shaped fallible operations, no exposed C# types. They
build the new render entirely against the adapter and never have to read
the underlying C# library's documentation.

**Why this priority**: P2, not P1, because the immediate value is unlocking
the *port* of existing sites (P1). Greenfield rendering is the next-quarter
value once the adapter is established. But it is still a first-class user
story because it determines the *shape* of the adapter API.

**Independent Test**: A contributor with no prior exposure to the underlying
C# library can write a new rendering function using only the adapter and the
adapter's documentation/types — measured by absence of any `open` or fully
qualified reference to the underlying C# library in their diff.

**Acceptance Scenarios**:

1. **Given** the contributor needs to render text with foreground colour
   and bold styling, **When** they consult the adapter API, **Then** they
   find a typed primitive that expresses style as an F# record/DU rather
   than as a markup string.
2. **Given** the contributor needs to compose layout (two columns, padded
   panel, stacked rows), **When** they consult the adapter API, **Then**
   they find pipelineable composition functions, not a mutable builder.

---

### User Story 3 — Catch regressions when the C# dependency upgrades (Priority: P3)

A Fugue maintainer bumps the underlying C# console library to a new
version (security fix, new release, breaking change in a transitive
dependency). They want a single, fast test pass to surface any behavioural
regression in the rendering layer before the change lands on `main`.

**Why this priority**: P3 because it only triggers on dependency upgrades,
which are infrequent. But it directly satisfies the integration-test arm of
Principle II (NON-NEGOTIABLE) — without it, the library exists but its
guard-rail does not.

**Independent Test**: The maintainer bumps the underlying C# library to a
new patch/minor version in isolation, runs the adapter's integration test
suite, and the suite either passes (cleared to merge) or fails with a
specific diff pointing at the affected primitive (cleared to investigate).

**Acceptance Scenarios**:

1. **Given** the integration test suite exists, **When** the underlying C#
   library version is bumped, **Then** every public adapter primitive is
   exercised at least once by an integration test that hits the real
   library (no mocks at the boundary).
2. **Given** the underlying C# library introduces a behavioural change
   (e.g., a different default colour, a changed padding character), **When**
   the integration suite runs, **Then** it fails with output that names the
   affected primitive.

---

### Edge Cases

- **Width 0 or negative**: rendering primitives MUST behave deterministically
  when terminal width is reported as zero, negative, or smaller than the
  content's intrinsic minimum (typical in pipe-redirected or detached-terminal
  contexts). The adapter MUST NOT throw; it MUST return a defined fallback
  (clipped output or `Result.Error`).
- **Disabled colour**: when the host disables colour (NO_COLOR env var,
  monochrome theme), the adapter MUST emit no escape sequences for colour
  while preserving structural primitives (panel borders, padding, layout).
- **Concurrent renders**: the adapter MUST tolerate being called from
  multiple F# call-sites in interleaved order without producing corrupted
  output (today's surface achieves this through a serialising actor;
  the adapter MUST NOT break that property).
- **Streaming partial input**: when called with text that arrives chunk
  by chunk (e.g., LLM streaming), the adapter MUST not assume completeness
  — partial markup must either render incrementally or return
  `Result.Error` predictably, never crash.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The adapter MUST be packaged as a separate F# project
  (`.fsproj`) that produces a standalone library assembly. It MUST be
  buildable in isolation without dragging in the existing REPL projects.
- **FR-002**: The adapter MUST expose an idiomatic F# API for terminal
  rendering — primitives (text with style, line break, raw markup),
  layout (panel, padded box, columns/rows), and inline composition that
  reads like an F# pipeline rather than a mutable builder.
- **FR-003**: No public type in the adapter's API surface MAY expose a
  type defined by the underlying C# library. Caller F# code MUST be able
  to import the adapter without taking an `open` on the C# library.
- **FR-004**: Every operation that can fail (invalid markup, unsupported
  terminal capability, render-time IO error) MUST surface failure as
  `Result.Error` or `Option.None` — never as a raised exception across
  the adapter's API boundary.
- **FR-005**: Every public primitive in the adapter MUST have an
  integration test that exercises it against the real underlying C#
  library (no mocks at the boundary, per Principle II). The integration
  suite MUST be runnable in isolation as `dotnet test` against the
  adapter's test project.
- **FR-006**: The adapter MUST preserve the existing project's terminal
  rendering parity: the same characters, styling, and layout that today's
  rendering produces MUST be reproducible through the adapter (verified
  per User Story 1's Acceptance Scenario 3).
- **FR-007**: The adapter MUST honour the host's colour-enabled flag and
  emit no escape sequences for colour when disabled (Edge Case: Disabled
  colour).
- **FR-008**: The adapter MUST tolerate degenerate terminal dimensions
  (width 0, negative, smaller than content minimum) without throwing
  (Edge Case: Width 0 or negative).
- **FR-009**: The adapter MUST coexist with the existing serialising
  writer actor — calls through the adapter MUST land at the actor's
  serialisation point, not bypass it. Concurrent renders MUST NOT
  interleave at the byte level.
- **FR-010**: The adapter project MUST live in the JIT-only closure
  (per Principle V). It MAY use reflection, dynamic dispatch, or full
  features of the underlying library that are incompatible with AOT
  trimming.
- **FR-011**: The adapter MUST be referenceable as a project reference
  from the existing REPL project, enabling incremental port (one
  call-site at a time) without forcing a flag-day cutover.
- **FR-012**: The adapter MUST ship with a short developer-facing
  quickstart that maps the most common existing rendering calls to
  their adapter equivalents — measured by completeness against the
  inventory of present-day rendering sites (per Success Criterion SC-005).

### Key Entities *(include if feature involves data)*

- **Render primitive**: the smallest typed unit of output the adapter
  exposes (e.g., styled text, line break, raw escape sequence). Each
  primitive carries an explicit style value and an explicit content payload.
- **Render composition**: a typed value combining primitives (panel
  wrapping content, padded box, column/row layout). Compositions are
  immutable values, not mutable builders.
- **Render context**: an immutable record describing the host terminal
  (width, colour-enabled, theme name) that an outer rendering pass
  threads through the adapter. The adapter does not own global console
  state; it reads from the context it is given.
- **Render outcome**: the typed result of evaluating a render — either
  a success carrying the produced byte/escape stream (handed off to the
  writer actor), or a typed error.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A contributor familiar with idiomatic F# but not with the
  underlying C# library can write a new rendering function for Fugue
  using only the adapter and its docs, in under 30 minutes, without
  consulting the underlying library's documentation.
- **SC-002**: At least 80% of existing rendering call-sites in the
  current REPL surface (counted at project start) can be ported to the
  adapter without changing visible terminal output. The remaining ≤20%
  are documented as out-of-scope edge cases (with reasons) rather than
  left as unexplained gaps.
- **SC-003**: Integration tests covering the adapter's public surface
  finish in under 30 seconds on a developer laptop and run in CI on
  every PR that touches the adapter project.
- **SC-004**: Upgrading the underlying C# library by one patch version
  takes less than one developer-hour from PR open to merge when no
  behavioural change is present (the test suite is the gate, not manual
  smoke).
- **SC-005**: A "from old to new" mapping document exists at the
  adapter project's root and covers 100% of the rendering call patterns
  observed in the existing codebase at the time the adapter ships v1.
- **SC-006**: After the first wave of ports lands, zero F# source files
  in the REPL surface contain an `open` on the underlying C# library
  except inside the adapter project itself. (Inventory check; gates
  follow-up port waves.)
- **SC-007**: Catching a behavioural regression from a hypothetical
  dependency upgrade (verified via a deliberately seeded regression in
  a dry-run) succeeds — the integration suite fails with output naming
  the affected primitive within one developer-minute of running the test.

## Assumptions

- The "C# console library" the user named is the dependency the existing
  REPL surface uses today for styled text, layout primitives, and markup
  rendering. The adapter targets *that specific dependency* as its
  initial wrapped surface; other C# dependencies (markdown parsing,
  command-line parsing) are explicitly out of scope for v1 and will be
  wrapped by their own adapters, each in a separate file/project per
  Principle II ("one adapter per dependency").
- The adapter project is JIT-only (Principle V). It will not appear in
  the AOT headless closure and is not subject to trim-warning discipline.
- The existing serialising writer actor stays as-is; the adapter renders
  *through* it (producing the byte/escape stream the actor today
  receives), not around it. The actor's responsibility for ordering and
  cursor save/restore is unchanged by this work.
- "Port the current implementation onto the new library" is a follow-up
  workstream, not part of this feature's scope. This feature delivers
  the library, the integration tests, and the migration mapping doc.
  Actual port PRs land later, one call-site or call-site cluster at a
  time, gated by visual parity (FR-006).
- The adapter assumes the host process retains ownership of `Console.Out`
  and of capability detection (colour-enabled, terminal dimensions).
  The adapter is a pure render layer; it does not perform IO probing.
- Renaming the new project: the `.fsproj` name is a planning-phase
  decision (`Fugue.Console` / `Fugue.Terminal` / similar) and not
  load-bearing for this spec. The spec is satisfied by any project
  name that meets FR-001 (standalone `.fsproj`, separate from the
  existing REPL projects).
