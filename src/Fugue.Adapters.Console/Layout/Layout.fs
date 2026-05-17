namespace Fugue.Adapters.Console.Layout

/// Axis along which a layout primitive distributes its children.
type Orientation =
    | Vertical
    | Horizontal

/// Alignment on the cross axis (perpendicular to the main layout axis).
/// Named `End_` with a trailing underscore to avoid clash with the F# keyword `end`.
type CrossAxisAlignment =
    | Start
    | Center
    | End_
    | Stretch

/// Policy for content that exceeds the available dimension.
type Overflow =
    | Clip
    | Strict
    | WrapNext
