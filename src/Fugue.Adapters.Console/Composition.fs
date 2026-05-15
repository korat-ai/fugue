namespace Fugue.Adapters.Console

/// Border style for Panel compositions. Maps to Spectre's `BoxBorder`.
type Border =
    | None_
    | Square
    | Rounded
    | Heavy

/// Horizontal alignment for Aligned compositions. Maps to Spectre's
/// `HorizontalAlignment`. Named with a suffix to avoid clashing with
/// Spectre's type in files that open both namespaces.
type Alignment =
    | LeftAlign
    | CentreAlign
    | RightAlign

/// Recursive layout tree. Leaf wraps a single Primitive; all other cases
/// are composition nodes. Immutable — a Composition value can be cached or
/// memoised. See data-model.md §5.
///
/// Adding a new case forces every Renderer match arm to update at compile
/// time (Principle I leverage via exhaustive matching).
type Composition =
    /// Leaf — wraps a single Primitive.
    | Leaf    of Primitive
    /// Vertically stacked children. Maps to Spectre's `Rows`.
    | Stack   of Composition list
    /// Inner content padded by N cells left/right and M rows top/bottom.
    /// Smart constructor `Composition.padded` rejects negative values.
    | Padded  of left: int * right: int * top: int * bottom: int
              * inner: Composition
    /// Border-decorated panel, optional header text. Maps to Spectre's
    /// `Panel` with optional `PanelHeader`.
    | Panel   of border: Border * header: SafeText option
              * inner: Composition
    /// Horizontal columns. Width hints as ratios summing to ≤ 1.0.
    /// Smart constructor `Composition.columns` validates ratios.
    | Columns of children: (float * Composition) list
    /// Alignment within the available width. Maps to Spectre's `Align`.
    | Aligned of Alignment * inner: Composition
    /// Markdown-style table. Headers determine the column count; every body
    /// row must have the same count. Smart constructor validates.
    | Table   of headers: Composition list * rows: Composition list list

/// Smart constructors for Composition nodes that can fail structurally.
/// Non-validating constructors (`stack`, `panel`, `aligned`) build the DU
/// directly; validating ones (`padded`, `columns`, `table`) return Result.
module Composition =

    /// Vertically stack children. Empty list is allowed (renders as nothing).
    let stack (children: Composition seq) : Composition =
        Stack (children |> Seq.toList)

    /// Pad inner content. Returns Error if any padding value is negative
    /// (negative padding is meaningless and indicates a caller bug).
    let padded
        (left:   int)
        (right:  int)
        (top:    int)
        (bottom: int)
        (inner:  Composition)
        : Result<Composition, RenderError> =
        if left < 0 || right < 0 || top < 0 || bottom < 0 then
            Error (RenderError.RenderFailed ("Padded", "padding values must be non-negative"))
        else
            Ok (Padded (left, right, top, bottom, inner))

    /// Wrap inner content in a border-styled panel with an optional header.
    /// No structural validation — header is optional and all border styles
    /// are valid at construction time.
    let panel
        (border: Border)
        (header: SafeText option)
        (inner:  Composition)
        : Composition =
        Panel (border, header, inner)

    /// Lay out children in horizontal columns. Each child is given a ratio
    /// (0.0–1.0) of the available width; ratios MUST sum to ≤ 1.0.
    /// Returns Error if any ratio is negative, > 1.0, or the sum exceeds 1.0.
    let columns (children: (float * Composition) seq) : Result<Composition, RenderError> =
        let list = children |> Seq.toList
        let anyNegative = list |> List.exists (fun (r, _) -> r < 0.0)
        let anyOverOne  = list |> List.exists (fun (r, _) -> r > 1.0)
        let total       = list |> List.sumBy fst
        if anyNegative then
            Error (RenderError.RenderFailed ("Columns", "column ratios must be non-negative"))
        elif anyOverOne then
            Error (RenderError.RenderFailed ("Columns", "individual column ratio must be ≤ 1.0"))
        elif total > 1.0 + 1e-9 then
            Error (RenderError.RenderFailed ("Columns", $"column ratios sum to {total:F4} which exceeds 1.0"))
        else
            Ok (Columns list)

    /// Align inner content within the available width.
    let aligned (alignment: Alignment) (inner: Composition) : Composition =
        Aligned (alignment, inner)

    /// Build a table from a header row and body rows. Every body row must
    /// have the same column count as the header row; ragged rows are
    /// rejected at build time. Empty header is also rejected.
    let table
        (headers: Composition seq)
        (rows:    Composition seq seq)
        : Result<Composition, RenderError> =
        let hList = headers |> Seq.toList
        let rList = rows |> Seq.map Seq.toList |> Seq.toList
        if hList.IsEmpty then
            Error (RenderError.EmptyComposition "Table: header row must not be empty")
        else
            let colCount = hList.Length
            let ragged = rList |> List.tryFind (fun row -> row.Length <> colCount)
            match ragged with
            | Some row ->
                Error (RenderError.RenderFailed ("Table",
                    $"ragged row: expected {colCount} columns, got {row.Length}"))
            | None ->
                Ok (Table (hList, rList))
