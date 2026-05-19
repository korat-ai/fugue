// =============================================================================
// Contract: Fugue.Cli.RenderBridge
// Feature: 005-cli-layout-migration
// Status: design — to be implemented in PR P1
//
// Sole bridge from the framework's abstract output (Composition → ANSI string)
// to Fugue's internal DrawOp representation consumed by the Surface actor.
//
// Owns the one mapping; the framework package (Fugue.Adapters.Console) drops
// its own Renderer.toDrawOp as a side effect of PR P1, satisfying the
// deferred Phase 5 condition that the package contain zero Fugue-internal
// references.
// =============================================================================

module Fugue.Cli.RenderBridge

open Fugue.Adapters.Console
open Fugue.Surface

/// Render a Composition to ANSI bytes wrapped as DrawOp.RawAnsi, suitable
/// for posting to the Fugue Surface actor.
///
/// Mechanics: identical to the current Renderer.toDrawOp implementation in
/// the package, relocated here so the package no longer needs to know about
/// Fugue.Surface.
///
/// Failure modes: any error from Renderer.toRawAnsi propagates unchanged
/// (degenerate-width context, composition lowering failure, etc.).
///
/// Determinism: deterministic for a given (ctx, composition) pair. Used by
/// snapshot tests under tests/Fugue.Tests/RenderSnapshotTests.fs.
val toDrawOp :
    ctx: RenderContext ->
    composition: Composition ->
        Result<DrawOp, RenderError>
