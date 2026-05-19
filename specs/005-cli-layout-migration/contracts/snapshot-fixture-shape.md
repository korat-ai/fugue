# Contract: Snapshot Fixture Shape

**Feature**: 005-cli-layout-migration | **Status**: design — to be implemented across PR P1–P4

Defines the format, location, naming, and lifecycle of snapshot fixtures used as the visual-regression-detection mechanism (FR-012 / R-3 decision).

## Location

```
tests/Fugue.Tests/Snapshots/
```

Single flat directory. If cross-platform divergence forces per-OS fixtures (see data-model §3 "Cross-platform consideration"), subdirectories appear (`macos/`, `linux/`, `windows/`); the default assumes uniform output and avoids the split.

## Naming convention

```
<source_file_basename>_<scenario>.txt
```

Where:

- `<source_file_basename>` is the migrated source file's name in snake_case, without the `.fs` extension. Example: `Surface.fs` → `surface`, `MarkdownRender.fs` → `markdown_render`.
- `<scenario>` is a short descriptor of the input the fixture covers, in snake_case. Examples: `status_bar`, `markdown_basic`, `streaming_complete`, `edit_diff`.

Examples (from data-model §3):

```
surface_status_bar.txt
surface_markdown_basic.txt
surface_diff_basic.txt
render_boot.txt
doctor_output.txt
markdown_streaming_complete.txt
diff_render_edit.txt
stacktrace_render_exception.txt
picker_turns_basic.txt
```

## Content format

- **Encoding**: UTF-8, no BOM.
- **Line endings**: LF (`\n`). Windows CRLF (`\r\n`) is FORBIDDEN — fixtures are stored canonically and compared literally.
- **ANSI escapes**: preserved literally as the bytes `\x1b[` (ESC + `[`) followed by the CSI parameters. No human-readable encoding.
- **Trailing whitespace**: preserved exactly as `Renderer.toRawAnsi` emits — do not normalise.
- **Final newline**: present if and only if `Renderer.toRawAnsi` emits one; do not add one for "POSIX file convention".

Fixtures are small (< 5 KB each; the largest expected — `surface_markdown_basic` — caps around 2 KB for an 80×24 region of styled markdown).

## Determinism preconditions

The fixture comparison is reliable only if these all hold:

1. **Context is constant per fixture**: the test that uses the fixture constructs `RenderContext` with explicit fixed values (`RenderContext.create 80 24 true "default"` is the canonical default). Tests MUST NOT call `RenderContext.probe ()` (which reads live terminal dimensions).
2. **Composition input is literal**: no `DateTime.Now`, no `Guid.NewGuid()`, no random IDs, no environment-variable values in the composition. If a render path genuinely depends on volatile state, the test passes a fixed mock value.
3. **Underlying renderer is deterministic**: validated by the framework's existing Phase 1+2 tests (260+ render-evidence assertions). If a snapshot fails for unclear reasons, suspect a non-determinism leak into the composition, not into the renderer.

## Lifecycle

### Creation (one-time per fixture)

```fsharp
// Step 1: Write the test with an empty expected fixture.
[<Fact>]
let ``surface status bar renders identically to baseline`` () =
    let ctx = RenderContext.create 80 24 true "default" |> Result.unwrap
    let comp = buildStatusBarComposition exampleStatusInputs
    let actual = Renderer.toRawAnsi ctx comp |> Result.unwrap
    let expected = File.ReadAllText "Snapshots/surface_status_bar.txt"
    Assert.Equal(expected, actual)
```

```bash
# Step 2: Run the test; observe failure (no fixture or empty fixture).
dotnet test --filter "renders identically to baseline"

# Step 3: Inspect actual output visually.
dotnet test --filter "..." 2>&1 | grep -A 100 "Expected:" | head -50

# Step 4: Verify the actual output matches pre-migration Spectre behaviour
# for the same logical input. (Manual: open the pre-migration binary, run the
# equivalent path, compare visually.)

# Step 5: If verified, save actual as the fixture.
# (In practice: copy the actual byte sequence to the .txt file.)
```

### Update (when the migration *intentionally* changes a fixture — e.g. closing bug #910)

1. Make the intentional change in the migrated source file.
2. Run the test; observe snapshot mismatch.
3. PR diff shows the fixture's `.txt` content changing byte-by-byte. Reviewer inspects.
4. Reviewer either approves (the change is the desired behaviour) or rejects (the change is an unintended regression).
5. If approved, commit the updated fixture alongside the source change.

### Permanent stability invariant

Once committed, a fixture changes only via the Update procedure above. A PR that silently regenerates fixtures without an intentional behaviour change in source MUST be rejected — that pattern is exactly the failure mode snapshots protect against.

## Test infrastructure

**No new library dependency** introduced. Tests use:

- `System.IO.File.ReadAllText` to load fixtures.
- `Xunit.Assert.Equal` for byte-for-byte comparison.
- Fixtures live as embedded test data (`<None Include="Snapshots/**" CopyToOutputDirectory="PreserveNewest" />` in `Fugue.Tests.fsproj`, or equivalent).

**Rationale for no library**: 9 fixtures + trivial format = adding `Verify` or `ApprovalTests.NET` is dependency overkill. Hand-rolled comparison is 3 lines per test, easy to read, easy to fail-message.

## Coverage commitments

Per FR-012 / SC-002, at minimum these scenarios MUST have fixtures by end of PR P4:

- Status bar render (Surface.fs).
- Markdown panel render (Surface.fs / MarkdownRender.fs).
- Diff panel render (Surface.fs / DiffRender.fs).
- Boot banner render (Render.fs).
- Doctor diagnostic render (Doctor.fs).
- Streaming markdown post-completion (StreamRender.fs / MarkdownRender.fs).
- Edit-tool diff render (DiffRender.fs).
- Exception render (StackTraceRender.fs).
- Picker render with selected row (Picker.fs).

Total: 9 fixtures, ≈ 20 KB. Added incrementally — each PR adds the fixtures for the files it migrates.
