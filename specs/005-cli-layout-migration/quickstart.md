# Quickstart: Porting one source file from Spectre.Console to the Layout framework

**Feature**: 005-cli-layout-migration | **Audience**: subagent (or contributor) executing `/speckit-implement` for one PR cluster

This is the **template procedure** each migration PR follows. Each subagent dispatched against a PR cluster (P1–P4) repeats this procedure once per source file in its cluster.

PR P5 is removal-only and follows a different shorter procedure — see § "PR P5 procedure" at the end.

## Prerequisites

- Working directory: repository root (`/Users/roman/dev/Korat_Agent_clean/`).
- Branch: this PR's feature branch (e.g. `005-p2-boot-diagnostics`), already checked out off latest `main`.
- Tools available: `dotnet`, `rg`, `git`, `gh`, `fslangmcp` (for F# semantic queries).

## Per-file procedure

For each file in the PR's cluster (see `contracts/pr-sequencing.md` for the file list per PR):

### Step 1 — Inventory the call sites

Identify every direct Spectre.Console usage in the file. Use `fslangmcp` semantic search (per CLAUDE.md F# rules):

```text
mcp__fslangmcp__set_project   (once per session)
mcp__fslangmcp__fcs_file_outline  (for the target file)
mcp__fslangmcp__workspace_symbol   (search for "AnsiConsole", "Markup", "Panel", etc.)
```

Document each call site: line number, what it renders, what input data it uses.

### Step 2 — Identify the target adapter primitive

For each call site, decide the replacement shape:

- Plain text writes (`AnsiConsole.Write("hello")`) → `Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "hello"))`.
- Styled writes (`AnsiConsole.MarkupLine("[red]error[/]")`) → `Composition.Leaf (Primitive.Styled (Style.foreground Colour.red, SafeText.ofLiteral "error"))`.
- Boxed content (`AnsiConsole.Write(Panel(...))`) → `Composition.panel Border.Rounded label inner`.
- Multi-line vertical layout → `Stack.create Vertical 0 Start [...]` → `Composition.ofStack`.
- Status bar / fixed-edge regions → `Dock.create [Bottom, status; Fill, content]` → `Composition.ofDock`.
- Tabular data (`AnsiConsole.Write(Table(...))`) → `DataDisplays.Grid` or `Composition` with `Stack` of styled cells, depending on whether interactive features are used.
- Exception display (`AnsiConsole.WriteException(ex)`) → `Console.renderException settings ex` (already wrapped in adapter).

Consult `src/Fugue.Adapters.Console/Composition.fsi`, `Layout/*.fsi`, `DataDisplays.fsi`, and `Console.fsi` for the exact signatures.

**Streaming render paths only** (`MarkdownRender.fs`, `StreamRender.fs`): use the producer pattern from data-model.md §5 — a closure over mutable state that returns the current full `Composition`. Mount via `LayoutHost.mount producer 16`. Token arrivals call `Scheduler.trigger sched` after updating state.

### Step 3 — Replace call sites one at a time

For each call site:

1. Replace the Spectre call with the new adapter-based code.
2. If the call site emits to stdout directly (`AnsiConsole.Write(...)`), route through Surface actor: build `Composition`, then `RenderBridge.toDrawOp ctx comp |> Result.iter Surface.write` (or equivalent depending on whether the call site is in the main render loop or a one-shot).
3. Update any `open` declarations at the file top: replace `open Spectre.Console` with `open Fugue.Adapters.Console` (and `open Fugue.Adapters.Console.Layout` if Stack/Dock/Grid/Flex used).

After each call site is replaced, build:

```bash
dotnet build -c Release
```

Build must stay green. F# compiler will catch any type mismatch immediately (e.g. wrong `Composition` shape, missing `Result.bind` chain).

### Step 4 — Add snapshot test for the migrated path

For each new render path, add a snapshot test in `tests/Fugue.Tests/RenderSnapshotTests.fs`. Template:

```fsharp
[<Fact>]
let ``<source_file_basename> <scenario> renders identically to baseline`` () =
    let ctx = RenderContext.create 80 24 true "default" |> Result.unwrap
    let comp = <build the Composition for a known input>
    let actual = Renderer.toRawAnsi ctx comp |> Result.unwrap
    let expected = File.ReadAllText "Snapshots/<source_file_basename>_<scenario>.txt"
    Assert.Equal(expected, actual)
```

Run the test:

```bash
dotnet test --filter "<source_file_basename> <scenario> renders identically"
```

First run: test fails (no fixture). Inspect actual output:

```bash
dotnet test --filter "..." 2>&1 | grep -A 50 "Expected:"
```

Verify the actual output matches the pre-migration Spectre behaviour for the same input — run the pre-migration binary side-by-side (`git stash` → run binary → restore stash) for visual comparison.

If verified, save actual output to the fixture path (`tests/Fugue.Tests/Snapshots/<source_file_basename>_<scenario>.txt`). Commit the fixture in the same commit as the test.

Re-run the test:

```bash
dotnet test --filter "<source_file_basename> <scenario> renders identically"
```

Test passes byte-for-byte.

### Step 5 — Verify per-PR acceptance gates locally

Before pushing, run all gates from `contracts/pr-sequencing.md`:

```bash
# G1
dotnet build -c Release

# G2
dotnet test -c Release --no-build

# G3 (progress invariant — current PR's expected post-merge count)
rg -c 'Spectre\.Console|AnsiConsole' src/Fugue.Cli/*.fs \
    | grep -v 'RenderBridge\.fs' \
    | awk -F: '{ sum += $2 } END { print sum }'

# G4 — snapshot tests pass (covered by G2)

# G5 — manual smoke (run binary, do scripted session)
dotnet run --project src/Fugue.Cli
# ... follow the canonical scripted session steps from data-model.md §4

# G6
dotnet publish src/Fugue.Cli.Aot -c Release -r osx-arm64
```

Don't push until G1–G6 all pass locally. G7 (pre-push review) is the subagent dispatch; G8 (CI) and G9 (P5 soak only) happen after push.

### Step 6 — Pre-push review

Dispatch `engineering-code-reviewer` subagent on the staged diff with the PR's intent:

```text
Review this Phase 4 migration PR for the cluster <cluster-name>.
Scope: <N> call sites across <M> files migrated from direct Spectre.Console
to Fugue.Adapters.Console + Layout API. Focus on:
1. Every replaced call site preserves visual output (constraint FR-003).
2. SafeText.ofLiteral or equivalent wraps every user-content string
   (Constitution Principle X — adversarial content boundary).
3. No accidental new dependency on Fugue.Surface from the package
   (FR-005 / FR-014).
4. New snapshot fixtures are sensible (no all-whitespace fixtures, no
   visibly-wrong ANSI sequences).
5. The PR body's Test plan checkboxes are honest — each gate has been
   verified by the author, not just ticked.
```

Subagent returns findings. Any BLOCKER finding must be addressed before push. Non-BLOCKER findings go to follow-up issues (linked in PR body).

### Step 7 — Push + open PR

```bash
git push -u origin <branch-name>
gh pr create --title "feat(005-pN): migrate <cluster> to Layout framework (N/28 sites)" \
             --body "<filled-in PR body template from pr-sequencing.md>"
```

Wait for CI (G8). When all checks green and `mergeStateStatus` is `CLEAN`, merge:

```bash
gh pr merge <PR#> --merge --delete-branch
```

(Per Constitution VII: `--merge`, never `--squash`. Per CLAUDE.md: confirm with user before merging if non-trivial — for Phase 4 PRs the trust-but-verify pattern allows direct merge once gates pass, but if any G7 finding required nontrivial fix, surface to user first.)

### Step 8 — Sync main + update CLAUDE.md SPECKIT marker

```bash
git checkout main
git pull --ff-only
# Update CLAUDE.md SPECKIT marker per data-model.md §8
# Commit + push the CLAUDE.md update
```

The SPECKIT marker reflects the post-merge state: `N/28 sites remaining`, with the next PR's cluster called out.

## PR P5 procedure (cleanup, no migration)

PR P5 has no migration work — it removes the `FUGUE_LEGACY_RENDER` fallback.

### Step P5-1 — Verify soak conditions

- `git log --oneline main` shows ≥ 1 full Fugue release cycle has passed since PR P4 merge.
- `gh issue list -R korat-ai/fugue --search "rendering regression" --state open` returns zero issues filed since P4.
- Explicit user confirmation before push (CEO escalation per Constitution VII).

### Step P5-2 — Remove the fallback

- `rg "FUGUE_LEGACY_RENDER" src/` — identify every occurrence.
- Delete the env-var read in `Repl.fs`.
- Delete the legacy code paths in `Surface.fs` (and any other file that branches on `legacyRender`).
- `rg "FUGUE_LEGACY_RENDER|legacyRender" src/` — must return zero matches.

### Step P5-3 — Apply remaining gates

Run gates G1, G2, G3 (count unchanged from P4, validates no accidental re-introduction), G6, G7, G8.

PR title: `chore(005-p5): remove FUGUE_LEGACY_RENDER fallback (closes #962)`.

After merge, update CLAUDE.md SPECKIT marker to "Phase 4 COMPLETE" final state per data-model.md §8.

## Common pitfalls (observed in Phase 3 review BLOCKERs)

These are the patterns that caught 6 BLOCKERs across Phase 3 PRs. Apply preventively:

1. **Missing `.fsi` companion** for a new module → Constitution Principle Per-module sealing. Every new `.fs` MUST have an `.fsi` if it adds public symbols. Caught by PR review (Phase 3 P4 S-1 BLOCKER).
2. **Weak assertions in tests** ("contains X") → Constitution Principle I bans `result.IsOk`-only style; same logic applies to substring-only. Snapshot tests assert byte-for-byte (full output), not "contains".
3. **Silent error swallowing** (`try ... with _ -> ()`) → Constitution Principle II requires explicit Result mapping. Same in Phase 4: if a render call returns `Error`, propagate it or log via stderr — don't drop silently.
4. **`Spectre.Console` accidentally re-introduced via transitive use** → check the file's `open` statements at end of porting; `rg 'Spectre\.Console' <file>` must return zero.
5. **Fixture committed without visual verification** → reviewer cannot tell if the fixture's bytes are correct without comparison. Always run the pre-migration binary on the same input and compare visually before committing the fixture.
6. **Streaming render path not coalescing** → if the producer returns a new `Composition` reference on every call even when state hasn't changed, the scheduler's ref-equality short-circuit doesn't fire and you get full re-render on every tick. Test: assert that calling the producer twice with no state change returns identical references.

## Tool discipline (per CLAUDE.md global F# rules)

- F# semantic queries → `fslangmcp` (set_project, fcs_*, workspace_*, textDocument_*). Never `rg` / `grep` for F# symbol queries.
- `rg` is correct for `.fsproj`, `.md`, `.yml`, JSON, idiom counts.
- Every subagent end-of-run UX report on `fslangmcp` is mandatory (per CLAUDE.md). Anything actionable goes to `Neftedollar/FsLangMCP` issue #100 within the same turn.
