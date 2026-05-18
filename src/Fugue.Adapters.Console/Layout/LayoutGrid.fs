namespace Fugue.Adapters.Console.Layout

open Fugue.Adapters.Console

// ColumnSize DU — declared here (implementation); signature in LayoutGrid.fsi.
type ColumnSize =
    | Fixed of width: int
    | Auto
    | Star of ratio: int

// RowSize DU — declared here (implementation); signature in LayoutGrid.fsi.
type RowSize =
    | Fixed of height: int
    | Auto
    | Star of ratio: int

// Internal record holding validated LayoutGrid data.
// Must be `internal` (not `private`) so the LayoutGrid constructor arg is
// at least as accessible as the LayoutGrid type itself.
type internal LayoutGridData =
    { Columns: ColumnSize list
      Rows:    RowSize list
      Cells:   Composition list list }   // outer = rows, inner = cells per row

/// Opaque sealed LayoutGrid type. Callers obtain values only via `LayoutGrid.create`.
[<Sealed>]
type LayoutGrid (data: LayoutGridData) =
    member internal _.Data = data

module LayoutGrid =

    // Validate Fixed/Star sizing DUs for columns.
    let private validateColumnSizes (cols: ColumnSize list) : Result<unit, RenderError> =
        cols
        |> List.tryPick (fun c ->
            match c with
            | ColumnSize.Fixed w when w < 1 ->
                Some (RenderError.InvalidArgument ("LayoutGrid", "Fixed column width must be ≥ 1"))
            | ColumnSize.Star r when r < 1 ->
                Some (RenderError.InvalidArgument ("LayoutGrid", "Star column ratio must be ≥ 1"))
            | _ -> None)
        |> function
           | Some e -> Error e
           | None   -> Ok ()

    // Validate Fixed/Star sizing DUs for rows.
    let private validateRowSizes (rows: RowSize list) : Result<unit, RenderError> =
        rows
        |> List.tryPick (fun rs ->
            match rs with
            | RowSize.Fixed h when h < 1 ->
                Some (RenderError.InvalidArgument ("LayoutGrid", "Fixed row height must be ≥ 1"))
            | RowSize.Star ratio when ratio < 1 ->
                Some (RenderError.InvalidArgument ("LayoutGrid", "Star row ratio must be ≥ 1"))
            | _ -> None)
        |> function
           | Some e -> Error e
           | None   -> Ok ()

    let create
            (columns: ColumnSize list)
            (rows:    RowSize list)
            (cells:   Composition list list)
            : Result<LayoutGrid, RenderError> =

        if columns.IsEmpty then
            Error (RenderError.EmptyComposition "LayoutGrid: at least one column required")
        elif rows.IsEmpty then
            Error (RenderError.EmptyComposition "LayoutGrid: at least one row required")
        elif cells.Length <> rows.Length then
            // Spec data-model.md §506: cells.Length ≠ rows.Length → InvalidArgument.
            // Must be checked before per-row ragged validation to avoid IndexOutOfRangeException
            // in LayoutGridRenderable.Render when the render loop indexes cells.[i].
            Error (RenderError.InvalidArgument (
                "LayoutGrid",
                sprintf "expected %d rows of cells, got %d" rows.Length cells.Length))
        else
            // Validate each row has exactly columns.Length cells.
            let colCount = columns.Length
            let raggedRow =
                cells
                |> List.mapi (fun i row ->
                    if row.Length <> colCount then
                        Some (RenderError.InvalidArgument (
                            "LayoutGrid",
                            $"row {i} has {row.Length} cells, expected {colCount}"))
                    else None)
                |> List.tryPick id
            match raggedRow with
            | Some e -> Error e
            | None ->
                match validateColumnSizes columns with
                | Error e -> Error e
                | Ok () ->
                    match validateRowSizes rows with
                    | Error e -> Error e
                    | Ok () ->
                        // Depth check: all cells flattened, max depth must be ≤ 98
                        // (LayoutGrid itself adds 1, so total depth ≤ 99; depth ≤ 100 is the limit).
                        // Any cell with depth > 98 → total > 99 → reject (same guard as Dock/Stack).
                        let flatCells = cells |> List.concat
                        let maxDepth = LayoutDepth.maxChildDepth flatCells
                        if maxDepth >= 100 then
                            Error (RenderError.InvalidArgument ("Layout", "depth exceeds 100"))
                        else
                            Ok (LayoutGrid { Columns = columns; Rows = rows; Cells = cells })

    // Assembly-internal accessors for LayoutComposition.fs lowering.
    let internal columns (g: LayoutGrid) = g.Data.Columns
    let internal rows    (g: LayoutGrid) = g.Data.Rows
    let internal cells   (g: LayoutGrid) = g.Data.Cells
