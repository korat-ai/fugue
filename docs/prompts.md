# Prompt Templates

Fugue executes slash commands in two modes: **code-driven** and **template-driven**.

Code-driven commands (`/clear`, `/new`, `/exit`, etc.) mutate session state or have rich side effects. Template-driven commands are pure prompts — no F# handlers, no binary bloat, no release cycle needed.

When you run `/command-name <args>`, Fugue looks for a matching prompt template. Drop a markdown file in `~/.fugue/prompts/` and the command works immediately. This is the embodiment of the "prompt-first, code-last" principle: *Can this be solved by sending a smart prompt to the LLM with zero code? If yes, it's a template.*

## 19 Built-In Templates

| Command | Args | Description |
|---------|------|-------------|
| `breaking-changes` | `arg` | Detect breaking API changes and list call sites |
| `check-effects` | `file` | Verify ZIO/Cats Effect/Async type consistency |
| `check-tail-rec` | `file` | Detect non-tail-recursive functions and offer rewrites |
| `complexity` | `target` | Cyclomatic complexity analysis and refactoring hints |
| `derive-codec` | `typeName` | Generate AOT-safe STJ JsonSerializerContext |
| `idiomatic` | `arg` | Suggest idiomatic style improvements for the language |
| `incremental` | `desc` | Add a new function in-place without rewriting files |
| `infer-type` | `expr` | Infer and explain F# type signature of an expression |
| `migrate-oop-to-fp` | `file` | Convert OOP class hierarchy to F# DUs |
| `monad` | `desc` | Generate F# CE / Scala for-comprehension from pseudocode |
| `pointfree` | `fn` | Offer point-free rewrite of an F# function |
| `port` | `file, target` | Idiomatic cross-language code translation |
| `refactor-pipeline` | `label` | Rewrite F# let-bindings as \|> pipe chain |
| `rop` | `desc` | Generate ROP Result/Either chain from plain-English |
| `scaffold` | `arg` | Language-aware project scaffolding |
| `scaffold-actor` | `actorName` | Generate typed actor with command DU and supervision |
| `scaffold-cqrs` | `cmdName` | Generate CQRS Command/Handler/Event/Test slice |
| `scaffold-du` | `concept` | Generate F# DU with match examples and JSON attrs |
| `translate-comments` | `file` | Translate code comments to English |

## Adding a Custom Prompt Command

1. **Choose a kebab-case name** — e.g., `my-refactor`.

2. **Create the file** at `~/.fugue/prompts/my-refactor.md`:
   ```markdown
   ---
   command: my-refactor
   args: [file]
   description: Brief one-liner for /help
   ---
   Your prompt template body here.
   
   Use {file} and {code} for file path and contents.
   Placeholders are replaced with CLI arguments.
   ```

3. **Use it immediately** — no restart required. Run `/my-refactor <args>`.

4. **Restart or `/new`** to pick up new templates after creating the file.

## Template Format

Frontmatter (YAML-like, between `---` delimiters):

```
---
command: my-command
args: [arg1, arg2]
description: One-liner shown in /help and autocomplete
---
```

- `command` (required) — kebab-case identifier. Must be unique; overrides built-in if same name.
- `args` (required) — positional argument names, e.g., `[file]` or `[source, target]`. Can be empty: `[]`.
- `description` (required) — brief summary for CLI hints.

Body — plain markdown. Placeholders `{varname}` are replaced at dispatch time. Unknown placeholders are left literal (no error).

## Variable Substitution

### User-Provided Arguments

Arguments are positional. First arg goes to first placeholder, etc.

```markdown
---
command: explain
args: [error]
---
Explain this error in plain English:

{error}
```

Then: `/explain "cannot find type 'List'"` substitutes `"cannot find type 'List'"` for `{error}`.

### Special Variables: File-Reading Commands

**File-aware commands** (`check-tail-rec`, `check-effects`, `migrate-oop-to-fp`, `port`, `refactor-pipeline`) auto-populate:

- `{file}` — the file path argument (as passed)
- `{code}` — full file contents (auto-read from disk)

This lets templates operate on code without duplicating file-reading logic.

```markdown
---
command: check-tail-rec
args: [file]
description: Detect non-tail-recursive functions
---
Analyse this {file} for tail-recursion violations:

```{code}
```

For each non-tail-recursive function:
1. Quote the current impl.
2. Show the tail-recursive rewrite.
3. Explain why it matters.
```

Then: `/check-tail-rec src/Algorithms.fs` reads the file, injects both `{file}` and `{code}`, and sends the prompt.

**Convention:** If a template declares a `file` argument, Fugue's dispatcher checks if the command is in the file-aware list. If so, it reads the file and injects `{code}` automatically. This avoids repeated file-reading boilerplate in prompts.

### Missing Variables

If you reference `{foo}` but `foo` is not in the args list and not auto-injected, the literal `{foo}` stays in the prompt. This is intentional — useful for templates that want to show examples of placeholder syntax.

```markdown
---
command: doc
args: [name]
description: Generate API documentation
---
# {name}

The `{method}` method does X. Use it like:

```rust
let result = {example};
```
```

Running `/doc String` renders `{method}` and `{example}` literally (you'd fill them in the response). Only `{name}` is substituted.

## Overriding Built-In Templates

Place a file named `~/.fugue/prompts/scaffold-cqrs.md` with a different prompt body. When you run `/scaffold-cqrs`, Fugue uses your override instead of the embedded template.

Useful for team conventions: customize the wording, add your naming rules, or include boilerplate specific to your codebase.

## Environment Override: `FUGUE_PROMPTS_DIR`

By default, user templates live in `~/.fugue/prompts/`. Override with:

```bash
export FUGUE_PROMPTS_DIR="$PWD/.fugue-prompts"
fugue
```

Portable escape hatch for per-project templates. Tests and CI often set this to isolate template loading.

## Loading Order

1. **Embedded (built-in) templates** — compiled into the AOT binary as `EmbeddedResource`.
2. **User overrides** — `~/.fugue/prompts/*.md` (or `$FUGUE_PROMPTS_DIR`) shadow built-ins by command name.
3. **User-only templates** — files in the prompts dir that don't match a built-in command name. These extend the command set.
4. **Cached after first lookup** — subsequent `/help` or completions use the in-memory cache. `/new` (or restart) resets it.

If a file in the prompts dir lacks a valid `command` field, it is skipped with a warning to stderr.

## What This Does NOT Replace

The following commands remain hardcoded in F# because they have side effects or rich state management:

- **Session commands:** `/clear` (reset conversation), `/new` (start fresh session), `/squash` (compress history), `/scratch` (temp session).
- **Bookmarking:** `/bookmark`, `/notes`, `/snippet`, `/macro`.
- **Rich rendering:** `/diff` (visual diff), `/git-log`, `/issue`, `/watch`.
- **System controls:** `/exit`, `/help`, `/context`, `/tools`, `/tokens`, `/locale`, `/pin`, `@recent N`.

These commands require direct access to REPL state, file I/O, or terminal control. They cannot be templates.

## Worked Example: `/explain-error`

Say you want a command that takes a stack trace and asks the agent to explain it in simple terms.

Create `~/.fugue/prompts/explain-error.md`:

```markdown
---
command: explain-error
args: [trace]
description: Explain a stack trace or error message in plain English
---
A user encountered this error. Explain what went wrong and how to fix it.

Assume the reader has intermediate programming skills but may not know this specific codebase or library. Focus on:
1. What the error means (don't just repeat it).
2. The most likely root cause (show the code path if visible in the trace).
3. Two or three actionable fixes, ranked by likelihood.
4. Links to relevant docs if you know them.

Error or trace:

```
{trace}
```
```

Usage:

```bash
/explain-error "System.NotImplementedException: The method or operation is not implemented."
```

The agent sees:

> A user encountered this error. Explain what went wrong and how to fix it.
> ...
> Error or trace:
> ```
> System.NotImplementedException: The method or operation is not implemented.
> ```

The agent responds with a clear explanation of `NotImplementedException`, likely causes, and fixes.

## Template Caching and Reload

Templates are loaded and cached on first lookup (`PromptRegistry.all()`). If you add or edit a file in `~/.fugue/prompts/`:

- **New session** (`/new`) — resets the cache; new templates are picked up.
- **Restart `fugue`** — cache is cleared on startup.
- **Same session, existing file** — cache is not automatically invalidated. The old version stays until `/new` or restart.

This is intentional: prompts are rarely changed mid-session. If you want to test an edit, use `/new` to fork a new session.

## Cross-Reference: Hooks vs. Templates

**Templates** — slash-command dispatch; send a prompt to the agent; zero F# code; edit and reload at runtime.

**Hooks** — event system; run around tool invocation (`PreToolUse`, `PostToolUse`) or session lifecycle (`SessionStart`, `SessionEnd`); F# code required; recompile to change.

See `docs/hooks.md` for the hook system.

## Picking a turn interactively (/turns)

The `/turns` command opens an arrow-key navigable picker of every user and assistant turn in the current session. Each row shows:

```
[1] user   first 60 chars of the prompt …
[2] asst   first 60 chars of the response …
```

Navigate with arrow keys (Up/Down), then press Enter to dispatch `/show N` for the chosen turn number. This composes seamlessly with the `/show` prompt template from Phase 1 of issue #901. Press Esc to cancel the picker.

Data is read from the on-disk JSONL session file via SessionPersistence, so the picker reflects all turns appended during the current session. It works correctly even after history compaction, but only sees turns in the active session, not historical archived sessions.

## Troubleshooting

### Command not found

Run `/help` to list all registered commands. If your custom command is missing:

- Check the filename matches the `command` field in frontmatter.
- Verify the `command` field is valid (kebab-case, non-empty).
- Run `/new` to reload the template cache.
- Check `FUGUE_PROMPTS_DIR` — verify the dir exists and is readable.

### Placeholder not substituted

- Did you pass enough arguments? E.g., `/cmd arg1` when template expects `[arg1, arg2]`.
- Is the placeholder name spelled exactly as declared in `args`?
- Check for typos in `{varname}` — including the braces.

### Override not taking effect

- Filename must match `~/.fugue/prompts/{command-name}.md` exactly.
- Command name in frontmatter must match the filename stem.
- Run `/new` to clear the cache.

### File not read for file-aware command

File-aware commands (`check-tail-rec`, `check-effects`, `migrate-oop-to-fp`, `port`) expect a valid file path as the argument. If the file doesn't exist:

- `{code}` will contain the argument verbatim (not the file contents).
- `{file}` will be the literal path string.

Pass a relative path or absolute path that exists on disk for auto-read behavior.
