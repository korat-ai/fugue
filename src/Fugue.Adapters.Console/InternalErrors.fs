namespace Fugue.Adapters.Console

// =============================================================================
// Assembly-internal exception types. No .fsi companion file — these are
// visible to all downstream compilation units in this assembly but are NOT
// exported as part of the public API (SC-004 invariant).
// =============================================================================

/// Internal exception used by layout IRenderable implementations (FlexRenderable
/// and future layout renderables) to propagate a typed `RenderError` through
/// Spectre's `IRenderable.Render → IEnumerable<Segment>` path back to
/// `Renderer.toRawAnsi`.
///
/// The renderer catches this BEFORE the generic `exn` catch and re-surfaces
/// the contained `RenderError` directly — preserving `LayoutOverflow` rather
/// than wrapping it in `RenderFailed`. See `Renderer.toRawAnsi`.
///
/// Design note: F# `.fsi` module signatures seal a module completely — items
/// in the `.fs` but not the `.fsi` are inaccessible even within the same
/// assembly. This file has no `.fsi` companion, so the exception is accessible
/// to all compilation units that come after it in the project file.
exception LayoutRenderErrorException of RenderError
