# Feature Specification: Publish Fugue.Adapters.Console as NuGet Package

**Feature Branch**: `004-publish-adapters-console-nuget`

**Created**: 2026-05-19

**Status**: **Deferred (2026-05-19)** — see "Deferral Note" below.

**Input**: User description: "Phase 5 — publish `Fugue.Adapters.Console` (Phase 1+2 F# wrapper over Spectre.Console + Phase 3 Layout framework: Stack/Dock/LayoutGrid/Flex + Scheduler) as a standalone NuGet package directly from the existing `korat-ai/fugue` monorepo, without extraction into a separate repository."

## Deferral Note (2026-05-19)

During `/speckit-plan` Phase 0, three concerns surfaced that the v1.1.0 constitution does not currently cover:

1. **New operating mode without governance**: publishing a NuGet creates an open-source-library maintainer role (external consumers, SemVer obligations to strangers, vulnerability response, transitive-dep migration support). The constitution speaks to Fugue-as-product, not Fugue-as-library-maintainer.
2. **Naming is irreversible after first publish**: `Fugue.Adapters.Console` as a package id was treated in the spec as "v1.0 concern" via a hypothetical `TypeForwarded` shim, but the first publish creates external references (Google, StackOverflow, downstream projects) that no shim repairs cleanly. The naming decision needs to be made BEFORE publication, not deferred past it.
3. **Bus factor = 1 + active development = bad time for external commitments**: a single active maintainer + ongoing Phase-3 churn means new issues from strangers compete with internal velocity; the worst outcome is an "abandoned-looking" package on a feed with stale `last commit` after 6+ months.

The feature is therefore **deferred** rather than re-scoped. Revisit conditions (ALL three must be met before reopening):

- **Condition 1 — dogfooding**: Phase 4 complete — `Fugue.Surface` / `Render.fs` / `MarkdownRender.fs` / `DiffRender.fs` / `StackTraceRender.fs` / `Doctor.fs` / `Picker.fs` / `StreamRender.fs` are all migrated to consume the package's Layout API in production. Zero direct `Spectre.Console.AnsiConsole` calls in `src/Fugue.Cli/` outside of the package itself.
- **Condition 2 — external demand signal**: at least 2 independent inbound signals (GitHub issue, mailing-list post, direct outreach) asking for the package as a separable artefact. Speculative publish without demand is rejected.
- **Condition 3 — bandwidth honesty**: either bus factor > 1 (a second active maintainer has been onboarded), OR the maintainer has explicitly carved 2–4 h/month for package issue triage and committed to it in writing (e.g. CLAUDE.md addendum).

When all three are met, this spec is the starting point — most of the analysis below stands. The Assumptions and Out of Scope sections, and Open Architectural Questions (resolved in research.md), remain the working baseline. Re-litigate only the points that have new evidence.

Tracking issue: see GitHub issue tagged `phase-5-deferred` for the live revisit checklist.

---

## User Scenarios & Testing *(mandatory)*

### User Story 1 — F# developer installs the package and renders a layout in their own project (Priority: P1)

A polyglot .NET developer who already uses Spectre.Console in C# discovers an F# wrapper that gives them typed primitives, smart constructors returning `Result`, and a higher-level Layout API (Stack / Dock / Grid / Flex). They add the package to a new `dotnet new console -lang F#` project, write 10 lines of F#, and render an aligned multi-region layout to their terminal.

**Why this priority**: This is the entire value proposition. Without an installable package that a stranger can `dotnet add package` and run examples from in under 5 minutes, the publication effort produces zero external value. Everything else in this feature is in service of this story.

**Independent Test**: A reviewer clones a fresh empty directory, runs `dotnet new console -lang F#`, follows the README's "Quick start" snippet (≤ 15 lines of F#), and observes a correctly aligned layout in their terminal. No knowledge of Fugue (the AI agent product) is required.

**Acceptance Scenarios**:

1. **Given** the package is published to a NuGet feed (initially local; later nuget.org), **When** a developer runs `dotnet add package Fugue.Adapters.Console` in a fresh F# console project, **Then** the package restores cleanly with only the external rendering library (and its sibling packages already used by the adapter) as transitive dependencies — no Fugue product code is pulled in.
2. **Given** a developer follows the README "Quick start" example, **When** they run `dotnet run`, **Then** their terminal displays a layout matching the README description (Stack with Dock containing two labelled regions).
3. **Given** the developer wants to compose more advanced layouts, **When** they read the four primitive examples in the README, **Then** each example compiles and runs as-is without modification.

---

### User Story 2 — Maintainer cuts a release with a single tag push (Priority: P2)

A Fugue maintainer decides the package is stable enough for a new public release. They bump the version field in the project file, push a git tag of an agreed shape, and within minutes the corresponding redistributable artefact appears on the configured NuGet feed without any further manual action.

**Why this priority**: Without automated release plumbing, every publish becomes a manual ritual the maintainer postpones; releases bunch up and the package ages on a stale version even when source is moving. A tag-driven workflow makes the cost of cutting a release near-zero and aligns with industry-standard release practice for .NET libraries.

**Independent Test**: A maintainer pushes a tag matching the agreed release-tag pattern to the repository, then within a bounded time window observes a successful CI run that produced a redistributable artefact and (when targeted at the public feed) confirms the new version appears on the public listing.

**Acceptance Scenarios**:

1. **Given** a tag matching the release-tag pattern is pushed, **When** the CI workflow runs, **Then** it produces a redistributable artefact whose version matches the tag and whose contents include the compiled library, embedded README, and license declaration.
2. **Given** the tag is pushed and the workflow has credentials configured, **When** the workflow completes, **Then** the package is uploaded to the target feed and the listing displays the README, license, repository link, and tags.
3. **Given** a tag does NOT match the release-tag pattern, **When** the developer pushes it, **Then** the publish workflow does NOT run (preventing accidental releases from miscellaneous tags).
4. **Given** the workflow fails partway (e.g. authentication error during upload), **When** the maintainer inspects the CI log, **Then** the failure is clearly attributable to a single step and the maintainer can re-run after fixing without manual cleanup of partial state.

---

### User Story 3 — Fugue continues to build and run after the package is decoupled (Priority: P1)

After the package is decoupled from the rest of the Fugue product, every existing Fugue contributor can clone the repository, build it, run the test suite, and run the Fugue REPL binary, with no observable change in behaviour. All existing tests continue to pass on all three supported continuous-integration operating systems, and both Fugue binaries continue to start and respond to standard flags.

**Why this priority**: This is the "do no harm" guarantee. A NuGet publication effort that breaks the parent product is a net loss. Treating regression-prevention as a first-class user story (not an implicit non-functional requirement) makes it visible in the task list and ensures it gets test coverage equal to the new functionality. Same priority level as Story 1 because the two together form the feature's "minimum acceptable result": you cannot ship one without the other.

**Independent Test**: A contributor on a fresh clone runs the standard full-test command and observes a passing test count greater than or equal to the pre-feature baseline, and additionally publishes both Fugue binaries per repository conventions and confirms each starts successfully and responds to standard flags.

**Acceptance Scenarios**:

1. **Given** the package decoupling work is merged, **When** any contributor runs the standard full-test command, **Then** the total passing test count meets or exceeds the pre-merge baseline.
2. **Given** the decoupling is complete, **When** a contributor runs the headless and interactive binary smoke tests defined in the repository conventions, **Then** both binaries start, accept the standard version and help flags, and exit cleanly.
3. **Given** the headless binary AOT-publish workflow runs on CI, **When** the binary is published for each supported runtime identifier, **Then** the publish produces a binary with no new trim or AOT warnings beyond the existing allow-listed set.

---

### User Story 4 — Reader of the package page understands what the package is for in under 60 seconds (Priority: P2)

A developer skimming search results on a NuGet feed lands on the package listing, reads the README rendered inline, and within one minute either decides "this fits my need, I'll try it" or "this isn't what I'm looking for". They don't need to clone the repository, open external documentation, or piece together intent from doc comments to make that decision.

**Why this priority**: Discoverability lives or dies on first-impression copy. A high-quality package with a vague README is invisible. This is a one-time content investment that compounds: every future visitor benefits.

**Independent Test**: A developer who has never heard of Fugue or this package reads only the package README (as rendered on the feed), and after one minute can state in their own words (a) what problem the package solves, (b) which alternative they should pick instead in two specified scenarios, and (c) the rough scope of the four layout primitives.

**Acceptance Scenarios**:

1. **Given** a reader opens the package listing on a NuGet feed, **When** they view the rendered README, **Then** the first paragraph clearly states what the package is for and who should use it.
2. **Given** the reader scrolls past the first paragraph, **When** they reach the Quick Start, **Then** the example code is ≤ 15 lines, copy-pasteable, and produces visible output described in plain text alongside.
3. **Given** the reader continues, **When** they reach the Primitives section, **Then** each of the four layout primitives has at least one minimal example and a one-line description of its intended use.
4. **Given** the reader is comparing alternatives, **When** they read the "When to use this" section, **Then** they find an honest comparison with the underlying rendering library used directly (i.e. "use it directly if you only need X; use this if you need Y").

---

### User Story 5 — Maintainer enforces the SemVer-bump rule via convention captured in project docs (Priority: P3)

A contributor opens a pull request that changes a publicly exposed signature in the package's API surface. The reviewer (human or automated) is reminded by a project-documented rule that this change requires a version bump in the package project file, and the contributor adjusts the PR accordingly before merge.

**Why this priority**: Without a written convention, SemVer drift is inevitable — breaking changes ship as patch versions, consumers' builds break, package trust erodes. The cost of writing the rule once is ~30 minutes; the cost of skipping it is found in months when the first compatibility complaint arrives.

**Independent Test**: A new contributor reads the relevant project doc, opens a contrived PR that modifies the public API surface but does not bump the version, and during review either receives a clear pointer to the convention or is auto-flagged by a check, and after applying the bump the PR is accepted.

**Acceptance Scenarios**:

1. **Given** a contributor changes a publicly exposed type or signature, **When** the reviewer checks the project docs, **Then** the documented convention specifies how to bump the version (major / minor / patch) for that change shape.
2. **Given** the convention is published, **When** any contributor searches the contributor-facing context file for "version" or "SemVer", **Then** they find the rule with examples within 30 seconds.

---

### Edge Cases

- **Stale fork installs the package as v0.x and never updates**: the package's public API may evolve before v1.0; consumers pinned to old prereleases should not block the maintainer from making breaking changes; the README must explicitly state pre-1.0 SemVer rules.
- **Consumer adds the package and the parent product's internal libraries simultaneously**: an end-user could theoretically pull both the published package and a transitive reference to in-tree code with the same namespace. The package contents must be unambiguous about what is public.
- **A reviewer pushes a malformed release tag** (e.g. `adapters-console-vfoo`): the workflow must not crash CI; it must either no-op or fail with a clear error attributable to the tag, and not leave a half-uploaded package on the feed.
- **NuGet credentials are missing or expired** when a release tag is pushed: the workflow must fail loudly at a single named step, not silently succeed having uploaded nothing.
- **Two release tags are pushed in close succession**: each must produce a separate, deterministic build; concurrent jobs must not race on the same artefact path.
- **A consumer runs the package in an environment without a TTY** (e.g. CI logs, redirected stdout): the rendered output should degrade gracefully to plain text where the underlying renderer supports it, not crash.
- **A consumer attempts to render a layout in an AOT-published application**: the package is JIT-validated only at v0.1; this constraint must be explicit on the listing so AOT consumers are not blindsided.
- **The package is unlisted or deprecated later**: the maintainer must have a documented path to unlist a version without affecting the parent product.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The repository MUST produce a single redistributable package artefact whose contents are the compiled library, an embedded README, a license declaration, and standard NuGet metadata (id, version, authors, description, project URL, repository URL, tags).
- **FR-002**: The package's runtime dependency graph MUST contain no project-internal references to other Fugue components; the only transitive dependencies MUST be the external rendering library and its sibling packages already in use by the adapter.
- **FR-003**: The repository MUST continue to build and pass its existing test suite at no less than the pre-feature baseline after the decoupling work is merged.
- **FR-004**: A reproducible local command MUST exist that, when invoked from a fresh clone, produces the package artefact suitable for inspection without requiring publish credentials.
- **FR-005**: A continuous-integration workflow MUST publish the package to the configured NuGet feed when a release tag of the agreed pattern is pushed, and MUST NOT publish on any other event (commit, pull request, branch push, non-matching tag).
- **FR-006**: The publish workflow MUST read credentials from the repository's secrets store (never from committed files), and MUST fail loudly with a single-step attribution if credentials are missing.
- **FR-007**: The package README rendered on the NuGet feed MUST contain, in order: a one-paragraph statement of purpose, a Quick Start example of ≤ 15 lines that produces visible output, one minimal example per layout primitive (Stack, Dock, Grid, Flex), an honest comparison with the underlying rendering library used directly, and a link to the canonical source-of-truth location for deeper documentation.
- **FR-008**: The repository's contributor-facing context document MUST contain a written rule that maps changes to the package's public surface to a version-bump category (major / minor / patch), with at least one example per category.
- **FR-009**: The package version field MUST be independently maintainable from the parent product's version (i.e. bumping the parent does not bump the package and vice versa).
- **FR-010**: The first published version MUST be a pre-release identifier until the parent product has migrated its own rendering paths to the package; only then MAY a stable `0.1.0` be released.
- **FR-011**: The package listing MUST clearly state, at v0.1, that AOT-published consumption is not validated; removal of that warning is permitted only after AOT validation has been added.
- **FR-012**: The package MUST NOT change its namespace or public type names as part of this feature; namespace rebranding is explicitly a v1.0 concern and out of scope here.
- **FR-013**: A smoke test MUST exist that exercises end-to-end consumption: build the package locally, install it into a throwaway project, render a non-trivial layout, and assert the rendered output is non-empty and well-formed; this smoke test MUST run in CI.
- **FR-014**: The single point of coupling between the package and the parent product's surface library MUST be relocated out of the package; the package's source MUST contain no project references to the parent product's code.

### Key Entities

- **Package artefact**: the redistributable unit produced by the build, identified by an id and a version, containing compiled code, embedded documentation, and metadata that describes itself to NuGet consumers.
- **Release tag**: a repository tag of an agreed shape whose push triggers the publish workflow; the tag's text encodes the version to publish, providing a single source of truth.
- **Publish workflow**: a CI job that runs only on release-tag pushes, packs the package, and uploads it to the configured feed using credentials from the repository secrets store.
- **Embedded README**: a single Markdown document inside the package that is rendered by the NuGet feed on the listing page; the canonical first-impression document for the package.
- **Public API surface**: the set of types, modules, and functions that are visible from outside the package — changes to which require version bumps per the documented SemVer convention.
- **SemVer-bump rule**: the written convention in the contributor context document that maps API-surface change shapes to version-bump categories.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An F# developer who has never seen this repository can install the package into a fresh empty F# project and render a non-trivial layout (Stack containing Dock containing two labelled regions) by following only the package README, within 5 minutes from project creation to visible output.
- **SC-002**: Pushing a matching release tag to the repository produces a successfully published package on the configured NuGet feed within 15 minutes, with zero manual intervention required between tag push and feed availability.
- **SC-003**: After the decoupling work is merged, the full repository test suite passes with a total count greater than or equal to the pre-merge baseline on all three supported continuous-integration operating systems.
- **SC-004**: A reader who has never heard of Fugue can describe, in their own words and after only reading the package README, what the package is for and which alternative to pick in at least two scenarios — within 60 seconds of opening the listing.
- **SC-005**: A contributor opening a pull request that changes the public API surface finds the SemVer-bump rule in the contributor context document within 30 seconds of searching for "version" or "SemVer".
- **SC-006**: A pushed tag that does NOT match the release pattern does NOT trigger a publish: zero unintended publishes occur during the feature's first year of operation.
- **SC-007**: The published package's runtime dependency graph contains exactly the external rendering library and its sibling packages — zero project-internal Fugue references appear in the package's dependency listing.
- **SC-008**: Inspection of the package's contents on the feed confirms: an embedded README is present and renders, a license declaration is present, a repository URL is present and resolves, and at least one tag matching the package's domain (terminal / TUI / Spectre / F#) is present.

## Assumptions

- **Naming**: The package id and namespace remain `Fugue.Adapters.Console` for v0.x. A neutral rebrand is deferred to a hypothetical v1.0 release with a compatibility shim for downstream consumers — that work is explicitly NOT part of this feature.
- **Renderer-to-DrawOp bridge**: The single point of coupling between the adapter and the parent product's surface library — a method that maps a composition to a Fugue-specific draw operation — will be relocated out of the package and into the consuming Fugue REPL code, so the package's dependency graph contains zero internal references. This is the natural strangler-fig path: the package owns the abstract composition type; consumers are free to map it to their own draw-op shape.
- **Single package vs split**: A single monolithic package is the v0.x default. Splitting into a "core wrapper" package and a "layout framework" package is a possible v0.2+ evolution if and only if real-world consumers ask for the wrapper alone (e.g. their TUI does not need the layout primitives). Until that signal arrives, splitting adds maintenance overhead without observable benefit.
- **Independent versioning**: The package's version field is decoupled from the parent product's binary version. The first published version is a pre-release identifier; the first stable `0.1.0` is published only after the parent product has migrated its own rendering paths onto the package (Phase 4 follow-up work in the parent product, tracked separately and outside this feature's scope).
- **AOT consumption deferred**: The package does NOT declare AOT compatibility metadata at v0.1; the listing explicitly warns that AOT-published consumption is unvalidated. AOT validation is a follow-up tracked separately and conditional on consumer demand.
- **First release is a dry run**: The first tag push targets a local or staging feed (not the public one) for one full release cycle, to flush out workflow defects before any externally observable publish occurs.
- **License**: The package license is the same as the parent repository (MIT). No legal review is required beyond that match.
- **Existing tests are the safety net**: All existing adapter tests already covering the package's surface continue to run unchanged inside the monorepo; no separate test project is created for the published package. The smoke test (FR-013) is additive, not a replacement.
- **Tag pattern**: A specific tag pattern conveys both intent and the version number in a single push. The pattern is implementation-defined and documented in the contributor context document alongside the SemVer rule.
- **No retroactive support for prior rendering-library versions**: The package's supported underlying-library version range matches the parent product's range. When the parent bumps the library, the package bumps in step.

## Dependencies

- **External**: NuGet feed account and publish credentials (initially a local or staging feed for the dry run; later the public feed once the dry run validates the workflow).
- **Internal — out of scope but blocks the stable release**: The parent product's migration of its REPL render paths onto the package (Phase 4 work in the parent product); a stable `0.1.0` is conditional on that migration completing successfully.
- **Internal — required for the package to make sense**: The adapter's existing public API surface is stable enough to publish; this is established by the prior Phase 1 / Phase 2 / Phase 3 features and is not re-litigated here.

## Out of Scope

- Extracting the package into a separate Git repository (rejected — monorepo publish is the chosen model).
- Renaming the namespace (deferred to a hypothetical v1.0 with compatibility shim).
- Validating AOT-published consumption (deferred until consumer demand surfaces).
- Splitting into two packages (deferred until consumer demand surfaces).
- Publishing to the public NuGet feed at this feature's completion (the first feature-completing release is a dry-run on a local or staging feed; public release is a follow-up after the dry-run validates the workflow).
- The parent product's migration of its render paths onto the package (Phase 4 work, tracked separately).
- Authoring contributor onboarding documentation beyond the SemVer-bump rule (general onboarding lives elsewhere in the repository).
