# Phase 0 Research: Full Spectre.Console F# Coverage

**Feature**: `002-spectre-full-coverage`
**Date**: 2026-05-16
**Status**: Phase 0 complete — no outstanding `NEEDS CLARIFICATION` markers.

This file resolves the five design questions surfaced during plan generation:
(R1) custom-renderable bridge shape, (R2) Live UI lifetime API,
(R3) interactive prompt API + cancellation, (R4) coverage matrix
generation strategy, (R5) `TestConsole` adapter wrapping.

Decisions here feed directly into Phase 1 (`data-model.md`,
`contracts/*.fsi`, `quickstart.md`) and the per-user-story task lists.
Every decision is traced back to its functional requirement (FR-008..FR-019)
and the constitutional principles it serves (especially I, II, III, V, X).

---

## R1 — Custom-renderable bridge shape (US1)

### Question
How do we let callers embed any externally-defined
`Spectre.Console.Rendering.IRenderable` into a `Composition` tree without
leaking the interface in public signatures (FR-008/FR-009)?

### Options considered

| ID | Shape | Sketch |
|----|-------|--------|
| **A** | New `Primitive.Renderable of <opaque>` DU case | Adds one case to existing `Primitive` DU; reuses `primitiveToRenderable` + `renderPrimitive` plumbing. |
| **B** | New top-level `Composition.Renderable of <opaque>` case | Treats the bridge as a layout-tree node, not a leaf. |
| **C** | Separate `Renderable` module + `Composition.ofRenderable` factory | New file `Renderable.fsi`/`fs`; `Primitive` and `Composition` unchanged. Opaque type `Renderable` constructed via `Renderable.fromSpectre : IRenderable -> Renderable` then injected via `Composition.ofRenderable : Renderable -> Composition`. |

### Decision

**Option C** — separate `Renderable` module with an opaque type and a
single `Composition.ofRenderable` factory. To keep the `Composition` DU
closed against external extension (Principle I — exhaustive matching
must still be mechanically provable inside the adapter), `ofRenderable`
lowers the opaque `Renderable` into a *private* DU case, e.g.
`Composition.Foreign of Renderable`. The case is `private` in `.fsi`,
constructible only via the factory.

### Rationale

1. **Phase 1 backward compatibility (FR-004)**. Options A and B both
   extend a public DU — every existing pattern match downstream becomes
   non-exhaustive at compile time. Option C adds one new private case
   that callers never write against; existing match arms outside the
   adapter touch neither `Primitive` nor `Composition`, so the 83
   existing tests stay green untouched (SC-005).
2. **Single-file ownership.** All the renderable-bridge plumbing
   (factory, exception catch, render path) lives in `Renderable.fs` /
   `Renderable.fsi`. The PR diff is a new file plus a one-line addition
   to `Composition.fsi` (`val ofRenderable : Renderable -> Composition`)
   and a new private case in `Composition.fs`. Reviewers see the bridge
   as a discrete unit, not as scattered DU edits.
3. **Exception boundary (FR-009)**. User-supplied `IRenderable` impls
   can throw arbitrarily. With a dedicated module we catch at exactly
   one point — the new arm in `Renderer.toRenderable` that handles
   `Composition.Foreign` — and convert to
   `Result.Error (RenderFailed (typeName, message))`. The `typeName`
   is captured at `Renderable.fromSpectre` call time
   (`r.GetType().FullName`) and stashed on the opaque value so the
   error message is informative even if the throwing renderable's
   `ToString` itself throws.
4. **Naming discipline (FR-002).** The factory's *argument* is
   `Spectre.Console.Rendering.IRenderable` — that's unavoidable, it's
   the very thing we're bridging. Its *return* is `Renderable`, an F#
   opaque type. The factory lives in a module clearly named
   `Renderable`; callers writing `Renderable.fromSpectre ir` are
   visibly opting into the one place Spectre is acknowledged at the
   call site. `rg 'Spectre\.' src/Fugue.Adapters.Console/*.fsi` still
   passes (SC-004), because the .fsi exposes only the abstract type and
   factory signature, not the underlying interface — see the
   double-blind type alias trick used in Phase 1's `Renderer.fs`
   (`type private IRenderable = Spectre.Console.Rendering.IRenderable`).
5. **Forward compatibility with "F# native renderables".** Spec
   Assumption explicitly defers "defining brand-new renderables in pure
   F# without referencing Spectre's interface" — that future story
   would add a second factory (e.g.
   `Renderable.fromDrawCallback : (RenderContext -> string) -> Renderable`)
   to the same module without disturbing the call sites that ported
   via `fromSpectre`. Options A/B would have to repaint the DU case to
   accept either source.

### Implications

- **data-model.md** gains a §"Renderable bridge" entity. Fields:
  underlying `IRenderable` (private), captured type name (string).
- **contracts/Renderable.fsi** exposes:
  ```fsharp
  type Renderable
  module Renderable =
      val fromSpectre : Spectre.Console.Rendering.IRenderable -> Renderable
  module Composition =
      val ofRenderable : Renderable -> Composition
  ```
  (`fromSpectre` is the single intentional leak — see FR-008's "single
  factory function" wording.)
- **Composition.fsi** gains a one-line `val ofRenderable` addition; the
  underlying `Composition.Foreign` case is `internal` so external matches
  cannot reference it. Existing matches remain exhaustive against the
  public DU shape.
- **Renderer.fs** gains a new arm in `toRenderable` matching
  `Composition.Foreign r` that unwraps the opaque value and runs Spectre
  with a try/catch around any `Render(...)`-time throw.

---

## R2 — Live UI lifetime API (US3)

### Question
What F# pattern do we use to drive Spectre `Live` / `LiveDisplay` /
`Status` / `Progress` sessions (FR-010, FR-011, FR-012)?

### Options considered

| ID | Shape | Public seam |
|----|-------|-------------|
| **A** | `IDisposable`-style session token | `Live.start ctx initial : Result<LiveSession, RenderError>` → `LiveSession.update`, `LiveSession.stop` (or `use`). |
| **B** | Scoped computation expression | `live ctx { initial composition; for token in stream do yield (compose token) }` |
| **C** | Action-callback (Spectre-native shape) | `Live.run ctx initial (fun session ctok -> async { ... })` — session lifetime is bound to the callback scope, matching Spectre's `Start`/`StartAsync(Func<LiveDisplayContext, Task>)`. |

### Decision

**Option C** — callback-scoped session. Public surface:

```fsharp
namespace Fugue.Adapters.Console

/// Session handle visible only inside the callback. Carries the
/// underlying Spectre context; cannot escape (no value of this type
/// can be stored outside the callback frame because the run function
/// never returns it).
type LiveSession

module LiveSession =
    /// Replace the live composition. Spectre refreshes on next tick.
    val update : LiveSession -> Composition -> Result<unit, RenderError>
    /// Force an immediate redraw (wraps Spectre's ctx.Refresh()).
    val refresh : LiveSession -> Result<unit, RenderError>

module Live =
    /// Run a live-updating composition for the duration of the callback.
    /// Cancellation tears down the cursor; concurrent runs on the same
    /// console return Error ConcurrentLiveSession.
    val run :
        ctx: RenderContext ->
            initial: Composition ->
            cancellation: System.Threading.CancellationToken ->
            body: (LiveSession -> Async<Result<'T, RenderError>>) ->
            Async<Result<'T, RenderError>>

module Status =
    val run :
        ctx: RenderContext ->
            spinner: SpinnerKind ->
            message: SafeText ->
            cancellation: System.Threading.CancellationToken ->
            body: (LiveSession -> Async<Result<'T, RenderError>>) ->
            Async<Result<'T, RenderError>>

module Progress =
    type ProgressTask
    val addTask : LiveSession -> SafeText -> Result<ProgressTask, RenderError>
    val increment : ProgressTask -> double -> Result<unit, RenderError>
    val run :
        ctx: RenderContext ->
            cancellation: System.Threading.CancellationToken ->
            body: (LiveSession -> Async<Result<'T, RenderError>>) ->
            Async<Result<'T, RenderError>>
```

### Rationale

1. **Spectre's own model is callback-scoped.** `AnsiConsole.Live(table).StartAsync(async ctx => ...)`,
   `AnsiConsole.Progress().StartAsync(async ctx => ...)`,
   `AnsiConsole.Status().StartAsync(...)` — every Live variant in 0.49
   takes a `Func<LiveDisplayContext, Task>` and never hands you a raw
   session token (verified via the Spectre 0.49 docs query). Mirroring
   that shape means our wrapper does not have to invent a session
   lifecycle that Spectre does not natively support; we wrap one
   callback in another and pass our typed `LiveSession` (which closes
   over the Spectre context) into the user's body.
2. **No leaked `IDisposable` (FR-010).** Option A would force us to
   manufacture a session that "lives past the StartAsync callback",
   which Spectre does not actually allow — the live region is torn down
   when StartAsync returns. We would either deadlock (block on a
   `TaskCompletionSource` until the user calls `stop`) or lie about
   when the cursor is restored. Option C cannot lie: the session is
   alive exactly while the callback runs, and torn down on return,
   matching Spectre's actual behaviour 1:1.
3. **Cancellation (FR-011) routes cleanly.** Spectre's Live/Status do
   not directly accept a `CancellationToken` in their StartAsync
   overloads (they accept `Func<LiveDisplayContext, Task>`); our
   wrapper accepts a token and wires it into the user's `Async` body
   via `Async.WithCancellation` (or `task { return! body session |> Async.AwaitTask } |> Async.StartImmediateAsTask(ct)`).
   On cancellation the `Async` throws `OperationCanceledException`,
   the callback returns to Spectre, Spectre restores the cursor — the
   adapter then catches the cancel and returns
   `Result.Error UserCancelled` to the caller.
4. **Single-session enforcement (FR-012).** Because the callback frame
   owns the session, we can guard with a per-console mutex: at entry
   to `Live.run` / `Status.run` / `Progress.run` we attempt to take a
   lock keyed by the underlying `IAnsiConsole` instance. If already
   held, return `Result.Error ConcurrentLiveSession` immediately,
   without entering the StartAsync. Lock released in `finally` block.
5. **Composition stays the unit of update (FR-010).**
   `LiveSession.update : LiveSession -> Composition -> Result<unit, _>`
   re-runs `Renderer.toRenderable` to produce a fresh `IRenderable`,
   stores it as the live target, then calls Spectre's `ctx.Refresh()`.
   This is the *only* place `Renderer` writes mid-flight; the docstring
   on `toRawAnsi` already notes "pure render" — Live carves out an
   explicit live-write path that is *not* `toRawAnsi`.
6. **What `Renderer.toRawAnsi` does inside a live session: nothing.**
   Live writes directly to the console (Spectre's own writer); the
   adapter's `Renderer.toRawAnsi` is not in the loop. This is worth
   documenting in `data-model.md` to head off confusion in PR review.

### Rejected alternatives

- **A (`IDisposable`/start-update-stop)** — looks F#-idiomatic but
  fights Spectre's actual lifecycle; would require either spinning a
  background thread driving StartAsync (concurrency + cancellation
  hazard) or blocking StartAsync until the caller signals stop
  (defeats the point of having a "start" returning a token).
- **B (computation expression)** — elegant for token-by-token streaming
  but constrains the body shape: anything the user wants to do that
  isn't "yield a new composition" (e.g. branching, exception handling,
  background tasks) is awkward inside a CE. Token streaming under C is
  a 3-line `for tok in stream do! LiveSession.update session (compose tok)`,
  which is plenty terse.

### Implications

- **data-model.md** gains §"Live session" entity. Lifetime: enters at
  callback entry, dies at callback exit. Single-per-console invariant
  enforced via private `ConditionalWeakTable<IAnsiConsole, obj>`.
- **contracts/Live.fsi** as sketched above. `SpinnerKind` is a closed
  DU mapping to Spectre's `Spinner.Known.*` registry (about 80
  built-in spinners; we wrap the ~10 most commonly used and document
  the policy for adding more on demand).
- **Errors.fs** gains `RenderError.UserCancelled` and
  `RenderError.ConcurrentLiveSession` (Phase 2 minor bump per FR-006).
- **contracts/Console.fsi** must expose a way for `Live.run` to receive
  a console — we settle that in R5 (TestConsole pulls double duty:
  prod console comes from `Console.default`, test console from
  `Console.test`).

---

## R3 — Interactive prompt API (US4)

### Question
For `TextPrompt` / `SelectionPrompt` / `MultiSelectionPrompt` /
`ConfirmationPrompt`: (a) async vs task return shape, (b) cancellation
strategy, (c) `Composition`-typed choice labels (FR-014),
(d) validator typing (FR-015).

### Options considered

- **Return shape**: (A) `Async<Result<'T, RenderError>>`, (B) `Task<Result<...>>`, (C) both (Async outward, Task inward).
- **Cancellation**: (X) pass `CancellationToken` straight into Spectre's `ShowAsync(IAnsiConsole, CancellationToken)`, (Y) wrap with `Async.StartAsTask` + `.WaitAsync(ct)` defensively, (Z) both — pass the token AND `Async.OnCancel` a no-op cursor restore in case Spectre swallows.
- **Choice label typing**: (P) plain string only, (Q) `Composition` (FR-014 mandates this), (R) `Composition` + display-string projection.
- **Validator typing**: (M) `string -> Result<'T, string>` returned from a typed F# function, (N) re-expose Spectre's `Func<T, ValidationResult>`.

### Decisions

- **Return shape: A — `Async<Result<'T, RenderError>>`.**
- **Cancellation: X with a Y fallback wrap.** Pass the token straight
  to `ShowAsync` (Spectre 0.49 supports it for all four prompt types
  per the docs). Additionally wrap with `Async.WithCancellationToken`
  so cancellation that fires *between* our call to construct the prompt
  and Spectre's `ShowAsync` still aborts. Convert
  `OperationCanceledException` and `TaskCanceledException` to
  `Result.Error UserCancelled` at the adapter boundary.
- **Choice label: Q — `Composition`.** Plus an internal display-string
  projection used when Spectre's IPromptChoice surface requires a
  string (see implementation note below).
- **Validator: M — typed F# `string -> Result<'T, string>`.**

### Rationale

1. **Async over Task**. F# idiom; matches what the rest of `Fugue.Cli`
   uses for its REPL loop (`Repl.fs` is `async {}` end-to-end). Inside
   the implementation we naturally call `Async.AwaitTask` on Spectre's
   `Task<T>` from `ShowAsync`. Option C ("expose both") doubles the
   public surface for no gain — interop callers can always wrap with
   `Async.StartAsTask` themselves; we ship the idiomatic shape.
2. **Cancellation strategy.** Spectre 0.49's `ShowAsync` overloads
   accept a `CancellationToken` — verified via docs query. So option X
   is viable as the primary path (no need for `WaitAsync` shim). The Y
   wrap is belt-and-braces: if Spectre's internal read-loop ignores the
   token while waiting on a blocking stdin read (a known issue in some
   prompt implementations on Windows ConHost), `Async.WithCancellationToken`
   will at least short-circuit the await on our side and we surface
   `UserCancelled` to the caller. The terminal-state recovery on
   forcible cancel is delegated to Spectre's own cleanup
   (`AnsiConsole.Cursor` restoration runs in its own `try/finally`
   inside `ShowAsync`); we do not duplicate cursor handling.
3. **Composition labels (FR-014).** Spectre's `IPromptChoice<T>` is
   internal and the public `SelectionPrompt<T>.AddChoice(T)` takes a
   bare T; the *display* of T is controlled by a `Converter : Func<T, string>`
   property. So our wrapper:
   - Accepts the user's choices as a `('T * Composition) list`
     (where `'T` is the *value* returned to the caller and the
     `Composition` is its *label*).
   - Internally calls `prompt.AddChoice(value)` with `value` as the T
     parameter, and sets `prompt.Converter <- Func<'T, string>(fun v -> renderToPlainString labelFor[v])`.
   - `renderToPlainString` calls `Renderer.toRawAnsi` with
     `ColourEnabled = false` for the conversion, OR with colour
     enabled — Spectre's `Markup` lines accept ANSI escapes only
     partially in selection lists; we test both and pick the variant
     Spectre accepts. (Test-time decision; doc what we picked in
     data-model.md.)
4. **Validator (FR-015).** Spectre's native validator is
   `Func<T, ValidationResult>`, where `ValidationResult` is success or
   error-with-string. Our typed F# shape:
   ```fsharp
   val textPrompt :
       prompt: SafeText ->
           validator: (string -> Result<'T, string>) ->
           ctx: RenderContext ->
           cancellation: CancellationToken ->
           Async<Result<'T, RenderError>>
   ```
   Internally we use `TextPrompt<string>` (the string-typed Spectre
   variant), then convert input via the user's `validator`:
   - Spectre's `Validate` arm runs validator(input); `Result.Ok _` →
     `ValidationResult.Success()`; `Result.Error msg` →
     `ValidationResult.Error(msg)`. Spectre re-prompts on Error using
     its native loop (FR-015 satisfied — we wrap, not reimplement).
   - On final success we run validator again to get the typed `'T`
     value (Spectre returned the raw string).
   - Two validator calls per successful prompt are a deliberate
     trade: the alternative is to use Spectre's `TextPrompt<T>` and
     supply a `TypeConverter<T>`, but that route serialises 'T through
     `CultureInfo` machinery and is hostile to F# DU values. The
     double-validate cost is negligible for human-typing-speed prompts.

### Rejected alternatives

- **N (re-expose Spectre's `ValidationResult`)** — violates FR-002
  (would put `Spectre.Console.ValidationResult` in our `.fsi`).
- **P (string-only labels)** — directly contradicts FR-014.
- **Z (replicate Spectre cursor handling)** — risk of double-cleanup
  bugs (cursor saved twice, restored once leaves terminal corrupted).
  Trust Spectre's `try/finally`.

### Implications

- **data-model.md** gains §"Prompt request" entity.
- **contracts/Prompts.fsi** with `textPrompt`, `selectionPrompt`,
  `multiSelectionPrompt`, `confirmationPrompt` as sketched above. All
  take `ctx: RenderContext`, `cancellation: CancellationToken`,
  return `Async<Result<'T, RenderError>>`. `NoInteractiveConsole`
  short-circuit lives in a `RenderContext.isInteractive` predicate
  (added in Phase 1 — verify, else add as part of Phase 2 P4).
- **Errors.fs** uses the same `UserCancelled` / `NoInteractiveConsole`
  cases added under R2.

---

## R4 — Coverage matrix generation (FR-018)

### Question
How do we enumerate every public type in `Spectre.Console 0.49.*` to
populate `coverage-matrix.md` and verify SC-001 (zero `pending` at PR
merge)?

### Options considered

| ID | Strategy |
|----|----------|
| **A** | Reflection-only: a test enumerates `typeof<Spectre.Console.AnsiConsole>.Assembly.GetExportedTypes()` and cross-checks against a hard-coded covered/excluded set. Doc generated by `dotnet fsi`. |
| **B** | Manual-only: hand-roll the matrix from Spectre source; CI greps for unknown types in `Spectre.Console.dll`. |
| **C** | Hybrid: hand-roll once, reflection test guards completeness on every build. |

### Decision

**Option C — hybrid.** `coverage-matrix.md` is hand-curated (humans
write the prose justifications); a single FsCheck/xUnit test
(`CoverageMatrixTests.fs`, FR-019) loads `Spectre.Console.dll` via
reflection, enumerates all public exported types, and asserts each one
appears in the matrix with status `covered` or `excluded`. Zero
`pending` rows are tolerated at PR merge (SC-001) — the test fails if
any are present.

### Rationale

1. **Humans write justifications, machines enforce completeness.** The
   matrix's *value* is the justification column ("excluded because
   Spectre internal", "covered by `DataDisplays.Tree`", etc.) — that
   prose has to be hand-written. The matrix's *correctness* (no type
   forgotten) is a pure enumeration check, mechanically verifiable.
   Splitting concerns matches Phase 1's discipline of "tests enforce
   invariants, docs explain them".
2. **Survives Spectre patch bumps.** When Spectre 0.49.x bumps to
   0.49.y and adds a new public type, the reflection test fails
   immediately with a clear message: "Type X not in coverage matrix".
   Maintainer opens `coverage-matrix.md`, adds a row with status
   `covered`/`excluded` + justification, and CI goes green. No silent
   drift between docs and reality.
3. **Format keeps reviewer overhead small.** The reflection test
   reports the *delta* (new types, removed types, types whose
   namespace changed) rather than re-printing the full matrix. Most
   patch bumps will produce zero-line diffs; the matrix only churns
   when Spectre genuinely changes its API.
4. **Namespace granularity.** Each row is one *type*, not one
   namespace. Rationale: a namespace (`Spectre.Console.Rendering`)
   often contains a mix of "wrap this" (`IRenderable` → bridge) and
   "internal helper, do not wrap" (`Segment`, `Measurement`) types.
   Per-type rows let us justify each independently. The matrix is
   sorted by namespace then type-name so reviewers can scan a single
   namespace's coverage at a glance.

### File format sketch

`specs/002-spectre-full-coverage/coverage-matrix.md`:

```markdown
# Spectre.Console 0.49.* Coverage Matrix

**Resolved version**: 0.49.1 (pinned in `Fugue.Adapters.Console.fsproj`)
**Last verified**: 2026-05-16 by `CoverageMatrixTests.``Every_public_type_is_accounted_for``

| Namespace                       | Type                  | Status    | Adapter wrapper / Justification                                 |
|---------------------------------|-----------------------|-----------|-----------------------------------------------------------------|
| Spectre.Console                 | AnsiConsole           | covered   | `Console.default` (P5)                                          |
| Spectre.Console                 | BarChart              | covered   | `DataDisplays.BarChart` (P2)                                    |
| Spectre.Console                 | BoxBorder             | covered   | `Composition.Border` DU (Phase 1)                               |
| Spectre.Console                 | BreakdownChart        | covered   | `DataDisplays.BreakdownChart` (P2)                              |
| ...                                                                                                                                |
| Spectre.Console                 | RecordedConsole       | excluded  | Test-utility shim; superseded by `Console.test` (P5)            |
| Spectre.Console.Advanced        | AnsiBuilder           | excluded  | Internal text builder; not needed at adapter level              |
| Spectre.Console.Rendering       | IRenderable           | covered   | `Renderable.fromSpectre` opaque bridge (P1)                     |
| Spectre.Console.Rendering       | Measurement           | excluded  | Internal layout struct; never returned across adapter boundary  |
| ...                                                                                                                                |
```

Status values:
- `covered` — has an adapter wrapper.
- `excluded` — explicit justification (one of: internal helper,
  superseded by Phase 1, obsolete in 0.49, unsafe in target environments).
- `pending` — WIP placeholder; **must be empty at PR merge** (SC-001).

### Implications

- **tests/Fugue.Adapters.Console.Tests/CoverageMatrixTests.fs** (new):
  ```fsharp
  [<Fact>]
  let ``Every public Spectre type is in the coverage matrix`` () =
      let asm = typeof<Spectre.Console.AnsiConsole>.Assembly
      let publicTypes = asm.GetExportedTypes() |> Array.map (fun t -> t.FullName)
      let matrixTypes = CoverageMatrix.load ()  // parses .md table
      let missing = publicTypes |> Array.filter (not << matrixTypes.Contains)
      Assert.Empty missing
  ```
- **CoverageMatrix module** (test-internal): small parser that reads
  the .md, extracts column 2 (type name) values, returns a `HashSet<string>`.
  Trivial to write; no extra NuGet deps.
- **FR-019 satisfaction**: a sibling test iterates the `covered` rows
  and asserts the named wrapper exists and produces non-empty output
  for a representative value. Wrappers are looked up via a hand-rolled
  registry (one entry per wrapper module — explicit, no reflection
  acrobatics).

---

## R5 — TestConsole adapter wrapping (US5)

### Question
How do we wrap `Spectre.Console.Testing.TestConsole` for the adapter's
deterministic-output capture (FR-017)?

### Options considered

| ID | Shape |
|----|-------|
| **A** | Public `TestConsole` record paired with `RenderContext`; new `Renderer.toRawAnsi` overload accepting a console sink. |
| **B** | Internal-only wrapper, used by the existing adapter test suite only. Production code unaffected. |
| **C** | Public `Console.test` factory returning an opaque wrapper with `capture : Composition -> string` and `cursorState : unit -> CursorState` for advanced assertions. Phase 1 tests don't migrate. |

### Decision

**Option C** — public `Console.test` factory returning an opaque
`TestConsole` wrapper. Production code uses `Console.default`. Phase 1
tests stay on their existing pattern (no migration required, SC-005).
The new Live/Status/Progress and Prompt tests (P3, P4) use
`Console.test` because direct-write paths need an `IAnsiConsole`
substitute the adapter controls.

### Public surface

```fsharp
namespace Fugue.Adapters.Console

/// Opaque handle to an active console (real or test).
type Console

module Console =
    /// Default production console: writes to stdout, respects terminal
    /// capabilities. Single shared instance.
    val default : unit -> Console

    /// Test console: captures all output to an in-memory buffer.
    /// Each call returns a fresh independent instance.
    val test : width: int -> colourEnabled: bool -> Console

    /// Render a Composition through this console's `Renderer.toRawAnsi`-
    /// equivalent path. For Console.default, writes to stdout. For
    /// Console.test, appends to the in-memory buffer.
    val write : Console -> RenderContext -> Composition -> Result<unit, RenderError>

    /// Test-only: drain the captured buffer (clearing it). Returns
    /// Result.Error InvalidArgument when called on Console.default.
    val capture : Console -> Result<string, RenderError>

    /// Test-only: snapshot the cursor position / scroll state.
    val cursorState : Console -> Result<CursorState, RenderError>
```

`Live.run`, `Status.run`, `Progress.run`, and the prompt wrappers
accept a `Console` parameter (not directly a `RenderContext`) so they
can be exercised against `Console.test` in unit tests without spinning
up a real terminal.

### Rationale

1. **Phase 1 tests don't need migration.** Today's tests render via
   `Renderer.toRawAnsi` which internally builds an ephemeral
   `AnsiConsole` per call (the `withSpectre` helper). That pattern
   works fine for *static* compositions. It does NOT work for Live or
   Prompts because those write directly to a long-lived console
   instance. So `Console.test` exists primarily to serve P3 + P4 (and
   anyone in P5 who wants the assertion power).
2. **TestConsole is real Spectre infrastructure**
   (`Spectre.Console.Testing.TestConsole`). We wrap it rather than
   reinventing because: (a) Spectre's own TestConsole already handles
   width clamping, cursor recording, ANSI capture; (b) reinventing it
   would mean rebuilding the parts of Spectre we're explicitly
   adapting; (c) `Spectre.Console.Testing` is a separate NuGet
   already referenced by the adapter test project (per spec
   Assumption). The wrapper is a one-liner internally:
   `type Console = private { Backend: IAnsiConsole; Mode: ConsoleMode }`
   where `Mode` is `Production | Test of TestConsole`.
3. **Opacity makes the test-vs-prod distinction load-bearing in the
   types.** `capture` and `cursorState` returning
   `Result.Error InvalidArgument` on a production console is the
   right shape: calling them is a test-code bug, not a runtime error
   condition. The opaque type prevents callers from accessing the
   underlying Spectre `TestConsole` to do something we don't intend.
4. **CursorState as its own DU.** P3 tests (cancellation leaves cursor
   clean) need to assert "cursor at column 0, no alt-screen active";
   without a typed `CursorState` record those assertions become
   awkward string matches against ANSI escape sequences. Cheap to
   provide; high leverage in tests.

### Rejected alternatives

- **A (`TestConsole` as a record paired with `RenderContext`)** —
  doubles the public surface (every `Renderer` function gains a
  console-aware overload). Option C subsumes A: `Console.write` is
  the new universal entry point, `Renderer.toRawAnsi` stays for pure
  string-out cases (Phase 1 backward compat).
- **B (internal-only)** — fails FR-017 ("adapter MUST expose a
  TestConsole wrapper"). The whole point is that downstream test code
  (e.g. `Fugue.Cli` REPL tests, future `Fugue.Surface` tests) can use
  it too.

### Implications

- **data-model.md** gains §"Console" and §"CursorState" entities.
- **contracts/Console.fsi** as sketched above.
- **Renderer.fsi / fs** unchanged in v1 of Phase 2 P5 — `toRawAnsi`
  and `toDrawOp` continue to work standalone for Phase 1 patterns.
  `Console.write` is implemented in `Console.fs` and internally calls
  the same `toRenderable` machinery (refactored to take an
  `IAnsiConsole` parameter rather than building one inside
  `withSpectre`; the new `withConsole : Console -> (IAnsiConsole -> unit) -> unit`
  helper supersedes `withSpectre` for the Live/Console paths).
- **Live/Status/Progress/Prompt wrappers** accept `Console` (not
  `RenderContext` alone) so they can target `Console.test` in unit
  tests.

---

## Summary table

| # | Question                                | Decision                                                                    |
|---|-----------------------------------------|-----------------------------------------------------------------------------|
| R1 | Custom-renderable bridge shape          | **C** — separate `Renderable` module + `Composition.ofRenderable` factory  |
| R2 | Live UI lifetime API                    | **C** — callback-scoped session via `Live.run` / `Status.run` / `Progress.run` |
| R3 | Interactive prompt API                  | **A + X + Q + M** — `Async<Result<_,_>>`, native CT, Composition labels, F# validator |
| R4 | Coverage matrix generation              | **C** — hybrid hand-curated matrix + reflection-based completeness test    |
| R5 | TestConsole adapter wrapping            | **C** — public `Console.test` factory returning opaque wrapper             |

## Open questions for `/speckit-clarify` (none — all resolved)

Every R has a single chosen option with rationale. No
`NEEDS CLARIFICATION` markers remain.

## Inputs to next phase

Phase 1 (`data-model.md`, `contracts/`, `quickstart.md`,
`coverage-matrix.md` scaffold) operates against:

- **R1** decision → `Renderable.fsi`, `Composition.fsi` (one new
  factory), `Composition.fs` (one new private case).
- **R2** decision → `Live.fsi` with `Live.run`/`Status.run`/`Progress.run`
  callback-scoped APIs; `Errors.fs` gets `UserCancelled` +
  `ConcurrentLiveSession` cases.
- **R3** decision → `Prompts.fsi` with four async wrappers; reuse the
  Errors cases from R2; add `NoInteractiveConsole` case.
- **R4** decision → `coverage-matrix.md` template (header + columns
  defined; rows fill in as P1..P5 PRs land);
  `CoverageMatrixTests.fs` test scaffold.
- **R5** decision → `Console.fsi` with `Console.default`/`Console.test`/
  `Console.write`/`Console.capture`/`Console.cursorState`; `CursorState`
  record in `Console.fsi`.

All five decisions preserve Phase 1 backward compatibility (SC-005).
None introduces any `Spectre.*` type into a public `.fsi` signature
(SC-004): the single intentional concession is `Renderable.fromSpectre`'s
*argument* type, which the spec explicitly carves out in FR-008
("via a dedicated factory function whose argument is the Spectre type
but whose return is the adapter's opaque wrapper").
