namespace Fugue.Surface

/// Computation-expression builder that assembles a DrawOp list via yield.
/// Using Yield/YieldFrom/Combine/Delay/Zero + For so that `if`/`for`/`let`
/// all work naturally alongside yielded DrawOp values.
///
/// Usage:
///   surface {
///       yield paint Region.StatusBarLine1 cwd Style.dim
///       if streaming then
///           yield paint Region.ThinkingLine "thinking..." Style.dim
///       else
///           yield eraseLine Region.ThinkingLine
///   }
///
/// The `paint`, `eraseLine`, `moveTo`, `append` names are plain functions
/// (not custom operations) so they can be used freely inside `if`/`for`.
type SurfaceBuilder() =
    member _.Yield(op: DrawOp)          : DrawOp list = [op]
    member _.YieldFrom(ops: DrawOp list): DrawOp list = ops
    member _.Combine(a: DrawOp list, b: DrawOp list) : DrawOp list = a @ b
    member _.Delay(f: unit -> DrawOp list) : DrawOp list = f ()
    member _.Zero() : DrawOp list = []
    member _.For(source: seq<'a>, body: 'a -> DrawOp list) : DrawOp list =
        source |> Seq.toList |> List.collect body
    member _.Run(ops: DrawOp list) : DrawOp list = ops

[<AutoOpen>]
module SurfaceBuilderInstance =
    /// The singleton builder instance.
    let surface = SurfaceBuilder()

/// Helper functions used inside `surface { yield ... }` blocks.
/// These are plain F# functions, not CE custom operations, so they
/// compose freely with `if`/`for`/`match` without FS3086 restrictions.
[<AutoOpen>]
module SurfaceOps =
    let inline paint (region: Region) (text: string) (style: Style) : DrawOp =
        DrawOp.Paint(region, text, style)

    let inline paintDefault (region: Region) (text: string) : DrawOp =
        DrawOp.Paint(region, text, Style.normal)

    let inline eraseLine (region: Region) : DrawOp =
        DrawOp.EraseLine region

    let inline moveTo (region: Region) (col: int) : DrawOp =
        DrawOp.MoveTo(region, col)

    let inline append (text: string) : DrawOp =
        DrawOp.Append text

    let inline saveAndRestore (inner: DrawOp list) : DrawOp =
        DrawOp.SaveAndRestore inner

    let inline resetScrollRegion () : DrawOp =
        DrawOp.ResetScrollRegion
