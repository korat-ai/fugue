# Fugue

> A small, fast, F#-on-.NET-AOT terminal coding agent.
> Cold-starts in ~40ms. Single 44MB native binary. Provider-agnostic (Anthropic / OpenAI-compat / Ollama).
> Inspired by Claude Code's workflow but built for engineers who don't want a 300MB Node tree.

**Repo:** `korat-ai/fugue` · **Status:** MVP shipped (Phase 1–5 closed, 2026-04-30) · **Default branch:** `main`

---

## Who this is for

Polyglot backend / systems engineers on **macOS-arm64 and Linux-x64** working in **.NET, TypeScript, Java, Elixir, Scala, Kotlin** (and adjacent). The audience cares about: fast launch, low RSS, scriptable terminal UX, no Electron/Node baggage, native binaries shipped via release pages or `brew`. **Not** for developers who need a polished GUI or for users who prioritise the largest possible plugin ecosystem — pick Claude Code or VS Code Copilot for that.

## Architecture

```
src/
  Fugue.Core/    — domain types, config, localization, system prompt, JSON DTOs (no UI, no network)
  Fugue.Agent/   — Microsoft.Agents.AI factory + Conversation streaming + provider Discovery
  Fugue.Tools/   — Read / Write / Edit / Bash / Glob / Grep as no-reflection AIFunction subclasses
  Fugue.Cli/     — REPL: ReadLine (own ANSI), MarkdownRender (Markdig), DiffRender, Render, StatusBar (scroll-region), Repl, Program
tests/
  Fugue.Tests/   — xUnit + FsUnit, 87 unit tests
docs/
  process.md, role-capabilities.md, workflows/  — orchestrator skill operational manual
  superpowers/specs/, superpowers/plans/        — design docs, implementation plans
```

**Stack:** F# 9 · .NET 10 · Spectre.Console 0.49 · Markdig 0.41 · `Microsoft.Agents.AI` 1.3 · xUnit + FsUnit · Native AOT (single-file, trimmed).

**Authoritative spec:** `docs/superpowers/specs/2026-04-29-fugue-spectre-aot-pivot.md`.

## Conventions & red lines

These are **hard rules**. Don't ask, just follow.

- **F# only under `src/`.** No C# files. C# is allowed only as external NuGet dependencies.
- **`.slnx`, not `.sln`.** Modern .NET solution format.
- **No `Co-Authored-By` trailers in commits.**
- **`TreatWarningsAsErrors=true`** stays. **No `<NoWarn>` for our own code** — fix the underlying issue. `<NoWarn>` is allowed only for known third-party trim/AOT warnings (`IL2026`, `IL3050`, `IL3053`, `IL2104`).
- **Every PR must produce an AOT-clean publish** (`dotnet publish src/Fugue.Cli -c Release -r osx-arm64`). No regressions in `Build succeeded. 0 Error(s).`
- **`git status` stays clean.** No stray files, no half-finished `.bak`/`.tmp`. Tree is always merge-ready.
- **No tests-on-mocks for things that hit the real boundary.** Tools tests run against `tmp/` directories; config tests isolate `HOME`.

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

**Phase 1–5 closed.** Acceptance criteria met (with one accepted deferral on binary size — 44MB vs 35MB target; root cause: `FSharp.Core` + `System.Text.Json` rooted via `TrimmerRootAssembly`; mitigation in v0.2 via STJ source-gen for F# DTOs).

Verified metrics:
- Binary: **44 MB** osx-arm64 single-file AOT
- Cold start: **40 ms**
- Peak RSS: **47 MB**
- Tests: **87/87** pass
- E2E smoke: passed against LM Studio (OpenAI-compat); not yet against real Anthropic / OpenAI cloud / Ollama

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

- *UX & input*: slash commands beyond `/help`+`/clear`+`/exit`, `@file` injection, `!shell` shortcut, input history, ghost suggestions, themes, vim mode, compact tool output, batch labels for parallel tool calls
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
- **`AOT`, `trimming`, `IL2026`, `IL3050`, `single-file`** — deployment constraints affect every code change
- **`F#`, `dotnet`, `.NET 10`** — language/runtime is fixed; no polyglot mixing
- **`performance`, `cold start`, `RSS`, `binary size`** — non-functional targets are first-class
- **`localization`, `i18n`, `en`, `ru`** — project ships in two languages from day one

The orchestrator should add `/engineering-ai-engineer` at PLAN+BUILD+VERIFY, `/testing-performance-benchmarker` at VERIFY, and `/engineering-fsharp-developer` (ad-hoc role — create via `/engineering-agent-prompt-engineer` if absent) at BUILD.

---

*This file is the orchestrator's first read. Keep it accurate. When status changes, update.*
