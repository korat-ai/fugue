namespace Fugue.Adapters.Console.Layout

open Fugue.Adapters.Console

// Internal depth-check helper shared by all layout primitives.
// Not part of the public API — internal to the assembly only.
module internal LayoutDepth =
    val layoutDepth    : Composition -> int
    val maxChildDepth  : Composition list -> int

/// Axis along which a layout primitive distributes its children.
type Orientation =
    /// Children flow top-to-bottom.
    | Vertical
    /// Children flow left-to-right.
    | Horizontal

/// Controls how children are positioned along the axis perpendicular to
/// the layout primitive's main orientation.
///
/// For a Vertical layout the cross axis is horizontal (left/centre/right).
/// For a Horizontal layout the cross axis is vertical (top/middle/bottom).
///
/// Named `End_` with a trailing underscore to avoid clash with the F# keyword `end`.
type CrossAxisAlignment =
    /// Align children to the leading edge of the cross axis.
    | Start
    /// Centre children on the cross axis.
    | Center
    /// Align children to the trailing edge of the cross axis.
    | End_
    /// Expand children to fill the full cross-axis extent.
    | Stretch

/// Policy for content that exceeds the available dimension.
/// Used by Flex and other layout primitives that can overflow.
type Overflow =
    /// Content that exceeds the available dimension is silently clipped.
    | Clip
    /// Content that exceeds the available dimension returns
    /// `Error (LayoutOverflow (...))` — the caller controls the failure path.
    | Strict
    /// Content wraps to the next row or column along the main axis.
    | WrapNext
