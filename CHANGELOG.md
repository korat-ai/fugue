# Changelog

All notable changes to Fugue are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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
