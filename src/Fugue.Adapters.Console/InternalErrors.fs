namespace Fugue.Adapters.Console

// Assembly-internal exception type for layout IRenderable implementations.
// Companion .fsi declares it `internal`, satisfying FR-012 per-module sealing
// while keeping it invisible to external consumers (SC-004 invariant).
exception LayoutRenderErrorException of RenderError
