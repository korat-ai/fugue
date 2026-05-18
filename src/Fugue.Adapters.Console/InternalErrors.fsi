namespace Fugue.Adapters.Console

/// Internal exception used by layout IRenderable implementations
/// (FlexRenderable, future layout renderables) to propagate a typed
/// `RenderError` through the upstream library's
/// `IRenderable.Render → IEnumerable<Segment>` path back to
/// `Renderer.toRawAnsi`. The renderer catches this BEFORE the generic
/// `exn` arm and re-surfaces the contained `RenderError` directly —
/// preserving `LayoutOverflow` rather than wrapping it in
/// `RenderFailed`. Assembly-internal; never observed by external
/// consumers (per FR-005 / SC-004).
exception internal LayoutRenderErrorException of RenderError
