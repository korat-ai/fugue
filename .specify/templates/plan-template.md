# Implementation Plan: [FEATURE]

**Branch**: `[###-feature-name]` | **Date**: [DATE] | **Spec**: [link]

**Input**: Feature specification from `/specs/[###-feature-name]/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command. See `.specify/templates/plan-template.md` for the execution workflow.

## Summary

[Extract from feature spec: primary requirement + technical approach from research]

## Technical Context

<!--
  ACTION REQUIRED: Replace the content in this section with the technical details
  for the project. The structure here is presented in advisory capacity to guide
  the iteration process.
-->

**Language/Version**: [e.g., Python 3.11, Swift 5.9, Rust 1.75 or NEEDS CLARIFICATION]

**Primary Dependencies**: [e.g., FastAPI, UIKit, LLVM or NEEDS CLARIFICATION]

**Storage**: [if applicable, e.g., PostgreSQL, CoreData, files or N/A]

**Testing**: [e.g., pytest, XCTest, cargo test or NEEDS CLARIFICATION]

**Target Platform**: [e.g., Linux server, iOS 15+, WASM or NEEDS CLARIFICATION]

**Project Type**: [e.g., library/cli/web-service/mobile-app/compiler/desktop-app or NEEDS CLARIFICATION]

**Performance Goals**: [domain-specific, e.g., 1000 req/s, 10k lines/sec, 60 fps or NEEDS CLARIFICATION]

**Constraints**: [domain-specific, e.g., <200ms p95, <100MB memory, offline-capable or NEEDS CLARIFICATION]

**Scale/Scope**: [domain-specific, e.g., 10k users, 1M LOC, 50 screens or NEEDS CLARIFICATION]

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Verify this plan against `.specify/memory/constitution.md` (v1.1.0). Each
gate below maps to a numbered Principle. Tick every box, or document the
violation under "Complexity Tracking" with an explicit justification.

- [ ] **Principle I — Test Coverage Discipline**: Every new public function,
      type, smart constructor, or DU listed in the plan has a corresponding
      test task in `tasks.md` (happy path + Error/None path). Bug-fix plans
      list a regression test that fails on pre-fix code.
- [ ] **Principle II — C#/F# Interop Sanitization**: If this plan touches a
      C# NuGet boundary (`Microsoft.Agents.AI`, `Spectre.Console`, `Markdig`,
      `System.CommandLine`, STJ, or any new C# package), it includes an
      adapter task and an integration-test task hitting the real library
      (no mocks at the boundary).
- [ ] **Principle III — Subagent-Driven Development**: Open-ended research
      and multi-file implementation are scoped for subagent dispatch, not
      inline execution. Non-trivial decisions are routed through the
      dual-subagent debate pattern.
- [ ] **Principle IV — F# Purity at Source**: No `.cs` files are introduced
      under `src/`. New dependencies are NuGet packages, not source.
- [ ] **Principle V — AOT-Closure vs JIT Discipline**: New code is placed
      in the correct closure. Headless-closure additions (`Fugue.Core`,
      `Fugue.Tools`, `Fugue.Agent`, `Fugue.Cli.Aot`) are AOT-clean; any
      AOT-incompatible primitive lives in `Fugue.Cli` or `Fugue.Surface`.
- [ ] **Principle VI — Prompt-First Slash Commands**: If this plan adds a
      slash command, the design question *"Can this be a prompt template
      with zero F# code?"* has been answered, and code is only used where
      side effects (file IO, shell, session-state mutation) require it.
      Conversely, if this plan promotes an *existing* prompt template to
      F# code, the planner has documented the failure pattern that
      justified the upgrade (per VI's inverse rule).
- [ ] **Principle VIII — Tool Contract Discipline**: If this plan adds or
      modifies a tool: (a) the tool's input is a named F# record with
      precise types validated in the dispatcher (not the tool body),
      (b) every code path returns a result (denial / timeout / error /
      abort included — no silent drop), (c) the dispatch is recorded in
      `SessionRecord`. No broad tools (`execute_anything` and friends).
- [ ] **Principle IX — Runtime Policy ≠ Prompt Policy**: Any safety
      property introduced by this plan (destructive-shell guard,
      approval-mode behaviour, secret redaction, CEO escalation) is
      enforced in F# code, not only in the system prompt. The system
      prompt MAY describe the rule; it MUST NOT be the only place it
      lives.
- [ ] **Principle X — Untrusted Content Boundary**: If this plan adds a
      retrieval channel (web fetch, GH API, MCP server, file ingest,
      shell stdout consumption), tool-result content from that channel
      is framed as *data* in downstream prompts, not spliced into the
      system prompt; instruction-shaped content from the channel triggers
      conflict-surfacing to the user rather than silent compliance.
- [ ] **Principle XI — Context as Engineered Artifact**: If this plan
      touches the system prompt, `CLAUDE.md`, tool-schema preamble, or
      conversation-compaction code: (a) the cacheable prefix stays free
      of timestamps / request IDs / volatile env state, (b) any
      compaction summary preserves working state (approvals, active
      plan, files-modified, error trends) not conversational prose,
      (c) new context sources are loaded on demand, not pre-spliced.
- [ ] **Quality Gates**: The plan does not regress `dotnet build`,
      `dotnet test`, or `dotnet publish src/Fugue.Cli.Aot` (zero trim
      warnings outside the allow-list).

## Project Structure

### Documentation (this feature)

```text
specs/[###-feature]/
├── plan.md              # This file (/speckit-plan command output)
├── research.md          # Phase 0 output (/speckit-plan command)
├── data-model.md        # Phase 1 output (/speckit-plan command)
├── quickstart.md        # Phase 1 output (/speckit-plan command)
├── contracts/           # Phase 1 output (/speckit-plan command)
└── tasks.md             # Phase 2 output (/speckit-tasks command - NOT created by /speckit-plan)
```

### Source Code (repository root)
<!--
  ACTION REQUIRED: Replace the placeholder tree below with the concrete layout
  for this feature. Delete unused options and expand the chosen structure with
  real paths (e.g., apps/admin, packages/something). The delivered plan must
  not include Option labels.
-->

```text
# [REMOVE IF UNUSED] Option 1: Single project (DEFAULT)
src/
├── models/
├── services/
├── cli/
└── lib/

tests/
├── contract/
├── integration/
└── unit/

# [REMOVE IF UNUSED] Option 2: Web application (when "frontend" + "backend" detected)
backend/
├── src/
│   ├── models/
│   ├── services/
│   └── api/
└── tests/

frontend/
├── src/
│   ├── components/
│   ├── pages/
│   └── services/
└── tests/

# [REMOVE IF UNUSED] Option 3: Mobile + API (when "iOS/Android" detected)
api/
└── [same as backend above]

ios/ or android/
└── [platform-specific structure: feature modules, UI flows, platform tests]
```

**Structure Decision**: [Document the selected structure and reference the real
directories captured above]

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| [e.g., 4th project] | [current need] | [why 3 projects insufficient] |
| [e.g., Repository pattern] | [specific problem] | [why direct DB access insufficient] |
