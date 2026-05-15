namespace Fugue.Adapters.Console

/// Recursive layout tree. v1 (US1 MVP) only includes the `Leaf` case;
/// US2 (Phase 4) extends with `Stack`, `Padded`, `Panel`, `Columns`,
/// `Aligned`, `Table`. See data-model.md §5.
///
/// Adding a new case in US2 forces every Renderer match arm to update
/// at compile time (Principle I leverage).
type Composition =
    | Leaf of Primitive
