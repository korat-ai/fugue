# Fugue

> A small, fast F# terminal coding agent. **Two binaries, two runtimes:**
> `fugue-aot` (Native AOT, ~40ms cold start) for CI / pipes / `--print`.
> `fugue` (JIT + ReadyToRun) for interactive REPL — long-lived, JIT warms once.
> Provider-agnostic (Anthropic / OpenAI-compat / Ollama).

**Repo:** `korat-ai/fugue` · **Status:** MVP shipped (Phase 1–5 closed, 2026-04-30) · **Default branch:** `main`

**🪡 AOT/JIT split locked 2026-05-02** — see [§ AOT vs JIT](#aot-vs-jit--two-binaries) below.  Trim/AOT discipline applies ONLY to `Fugue.Cli.Aot` and its closure. REPL code (`Fugue.Cli`, `Fugue.Surface`) is JIT-only — write idiomatic F# without trim ceremony.

---

## Who this is for

Polyglot backend / systems engineers on **macOS-arm64 and Linux-x64** working in **.NET, TypeScript, Java, Elixir, Scala, Kotlin** (and adjacent). The audience cares about: fast launch, low RSS, scriptable terminal UX, no Electron/Node baggage, native binaries shipped via release pages or `brew`. **Not** for developers who need a polished GUI or for users who prioritise the largest possible plugin ecosystem — pick Claude Code or VS Code Copilot for that.

## Architecture

```
src/
  Fugue.Core/         — domain types, config, localization, system prompt, JSON DTOs    [AOT-clean]
  Fugue.Agent/        — Microsoft.Agents.AI factory + streaming + provider Discovery     [AOT-clean]
  Fugue.Tools/        — Read / Write / Edit / Bash / Glob / Grep, no-reflection          [AOT-clean]
  Fugue.Cli.Aot/ — entry point for --print / pipe-input mode                        [AOT-only, single-file]
  Fugue.Surface/      — typed DrawOps + MailboxProcessor surface, MockExecutor           [JIT-only]
  Fugue.Cli/          — REPL: ReadLine, MarkdownRender, DiffRender, StatusBar, Repl      [JIT-only, ReadyToRun]
tests/
  Fugue.Tests/        — xUnit + FsUnit, 540+ unit tests
docs/
  process.md, role-capabilities.md, workflows/  — orchestrator skill operational manual
  superpowers/specs/, superpowers/plans/        — design docs, implementation plans
```

**Stack:** F# 9 · .NET 10 · Spectre.Console 0.49 · Markdig 0.41 · `Microsoft.Agents.AI` 1.3 · `System.CommandLine` (parser; F# DSL wrapper) · xUnit + FsUnit.

**Authoritative spec:** `docs/superpowers/specs/2026-04-29-fugue-spectre-aot-pivot.md`.

## AOT vs JIT — two binaries

Fugue ships as **two distributions** with different runtime tradeoffs. Pivot locked 2026-05-02.

| Binary             | Runtime                    | Cold start | Use case                                                       |
|--------------------|----------------------------|-----------:|----------------------------------------------------------------|
| `fugue-aot`   | **Native AOT** single-file | ~40 ms     | CI / scripting / pipe-input / `--print` / `-p` flag            |
| `fugue`            | **JIT + ReadyToRun**       | ~150 ms    | Interactive REPL / TUI sessions (long-lived, JIT warms once)   |

**Headless trigger** (decided in one place, `Fugue.Cli.Aot/Program.fs`):
```fsharp
let isHeadless (argv: string[]) : bool =
    argv |> Array.exists (fun a -> a = "--print" || a = "-p")
    || Console.IsInputRedirected
```

**Why we split:** keeping all code AOT-clean cost too much — binary 47MB vs 35MB target; constant trim-warning ceremony; `[<DynamicDependency>]` and source-gen workarounds; whole categories of NuGet packages off-limits. Interactive REPL stays alive for hours, so JIT pays its warmup once. Headless runs per-invocation, so its cold start is critical and AOT stays.

**What this means for contributors:**
- Code in `Fugue.Cli` (REPL) and `Fugue.Surface` (TUI primitives) is **JIT-only**. Write idiomatic F# without trim ceremony — reflection, dynamic loading, full Spectre features all OK.
- Code in `Fugue.Core`, `Fugue.Tools`, `Fugue.Agent`, `Fugue.Cli.Aot` is **AOT-clean**. Same constraints as before for these modules: no `MakeGenericMethod` outside annotated boundaries, STJ source-gen for new DTOs, etc.
- New features that don't fit AOT (MCP discovery, plugins, dynamic theme loading) live in `Fugue.Cli` / `Fugue.Surface` and are **interactive-only by design**. If a feature can't pass AOT publish, it doesn't belong in the headless surface — that's a deliberate signal, not a bug.
- Argument parsing uses `System.CommandLine` (AOT source-gen friendly) wrapped in a thin F# DSL (`Cli.command "print" (fun args -> ...)`). One parser, both binaries.

## Conventions & red lines

These are **hard rules**. Don't ask, just follow.

- **F# only under `src/`.** No C# files. C# is allowed only as external NuGet dependencies.
- **`.slnx`, not `.sln`.** Modern .NET solution format.
- **No `Co-Authored-By` trailers in commits.**
- **`TreatWarningsAsErrors=true`** stays in `Fugue.Core`, `Fugue.Tools`, `Fugue.Agent`, `Fugue.Cli.Aot` (the AOT closure). For these, `<NoWarn>` is allowed only for known third-party trim/AOT warnings (`IL2026`, `IL3050`, `IL3053`, `IL2104`). For `Fugue.Cli` / `Fugue.Surface` (JIT-only): `TreatWarningsAsErrors=true` still on, but trim/AOT warnings simply don't appear there.
- **Every PR must produce an AOT-clean publish of the headless binary** (`dotnet publish src/Fugue.Cli.Aot -c Release -r osx-arm64`). The interactive `Fugue.Cli` only needs `dotnet build` + `dotnet test` to pass — it doesn't get AOT-published.
- **Every 10 commits: full AOT publish + smoke-test the headless binary locally:**
  ```
  dotnet publish src/Fugue.Cli.Aot -c Release -r osx-arm64
  src/Fugue.Cli.Aot/bin/Release/net10.0/osx-arm64/publish/fugue-aot --version
  src/Fugue.Cli.Aot/bin/Release/net10.0/osx-arm64/publish/fugue-aot --print "hello"
  ```
  `dotnet build` / `dotnet test` run under JIT and will NOT catch AOT-specific failures in the headless closure (e.g. `MakeGenericMethod`, missing native code). Only the published native binary reveals them.
- **`git status` stays clean.** No stray files, no half-finished `.bak`/`.tmp`. Tree is always merge-ready.
- **No tests-on-mocks for things that hit the real boundary.** Tools tests run against `tmp/` directories; config tests isolate `HOME`.
- **Never squash-merge PRs. Use `--merge` (merge commit) or `--rebase`.** Squash rewrites SHAs, which breaks stacked PRs (downstream branches lose their common history, GitHub auto-closes them when the base branch is deleted). Merge commits preserve original SHAs so downstream PRs see their predecessors as "already applied" and continue cleanly. Default: `gh pr merge <N> --merge --delete-branch`. Past incident: squash-merging Phase 1.2a auto-closed PR #905 and #906 (had to recreate as #907 + #908). Never repeat.
- **Semver release policy:**
  - **New features → minor bump** (e.g. `0.1.0` → `0.2.0`). Default for any user-visible feature, command, UX change.
  - **Bug fixes → patch bump** (e.g. `0.2.0` → `0.2.1`). Only when no new behavior, just fixing existing.
  - **Breaking changes → major bump** (e.g. `0.x` → `1.0`). Config schema changes, removed flags, changed CLI defaults — require user migration.
  - Don't retag the same version after fixes — push a new patch.
- **🚨 NEW SLASH COMMANDS: PROMPT-FIRST, CODE-LAST. THIS IS NON-NEGOTIABLE.** When the user asks for a new `/some-command`, the **first question** is always: *"Can this be solved by sending a smart prompt to the LLM, with zero F# code?"* If yes — implement it as a **prompt template**, not as a hardcoded handler in `Repl.fs`. Hardcoded handlers cost compile time, bloat the binary, bloat `Repl.fs` (already 2800+ lines), and require a release for every prompt tweak. **Code is the last resort, not the default.** Only write F# code when the command genuinely needs side effects (reading files, running shell, mutating session state) that can't be expressed as a prompt. Examples: `/scaffold cqrs <Cmd>`, `/derive codec <Type>`, `/refactor pipeline` — these are pure prompts and **should not exist as code**. They should live in `~/.fugue/prompts/<name>.md` (with embedded defaults shipped in the binary). Past mistake: we burned tokens and code on commands that were just "send X prompt to agent" — never repeat. **When in doubt, write a `.md` template, not F# code.**

## Decision-making pattern (CEO escalation)

When a decision is **non-trivial** — breaking change, file deletion, schema migration, picking between two architectures, dropping a dependency — the orchestrator does **not** decide alone. Pattern:

1. Spawn **two subagents on the same model** (typically `sonnet` for medium / `opus` for architecture-grade) running in parallel.
2. Subagent A argues **for** the change. Subagent B argues **against**.
3. Run **3–5 rebuttal rounds** between them (`SendMessage` to continue).
4. Surface their final positions, the strongest counter-argument from each side, and any common ground to **the user (CEO)**.
5. **The user decides.** Orchestrator never picks the winner unilaterally on these classes of decisions.

**Always use this pattern for:**
- Breaking changes to public-facing config schema, CLI flags, or `~/.fugue/config.json` shape
- Deleting source files (allowed if a recent commit still has them; the debate is whether they're truly unused)
- Adding/dropping NuGet packages that affect AOT trim cost
- Architectural pivots (e.g., swapping rendering library, changing provider abstraction)

**Skip the debate (just act) for:** typo fixes, small refactors, test additions, doc edits, dependency patch bumps that change no APIs.

## CEO escalation rules (non-debate)

Always confirm with the user before:
- `git push`, `git push --force`, creating GitHub releases, publishing to NuGet
- Deleting files **without** prior commits backing them up (i.e., uncommitted work)
- Modifying CI/CD pipelines (when added)
- Rotating or regenerating API keys / secrets
- Sending anything outside the local machine (paste-bins, telemetry endpoints, gists)

## Status (2026-04-30)

**Phase 1–5 closed.** Acceptance criteria met (with one accepted deferral on binary size — 47MB vs 35MB target; root cause: `FSharp.Core` + `System.Text.Json` rooted via `TrimmerRootAssembly`, plus features added since MVP close (hooks, sessions, SQLite); mitigation in v0.2 via STJ source-gen for F# DTOs).

Verified metrics:
- Binary: **47 MB** osx-arm64 single-file AOT
- Cold start: **40 ms**
- Peak RSS: **47 MB**
- Tests: **120+/120+** pass
- E2E smoke: passed against LM Studio (OpenAI-compat); not yet against real Anthropic / OpenAI cloud / Ollama
- Multiple PRs in review (batch shipped 2026-04-30):
  - PR #221: `!cmd` shell shortcut, `/new` session reset, Ctrl+L, 8 UX/bug fixes
  - PR #321: elapsed time display in status bar (#253)
  - PR #335: `/tools` command + `/clear-history` (#258, #240)
  - PR #350: hierarchical FUGUE.md loading cwd→git root + `~/.fugue/FUGUE.md` (#217)
  - PR #356: `/short` and `/long` verbosity commands (#236)
  - PR #357: `/summary` session summary command (#209)
  - PR #166: input history (Up/Down) + word cursor (pending)
  - #898: `/model set` autocomplete — `ModelDiscovery` module fetches model list from active provider at session start; Tab-cycles through matching names in `/model set <prefix>` context (shipped 2026-05-01)
  - #901 Phase 2: `/turns` interactive turn picker (arrow-key navigation + Enter to dispatch `/show N`)

Live binary: `src/Fugue.Cli/bin/Release/net10.0/osx-arm64/publish/fugue`

## Open backlog

**Authoritative source: GitHub Issues at [`korat-ai/fugue/issues`](https://github.com/korat-ai/fugue/issues).** Issue numbers grow continuously — never hardcode them in this file. Always pull live before planning.

**Label taxonomy:**

- `enhancement,ux` — input handling, slash commands, themes, history, compact display, ghost-suggestions, vim mode
- `enhancement,safety` — approval modes, checkpoints, sandboxing
- `enhancement,integrations` — MCP, LSP, headless / non-interactive mode
- `enhancement,extensibility` — hooks (`PreToolUse`/`PostToolUse`/`SessionStart`/`SessionEnd`), plugin points
- `bug` — regressions, crashes, terminal glitches
- `infra` — AOT/trim/publish/CI changes
- `docs` — spec/plan/CLAUDE.md edits

**Querying the live list (orchestrator should run these on every plan):**

```bash
gh issue list -R korat-ai/fugue --state open --limit 100 \
  --json number,title,labels --jq '.[] | "#\(.number) [\(.labels|map(.name)|join(","))] \(.title)"'

# By label:
gh issue list -R korat-ai/fugue --label safety
gh issue list -R korat-ai/fugue --label integrations
```

**Context for what each label group looks like (pre-MVP-close, may have shipped since):**

- *UX & input*: slash commands beyond `/help`+`/clear`+`/new`+`/exit`, `@file` injection, input history, ghost suggestions, themes, vim mode, compact tool output, batch labels for parallel tool calls
- *Safety*: approval modes (Plan/Default/Auto-Edit/YOLO with Shift+Tab cycle, covers spec's v0.2 permission-prompt), file-modification checkpointing + `/restore`
- *Context & sessions*: `FUGUE.md` per-project context (loaded on session start), `/init` to bootstrap it, persistent sessions, context-window % indicator, `/compress` history summarisation
- *Integrations*: MCP client, LSP integration, headless / non-interactive mode for CI
- *Rendering*: rich diff viewer for `Edit` tool results, syntax highlighting in code blocks

**Polish not yet ticketed** (raise as issues when picked up):

- `cd` persistence in Bash tool (subprocess `cd` doesn't propagate to Fugue's tracked cwd)
- Live progressive markdown re-render during streaming (tried `\x1b[s/u` and relative cursor moves — both unstable across multi-segment responses with tool errors; needs a virtual-buffer approach or alt-screen excursion)
- E2E smoke against real Anthropic / OpenAI cloud / Ollama
- STJ source-gen for F# DTOs → drops binary size below 35MB target
- F# script config (`~/.fugue/fugue.fsx`) — xmonad-style "recompile self" pattern

## Style notes

- **Musical references in UI** are encouraged where they fit naturally — the project is named after a contrapuntal form, lean into it. Examples: `cadence` for completion, `tempo` for streaming rate, `voice` for an active agent, `prelude`/`coda` for boot/exit. **Don't force it.** If a name reads awkward to a non-musician, pick something plain.
- Terse code over verbose. No XML doc bloat. Comments only when *why* is non-obvious.
- F# idioms: pipes, DUs over inheritance, immutable by default. `mutable` only inside narrow IO/state holders (`ReadLine.S`, `StatusBar`'s module-level state).

## Focus signals (for orchestrator role selection)

This project's domain emphasises:

- **`AI`, `LLM`, `Claude`, `GPT`, `Ollama`, `Anthropic`, `OpenAI`** — agent framework integration is the core feature
- **`CLI`, `TUI`, `terminal`, `ANSI`, `scroll region`** — UX is terminal-first
- **`AOT`, `trimming`, `IL2026`, `IL3050`, `single-file`** — deployment constraints affect headless-closure code (`Fugue.Core` / `Fugue.Tools` / `Fugue.Agent` / `Fugue.Cli.Aot`); REPL code (`Fugue.Cli` / `Fugue.Surface`) is JIT-only and free of these
- **`F#`, `dotnet`, `.NET 10`** — language/runtime is fixed; no polyglot mixing
- **`performance`, `cold start`, `RSS`, `binary size`** — non-functional targets are first-class
- **`localization`, `i18n`, `en`, `ru`** — project ships in two languages from day one

The orchestrator should add `/engineering-ai-engineer` at PLAN+BUILD+VERIFY, `/testing-performance-benchmarker` at VERIFY, and `/engineering-fsharp-developer` (ad-hoc role — create via `/engineering-agent-prompt-engineer` if absent) at BUILD.

---

*This file is the orchestrator's first read. Keep it accurate. When status changes, update.*
