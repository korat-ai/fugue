# Changelog

All notable changes to Fugue are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.2.3] — 2026-05-01

### Fixed
- Inline slash-command suggestions reappear on bare `/` again (regression from v0.2.2 — was gated to ≥2 chars to avoid terminal overflow; the `truncate 8` cap already prevents that).
- Slash-suggestion list expanded from 13 to 70+ entries — `/m*`, `/scaffold *`, `/check *`, `/derive *`, `/refactor *`, `/migrate *`, `/translate *`, etc. were missing entirely. Two parallel lists (`ReadLine.fs` vs `Repl.fs helpItems`) had drifted apart.
- Removed stray `[/]` (Spectre markup) embedded inside a raw ANSI escape sequence in slash overflow hint.

[0.2.3]: https://github.com/korat-ai/fugue/releases/tag/v0.2.3

## [0.2.2] — 2026-05-01

### Fixed
- NuGet publishing — replaced manual `curl` OIDC fetch + raw JWT-as-api-key with official `NuGet/login@v1` action. NuGet Trusted Publishing requires an explicit token-exchange step (GitHub OIDC → NuGet temporary API key); we were skipping it, getting consistent 403 Forbidden. v0.2.0 and v0.2.1 NuGet pushes failed for this reason; v0.2.2 succeeds.

[0.2.2]: https://github.com/korat-ai/fugue/releases/tag/v0.2.2

## [0.2.1] — 2026-05-01

### Fixed
- `/model` picker crashed on render with `InvalidOperationException: Could not find color or style 's\'`. Spectre.Console markup escapes literal brackets as `[[` / `]]`, not `\[` / `\]` — the picker used the wrong escape, broke on every redraw.

[0.2.1]: https://github.com/korat-ai/fugue/releases/tag/v0.2.1

## [0.2.0] — 2026-05-01

### Added
- `/model` interactive picker — arrow-key navigation (↑/↓), Enter to select, Esc to cancel, digits 1-9 for quick-pick (#898)
- `/model set <name>` — runtime model switching with agent rebuild
- `Fugue.Agent.ModelDiscovery` — per-provider model listing (OpenAI/LM Studio `/v1/models`, Ollama `/api/tags`, Anthropic hardcoded). HTTP timeout 1.5s, AOT-safe via `JsonNode.Parse`
- `--help` flag — shows usage and exits cleanly without requiring config
- `/engineering-fsharp-developer` agent role with AOT hard rules and known traps reference
- CI: `verify-brew` job — smoke-tests `brew install fugue && fugue --version && fugue --help` after each release

### Changed
- Banner: `♩ Fugue` → `♩♪♫ Fugue` (rhythmic note sequence)
- Turn numbers `[N]` now follow message alignment — right-aligned messages show `[N]` at end of bubble
- Slash command suggestions only appear at ≥2 char prefix (bare `/` no longer overflows terminal)

### Fixed
- AOT crash on startup: `Render.gradientMarkup` and ~30 other `sprintf` call sites replaced with F# string interpolation `$"..."` — `PrintfImpl.MakeGenericMethod` is not AOT-compatible
- NuGet Trusted Publishing — explicit OIDC token retrieval via GitHub Actions ID token endpoint
- macOS release: `sha256sum` not available → portable fallback to `shasum -a 256`
- Release pipeline: force-publish via `gh release edit --draft=false` after creation (guards against retag draft regression)

### Documentation
- CLAUDE.md: hard rule "new slash commands prompt-first, code-last"
- CLAUDE.md: AOT smoke-test rule (`dotnet publish -r osx-arm64` and run native binary every 10 commits — `dotnet build` runs JIT and misses AOT failures)
- CLAUDE.md: semver release policy (features → minor, fixes → patch, no retags)

[0.2.0]: https://github.com/korat-ai/fugue/releases/tag/v0.2.0

## [0.1.0] — 2026-04-30

### Added
- Provider-agnostic AI agent: Anthropic, OpenAI-compatible, Ollama
- Tools: Read, Write, WriteBatch, Edit, Bash, Glob, Grep, Tree
- GlobTool: `!exclusion` patterns, brace expansion `{a,b}`, multi-ext `**/*.{fs,fsi}`
- GrepTool: `--symbol` word-boundary search, comment-line skip
- EditTool: conflict detection — warns if file changed on disk since last Read
- WriteBatch: atomic multi-file write (all-or-nothing staged rename)
- BashTool: `clean_env` flag strips secrets; uses `/bin/sh -c` universally
- Markdown rendering with Markdig + Spectre.Console
- Diff rendering for Edit tool output
- Stack trace beautifier
- Input history (Up/Down), word cursor (Ctrl+Left/Right)
- Slash commands: `/help`, `/clear`, `/new`, `/exit`, `/summary`, `/short`, `/long`,
  `/compress`, `/tools`, `/tokens`, `/context show`, `/locale`, `/pin`,
  `/report-bug`, `/http`, `/rop`, `/infer-type`, `/pointfree`, `/monad`,
  `/scaffold du|cqrs|actor`, `/derive codec`, `/migrate oop-to-fp`,
  `/check exhaustive|tail-rec|effects`, `/refactor pipeline`,
  `/translate comments`, `/breaking-changes`, `/idiomatic`, `/incremental`,
  `/port`, `/complexity`
- `@file` injection, `@recent N`, `@last` shortcuts
- CRLF / zero-width character input sanitization
- Error trend tracking + recurring-error nudge
- Session summary auto-saved on exit
- FUGUE.md hierarchical context loading (cwd → git root → `~/.fugue/FUGUE.md`)
- Approval modes: Plan / Default / Auto-Edit / YOLO (Shift+Tab)
- Status bar with streaming indicator, elapsed time, token/s estimate
- Clip ring (`Ctrl+Y` cycle), undo/redo
- Dark/light/no-color theme auto-detect
- Native AOT single-file binary: 46 MB osx-arm64, 40 ms cold start, 47 MB RSS
- GitHub Actions CI (build + test + AOT smoke on 3 platforms)
- GitHub Actions release (AOT binaries + NuGet tool publish + Homebrew tap update)
- Homebrew tap: `brew tap korat-ai/fugue && brew install fugue`
- dotnet tool: `dotnet tool install -g fugue` (managed, suffix `-jit`)

### Distribution
| Channel | Install |
|---|---|
| Homebrew (macOS/Linux) | `brew tap korat-ai/fugue && brew install fugue` |
| dotnet tool | `dotnet tool install -g fugue` |
| GitHub Releases | download `fugue-0.1.0-<rid>.tar.gz` |

[0.1.0]: https://github.com/korat-ai/fugue/releases/tag/v0.1.0
