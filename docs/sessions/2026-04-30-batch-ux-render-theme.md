<!-- /Users/roman/Documents/dev/Korat_Agent/docs/sessions/2026-04-30-batch-ux-render-theme.md -->

## 2026-04-30 Batch: UX, Rendering, Theme

## Critical fix: post-stream markdown re-render

`Repl.fs` streamAndRender() now properly renders markdown after assistant response completes. Previously `assistantFinal` was unreachable dead code; streaming wrote raw text via `Console.Out.Write`. Fixed by computing line count from raw output, moving cursor back with `\x1b[{rawLines}A\x1b[J`, then rendering with `AnsiConsole.Write(Render.assistantFinal text)`.

## Emoji mode config toggle (#893)

Added `UiConfig.EmojiMode` string field (auto/always/never). `Program.fs` maps the config value to `MarkdownRender.initEmoji()`. Expanded `EmojiMap.pairs` from 20 to 38 entries to cover more markdown syntax elements.

## Macros test suite (#896)

Added 19-case unit test suite in `MacrosTests.fs` covering record/stop/play/list/remove/clear operations across valid and edge-case scenarios.

## Nocturne theme palette (#894)

New theme with muted tool bullets, ANSI-256 steel-blue status bar, fenced code-block line numbers in `#5a7a99`, and dim `#1e3a5a` bubble Panel borders. `Render.initTheme` and `StatusBar.initTheme` added and called from `Program.fs` on startup.

## Bubbles mode persistence (#897)

`/theme bubbles` now persists to config. Added `UiConfig.BubblesMode` bool field. Command updates `savedUi` and calls `Config.saveToFile`. 3 round-trip unit tests verify persistence and reload.

## Typewriter entrance effect (#868)

`/theme typewriter` toggle adds per-line entrance animation. Uses `AnsiConsole.Create` + `StringWriter` to capture lines, splits by newline, writes each line with `Task.Delay(12)` between. Persists to config.

## Gradient startup banner (#862)

`Render.showBanner()` now interpolates blue-to-purple gradient (`#3b82f6→#a855f7`) across "Fugue" characters via Spectre markup.

## Test count

**315 / 315** pass. All features validated end-to-end.
