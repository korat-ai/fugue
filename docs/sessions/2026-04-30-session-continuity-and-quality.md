# Session: Continuity, Quality Signals, and Terminal Introspection

**Date:** 2026-04-30

## Issues Closed

- #808: Tool-call efficiency warning — displays ⚠️ after turns with ≥10 tool calls
- #801: `/compat` command — terminal capability matrix with Spectre.Console Table rendering
- #856: Session continuity — three-sentence LLM-generated summary on exit, `<previous-session>` context injection on startup
- #800: Context quality indicator — real-time context window % in StatusBar line 2 with color-coded urgency (green/yellow/red)

## Issues Partially Implemented

- #793 (phase 0): Turn annotation system — `Annotation.fs` foundation in place, `/rate up|down` command functional; phase 1+ (persistent rating storage, model training signals) deferred

## Key Technical Decisions

- **SessionSummary.fs injection point:** `<previous-session>` block inserted directly before user's first message each session, preserving chain-of-thought without expanding system prompt. Avoids context-pollution by summarizing to exactly three sentences.
- **compat matrix as introspection:** `/compat` reflects Spectre.Console's own capability detection (`AnsiSupport`, `Colorspace`). Single source of truth; serves both debugging and feature negotiation.
- **Efficiency warning threshold at ≥10 calls:** Aligns with practical token-budgeting; 10+ calls signal orchestrator should consolidate or parallelize. Rendered as terse ⚠️ in response footer, not modal.
- **Context % color coding:** Yellow at 75%, red at 90%. Precise enough to guide `/compress` decisions without constant manual calculation. Tied to `model.ContextWindow` from provider discovery.
- **Annotation.fs as minimal foundation:** Decoupled rating logic (user input, session tracking, `/rate` parsing) from scoring system (phase 1). Allows incremental wiring without bloating this session's scope.

## Files Changed

- `src/Fugue.Cli/SessionSummary.fs` — new; LLM summarization, `<previous-session>` formatting
- `src/Fugue.Cli/Render.fs` — ⚠️ emoji update, efficiency warning footer
- `src/Fugue.Cli/StatusBar.fs` — context % indicator, color-coded bands
- `src/Fugue.Cli/Annotation.fs` — new; `/rate` command parser, session turn tracking
- `src/Fugue.Cli/Repl.fs` — SessionSummary injection on startup, `/compat` handler
- `src/Fugue.Core/CompatMatrix.fs` — new; Spectre capability detection

**Binary size:** Unchanged (44 MB osx-arm64). AOT-clean publish verified.
