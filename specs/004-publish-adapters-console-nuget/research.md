# Phase 0: Research — Publish Fugue.Adapters.Console as NuGet Package

**Feature**: [spec.md](./spec.md) | **Plan**: [plan.md](./plan.md) | **Date**: 2026-05-19

This document resolves the four open architectural questions (R-1..R-4) flagged in the spec's Assumptions section. Each finding follows the standard format: **Decision / Rationale / Alternatives considered**.

---

## R-1 — Disposition of `Renderer.toDrawOp` (the single `Fugue.Surface` coupling point)

**Question**: The only code in `src/Fugue.Adapters.Console/` that references `Fugue.Surface` is one method in `Renderer.fs`/`Renderer.fsi`:

```fsharp
let toDrawOp (ctx: RenderContext) (composition: Composition) : Result<Fugue.Surface.DrawOp, RenderError> =
    toRawAnsi ctx composition
    |> Result.map Fugue.Surface.DrawOp.RawAnsi
```

To publish the package with zero internal references (FR-002, FR-014), this method must move OR be dropped OR be sealed-internal. Three options:

- **(a) Drop completely**: callers convert `string` ANSI to `DrawOp.RawAnsi` themselves at the call site.
- **(b) Relocate to `Fugue.Cli.RenderBridge`**: a new ~20-line module inside the consuming Fugue REPL code.
- **(c) Mark `internal` + `InternalsVisibleTo Fugue.Cli`**: keep the method in the package source but hide it from the public NuGet API surface.

**Decision**: **(b) Relocate to `Fugue.Cli.RenderBridge`**.

**Rationale**:

- Option (a) "drop completely" leaks abstraction: every call site must repeat `match toRawAnsi ctx c with | Ok ansi -> Ok (DrawOp.RawAnsi ansi) | Error e -> Error e`. That's 6 lines repeated at every Fugue render path — exactly the kind of glue a small bridge module should absorb. There is one canonical mapping; we should have one canonical name for it.
- Option (c) "internal + InternalsVisibleTo" is technically clean but semantically wrong: the method is *not* part of the package's intended public surface, but it is also not part of its internal machinery. It's a Fugue-specific *consumer-side* concern. Burying it inside the package's source tree mis-signals its ownership: the package's tests would have to test it, the package's docs would have to explain why, and any future contributor extracting the package to a separate repo (hypothetically v2.0) would have to find and remove it.
- Option (b) "relocate to Fugue.Cli.RenderBridge" is the textbook **strangler-fig** placement: the package owns the abstract `Composition` type; consumers map it to their own draw-op shape in their own code. The Fugue REPL is one specific consumer; its mapping lives in `Fugue.Cli` where it can evolve independently of the package release cycle.

**Mechanics**:

- New files: `src/Fugue.Cli/RenderBridge.fsi` (declaration), `src/Fugue.Cli/RenderBridge.fs` (body), `tests/Fugue.Tests/RenderBridgeTests.fs` (happy + error tests).
- Module signature:
  ```fsharp
  module Fugue.Cli.RenderBridge

  open Fugue.Adapters.Console
  open Fugue.Surface

  /// Render a Composition to ANSI bytes wrapped as a DrawOp.RawAnsi, suitable for
  /// posting to the Fugue Surface actor. The bridge owes nothing to the package;
  /// it is the Fugue REPL's chosen mapping from the package's abstract output
  /// (string ANSI) to Fugue's internal draw-op representation.
  val toDrawOp :
      ctx: RenderContext ->
      composition: Composition ->
          Result<DrawOp, RenderError>
  ```
- Call sites updated: `LayoutHost.fs` (currently calls `Renderer.toRawAnsi` and constructs `DrawOp.RawAnsi` inline — switch to `RenderBridge.toDrawOp`). Any other Fugue-side caller (none today, since legacy render paths bypass the package) updated similarly when migrated in Phase 4.
- Removed from package: 4 lines in `Renderer.fs`, 4 lines in `Renderer.fsi`.

**Alternatives considered**: (a) and (c) above; both rejected per rationale.

---

## R-2 — Single monolithic package vs split into `Fugue.Adapters.Console` + `Fugue.Adapters.Console.Layout`

**Question**: Should we ship one package containing everything (Phase 1+2 wrapper + Phase 3 Layout), or split into a "core wrapper" package and an additive "layout framework" package that depends on it?

**Decision**: **Single monolithic package** at v0.x. Split deferred to v0.2+ contingent on consumer demand.

**Rationale**:

- **Maintenance overhead**: Two packages require two `<Version>` fields, two release-tag patterns, two CI workflows, two READMEs, and dependency-version coordination (package B must specify a compatible version range of package A). For a single-maintainer project, that overhead is non-trivial and recurring.
- **Discoverability**: A single package name on a NuGet feed is one search hit, not two. Splitting hurts SEO for the smaller package because consumers are likely to search for the layout primitive ("F# stack dock layout terminal") and find only the monolith.
- **Bandwidth cost is negligible**: The complete adapter compiles to a small `.dll`; the Layout subset would only marginally shrink the package size. We are not in the territory where "downloading 200 KB you don't need" is a real concern.
- **YAGNI**: No consumer has asked for the wrapper alone. Speculatively splitting is exactly the optimisation Fugue's "Don't add features beyond what the task requires" guidance warns against.
- **Reversibility**: If a real signal arrives (someone files an issue: "I want Stack/Dock/Grid/Flex for an existing Spectre-using TUI, but I don't want to take the wrapper layer because I already have my own"), we can ship `Fugue.Adapters.Console.Layout` as an additive split in v0.2 and continue maintaining the monolith. The cost of the v0.x monolith is zero in that scenario.

**Mechanics**:

- One `Fugue.Adapters.Console.fsproj` with `<IsPackable>true</IsPackable>`.
- One `PackageId=Fugue.Adapters.Console`.
- One README inside `src/Fugue.Adapters.Console/`.
- One workflow `.github/workflows/publish-adapters-console.yml`.

**Alternatives considered**:

- **Split-first** (two packages from v0.1): rejected for the maintenance and discoverability reasons above.
- **Three packages** (Primitives / Widgets / Layout): rejected as further over-engineering; the inter-dependency graph would be a thin sandwich, and consumers would have to pick which two-of-three they want for any non-trivial use case.

---

## R-3 — SemVer policy: tracks Fugue product version or independent?

**Question**: The Fugue binary has its own version (currently 0.x). Does the NuGet package follow the binary's version, or maintain its own?

**Decision**: **Independent versioning**, starting at `0.1.0-prerelease`. First stable `0.1.0` only after parent product migrates its render paths to consume the package (Phase 4 dependency captured in spec FR-010 and Dependencies section).

**Rationale**:

- **Different audiences, different release cadences**: The Fugue product is shipped to end-users who run the binary; cadence is dictated by user-facing feature delivery. The package is shipped to F# developers who consume the API; cadence is dictated by API-surface stability and backward compatibility. Tying the two means every Fugue patch bump forces a package republish — even when nothing in the adapter changed. The opposite — a package API breaking change — must NOT be tied to Fugue binary bumps that have nothing to do with the adapter.
- **Pre-release tail is essential**: `0.1.0-prerelease` (or equivalent NuGet pre-release suffix) signals to consumers: "API may change before 1.0, pin at your own risk." Without the pre-release suffix, anyone consuming `0.1.0` has SemVer-stability expectations the maintainer is not yet ready to honour. Per spec FR-010, the first stable `0.1.0` is gated on Phase 4 (parent product eats its own dog food) so we can confidently promise API stability at that point.
- **Industry standard for .NET monorepo publishing**: ASP.NET Core, `Microsoft.Extensions.*`, FSharp.Core all publish per-package versions decoupled from any "umbrella product" version. This is the established norm; deviating from it confuses consumers who expect NuGet packages to version independently.

**Mechanics**:

- `<Version>0.1.0-prerelease</Version>` in `Fugue.Adapters.Console.fsproj`.
- Release tag pattern: `adapters-console-vX.Y.Z[-suffix]` (e.g. `adapters-console-v0.1.0-prerelease`, later `adapters-console-v0.1.0`).
- CLAUDE.md addendum maps public-API surface changes to bump categories:
  - **Patch** (e.g. `0.1.0` → `0.1.1`): bug fix in implementation, no `.fsi` change.
  - **Minor** (e.g. `0.1.0` → `0.2.0`): additive `.fsi` change (new type, new function, new DU case).
  - **Major** (e.g. `0.x` → `1.0.0`): removed/renamed exported symbol, signature change, namespace rebrand.
  - Pre-1.0 special: SemVer permits breaking changes in minor bumps for `0.x` — the addendum documents this so contributors don't unduly delay necessary breaks.

**Alternatives considered**:

- **Tracked to Fugue binary**: rejected — couples cadences with nothing in common.
- **Calendar versioning (CalVer)** (e.g. `2026.5.19`): rejected — CalVer suits products where "what version are you on?" is the maintainer's preferred phrasing; for libraries, SemVer-encoded API compatibility wins.

---

## R-4 — AOT-friendliness package metadata (`<IsAotCompatible>` attribute)

**Question**: .NET 8+ supports `<IsAotCompatible>true</IsAotCompatible>` in `.fsproj` to declare AOT-compatibility and surface trim warnings during build. Should `Fugue.Adapters.Console` declare itself AOT-compatible?

**Decision**: **Do NOT declare AOT compatibility at v0.1**. Document the deferral in the README. Re-evaluate if a consumer files an issue requesting AOT support.

**Rationale**:

- **The package is JIT-only by design** (per Fugue's own AOT/JIT split, established 2026-05-02): `Fugue.Adapters.Console` lives in the JIT closure because Spectre.Console transitively triggers trim warnings (`IL2026`, `IL3050`) that the Fugue project has deliberately quarantined to the JIT-only `Fugue.Cli`/`Fugue.Surface` portion. Declaring `<IsAotCompatible>true</IsAotCompatible>` on the package would either (a) light up dozens of trim warnings at every consumer's build (bad UX, false alarms), or (b) require a Fugue-side investment in suppressing/annotating each warning — work that is unjustified until a consumer asks for AOT.
- **Honest signalling is better than false promise**: Marking the package AOT-compatible while transitively pulling in Spectre.Console (which is not AOT-validated by its own authors) would set up consumers for unexpected failures at their own AOT-publish step. The package README MUST instead state explicitly: "v0.1 is JIT-validated only. AOT publish is not supported; if you need it, please file an issue."
- **Re-evaluable on demand**: If even one issue arrives asking for AOT, we can: (i) confirm AOT trim closure on the package source itself (we control it), (ii) check Spectre's then-current AOT story (the upstream may have improved), (iii) decide if the work is justified. The cost of waiting is one explicit README sentence; the cost of declaring prematurely is broken consumer builds and bug-report triage.

**Mechanics**:

- No `<IsAotCompatible>` line in the `.fsproj`.
- README "When NOT to use this" section explicitly lists AOT publish as a current non-target.
- Follow-up GitHub issue template: "AOT-publish support" — created if/when the first consumer request lands, NOT created speculatively.

**Alternatives considered**:

- **Declare `<IsAotCompatible>true</IsAotCompatible>` immediately**: rejected — sets up trim-warning ambush for consumers.
- **Declare `<IsAotCompatible>false</IsAotCompatible>` explicitly**: technically legal but `false` is the default; explicit `false` adds noise without affecting behaviour. Omitting the line is the cleaner expression.

---

## Summary of decisions

| Question | Decision | Spec impact |
|---|---|---|
| R-1: `Renderer.toDrawOp` disposition | Relocate to `Fugue.Cli.RenderBridge` | Confirms FR-014 mechanics; new file added to plan |
| R-2: Mono vs split package | Single monolithic package at v0.x | Confirms FR-001 (single artefact); split deferred |
| R-3: SemVer policy | Independent, starts `0.1.0-prerelease` | Confirms FR-009, FR-010; tag pattern fixed as `adapters-console-vX.Y.Z[-suffix]` |
| R-4: AOT-friendliness metadata | Do NOT declare; explicit README warning instead | Confirms FR-011; README structure must include the warning |

All four decisions were resolvable through informed defaults; no consumer-survey or dual-agent debate was required. Should `/speckit-implement` surface contraindicating evidence (e.g. the strangler-fig relocation breaks a hidden consumer), this document is the place to record the re-litigation.

## Best-practice references consulted

- **.NET monorepo NuGet publishing**: aspnetcore (Microsoft), `Microsoft.Extensions.*`, FsToolkit.ErrorHandling — all use independent `<Version>` per packable project + tag-driven CI publish.
- **PackageReadmeFile + embedded README**: NuGet docs require the file to be present in the `.nupkg` at a known path; `<PackageReadmeFile>README.md</PackageReadmeFile>` + `<None Include="README.md" Pack="true" PackagePath="\" />` is the standard incantation.
- **GitHub Actions tag-triggered publish**: `on: push: tags: ['adapters-console-v*']` is the standard pattern; isolating to a tag prefix avoids accidental publishes from any other tag.
- **SemVer for pre-1.0 .NET libraries**: SemVer spec §4 permits breaking changes in 0.x minor bumps; this is the convention adopted by FsHttp, FsToolkit.ErrorHandling, and similar libraries.
