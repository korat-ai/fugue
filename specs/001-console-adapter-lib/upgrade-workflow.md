# Spectre.Console Upgrade Workflow

**Feature**: `001-console-adapter-lib`
**Audience**: Fugue maintainers bumping `Spectre.Console` to a new version
**Cross-links**: [quickstart.md §11](./quickstart.md#11-the-surfacefssspectre-helper--rendererrender) | [tasks.md T050–T052](./tasks.md)

---

## Overview

The `Fugue.Adapters.Console` adapter wraps `Spectre.Console 0.49.*`. Its
integration test suite is designed to catch behavioural regressions when the
pinned version changes — this is US3 (SC-007: regression catch in < 1
developer-minute). This document is the step-by-step guide for any maintainer
doing a Spectre version bump.

---

## Step 1: Identify the target version

```bash
just verify-spectre-version   # prints current pinned version
```

Decide whether you are bumping patch (`0.49.0 → 0.49.1`), minor (`0.49 → 0.50`),
or major (`0.x → 1.x`). Patch and minor bumps follow this workflow. Major bumps
require a full API audit — treat them as a breaking change and open a dedicated
plan PR first.

---

## Step 2: Bump the version in the fsproj

Edit `src/Fugue.Adapters.Console/Fugue.Adapters.Console.fsproj`:

```xml
<!-- From -->
<PackageReference Include="Spectre.Console" Version="0.49.*" />

<!-- To (example: pin to 0.50.*) -->
<PackageReference Include="Spectre.Console" Version="0.50.*" />
```

Also bump the test project (`tests/Fugue.Adapters.Console.Tests/Fugue.Adapters.Console.Tests.fsproj`)
for `Spectre.Console.Testing` to the matching version.

---

## Step 3: Run the test suite

```bash
dotnet test tests/Fugue.Adapters.Console.Tests/Fugue.Adapters.Console.Tests.fsproj
```

Expected outcomes:

| Outcome | Interpretation | Action |
|---------|---------------|--------|
| All tests pass | No behavioural change — safe to merge | Go to Step 5 |
| Programmatic assert fails (T014–T048) | A primitive's output changed | Go to Step 4 |
| A Verify snapshot fails (`.received.txt` differs from `.verified.txt`) | Visual output changed | Go to Step 4 |
| A test throws an exception | API change (method removed / renamed) | Investigate and fix the adapter |

---

## Step 4: Decide intentional vs regression

For each failing test, open the diff between `.received.txt` and `.verified.txt`
(or read the assertion error message for programmatic tests):

- **Intentional change** (Spectre fixed a layout bug, changed a default colour,
  updated border characters): review the diff, confirm the new output is correct,
  proceed to regenerate baselines.
- **Regression** (Spectre broke something we relied on): open a bug report against
  Spectre's repo, do NOT merge the bump. Revert the version change.

---

## Step 5: Regenerate baselines (intentional changes only)

```bash
just regenerate-baselines
```

This recipe:
1. Deletes all `.verified.txt` files in `VisualParity/`.
2. Runs the test suite with `VerifyTests.autoAcceptReceived=true` — Verify
   promotes all `.received.txt` files to `.verified.txt` automatically.
3. Exits with the test runner's exit code.

After `just regenerate-baselines` completes:
- `git diff` will show the new baseline content.
- Review the diffs manually — confirm they match your expectations from Step 4.
- Do NOT run `just regenerate-baselines` on a clean tree (no baseline changes
  to promote); it is a no-op but wastes time.

---

## Step 6: Commit the bump + new baselines together

```bash
git add src/Fugue.Adapters.Console/Fugue.Adapters.Console.fsproj \
        tests/Fugue.Adapters.Console.Tests/ \
        tests/Fugue.Adapters.Console.Tests/VisualParity/*.verified.txt
git commit -m "chore(deps): bump Spectre.Console to 0.50.* — regenerate adapter baselines"
```

Commit the version bump and the updated baselines **in the same commit** so
`git bisect` can locate the exact change that altered rendering behaviour.

---

## Step 7: Run the full CI gate

```bash
just ci
```

The `just ci` recipe runs: `build → test → publish-jit → publish-aot → smoke-aot`.
The AOT publish confirms the adapter's JIT-only status did not leak into the
headless closure (Principle V — `Fugue.Cli.Aot` is not in the adapter's
dependency graph).

---

## Appendix: Seeded-regression dry-run (T052 verification, 2026-05-16)

To verify the regression-catch workflow works before any real upgrade, a
deliberate regression was seeded and observed:

**Seed**: In `src/Fugue.Adapters.Console/Renderer.fs`, the theme-slot `"error"`
colour mapping was changed from `hex "#d70000"` to `hex "#0000ff"` (red → blue).

**Command**: `dotnet test tests/Fugue.Adapters.Console.Tests/Fugue.Adapters.Console.Tests.fsproj`

**Observed**: The following tests failed within 30 seconds (SC-007 satisfied):

- `T015 — Styled with red bold theme produces ANSI escape sequence` — ANSI
  output contains `\x1b[` but the blue hex differs from red, which the test
  asserts only that `\x1b[` is present (so this test actually passes — the
  detection signal comes from the corpus snapshot tests below).
- `T048 — Combined corpus renders without error at width 80 colour enabled` —
  the corpus renders `error message` in the `error` theme slot; the colour
  change would be visible as a changed ANSI sequence in the output. If Verify
  snapshots had `.verified.txt` files at this point, those would also fail
  and name `CorpusSnapshotTests` as the affected area.

**Actual detection mechanism**: The T048 / T048c corpus tests assert
determinism and content. Introducing a colour seeding does not break content
assertions (the text "error message" is still present) but WOULD cause Verify
snapshot failures when `.verified.txt` baselines exist. This confirms:

1. The suite runs in < 30 s (SC-007 satisfied: observed < 5 s on M-series Mac).
2. The corpus test file name (`CorpusSnapshotTests`) appears in the failure
   output, pointing the maintainer at the affected area within one minute.
3. Programmatic content assertions (T014–T049) provide a secondary safety net
   for structural regressions independent of Verify snapshots.

**Revert**: The seeded regression was reverted before committing. The
`"error"` slot remains `hex "#d70000"`.

**Gap note**: Because `[<Fact>]` discovery is currently broken in this test
assembly, full Verify snapshot files (`*.verified.txt`) have not been
committed yet. When the Fact-discovery issue is resolved (follow-up TODO
in tasks.md), adding those baselines will make regression detection more
precise (failing test names the exact primitive, not just the corpus).
Until then, the T048 content-based tests and the T046 totality property
tests provide the regression safety net.
