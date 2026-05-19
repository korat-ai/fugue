namespace Fugue.Adapters.Console

// =============================================================================
// Data-display primitives — Phase 2 (US2)
//
// Types and module implementations for the eight data-display widgets.
// Each widget type is declared HERE (type + module in the same file, as
// required by the F# rule that prohibits a type and module with the same name
// from existing in different files of the same namespace — FS0250).
//
// Lowering: each widget's `toComposition` lowers through the Renderable bridge:
//   spectreObject |> Renderable.fromSpectre |> Composition.ofRenderable
// Zero new Renderer arms needed — the existing Foreign arm handles all eight.
// =============================================================================

module private DataDisplaysHelpers =

    type SpectreColor   = Spectre.Console.Color
    type SpectreHAlign  = Spectre.Console.HorizontalAlignment
    type SpectreJustify = Spectre.Console.Justify
    type IRenderable    = Spectre.Console.Rendering.IRenderable

    let inline lower (r: IRenderable) : Composition =
        r |> Renderable.fromSpectre |> Composition.ofRenderable

    let toSpectreColor (c: Colour) : SpectreColor =
        match c with
        | Colour.Default        -> SpectreColor.Default
        | Colour.Rgb (r, g, b)  -> SpectreColor (r, g, b)
        | Colour.Theme _        -> SpectreColor.Default

    let toSpectreHAlign (a: Alignment) : SpectreHAlign =
        match a with
        | Alignment.LeftAlign   -> SpectreHAlign.Left
        | Alignment.CentreAlign -> SpectreHAlign.Center
        | Alignment.RightAlign  -> SpectreHAlign.Right

    let toSpectreJustify (a: Alignment) : SpectreJustify =
        match a with
        | Alignment.LeftAlign   -> SpectreJustify.Left
        | Alignment.CentreAlign -> SpectreJustify.Center
        | Alignment.RightAlign  -> SpectreJustify.Right

    let validateChartValue (node: string) (value: float) : Result<unit, RenderError> =
        if System.Double.IsNaN value || System.Double.IsInfinity value || value < 0.0 then
            Error (RenderError.InvalidArgument (node, "value must be non-negative and finite"))
        else
            Ok ()

    let validateChartWidth (node: string) (width: int) : Result<unit, RenderError> =
        if width <= 0 then
            Error (RenderError.InvalidArgument (node, "width must be positive"))
        elif width > 1000 then
            Error (RenderError.InvalidArgument (node, "width must be ≤ 1000"))
        else
            Ok ()

// ──────────────────────────────────────────────────────────────────────────────
// Tree (§3.2.1)
// ──────────────────────────────────────────────────────────────────────────────

/// Builder node for tree construction. Constructed only via
/// `TreeNode.leaf` / `TreeNode.branch`.
type TreeNode =
    private { TnLabel: string; TnChildren: TreeNode list }

/// Immutable tree, ready to render. Depth-bounded.
type Tree =
    private { Root: TreeNode }

module TreeNode =

    let leaf (label: SafeText) : TreeNode =
        { TnLabel = SafeText.unwrap label; TnChildren = [] }

    let branch (label: SafeText) (children: TreeNode list) : TreeNode =
        { TnLabel = SafeText.unwrap label; TnChildren = children }

module Tree =

    let private checkDepth (limit: int) (root: TreeNode) : int option =
        let rec dfs depth (node: TreeNode) =
            if depth > limit then Some depth
            else node.TnChildren |> List.tryPick (dfs (depth + 1))
        dfs 0 root

    let private buildSpectre (root: TreeNode) : Spectre.Console.Tree =
        let tree = Spectre.Console.Tree root.TnLabel
        let rec addChildren (parent: Spectre.Console.TreeNode) (children: TreeNode list) : unit =
            for child in children do
                let sn : Spectre.Console.TreeNode =
                    Spectre.Console.HasTreeNodeExtensions.AddNode (parent, child.TnLabel)
                addChildren sn child.TnChildren
        for child in root.TnChildren do
            let sn : Spectre.Console.TreeNode =
                Spectre.Console.HasTreeNodeExtensions.AddNode (tree, child.TnLabel)
            addChildren sn child.TnChildren
        tree

    let create (root: TreeNode) : Result<Tree, RenderError> =
        match checkDepth 1000 root with
        | Some depth ->
            Error (RenderError.InvalidArgument ("Tree", $"depth {depth} exceeds 1000"))
        | None ->
            Ok { Root = root }

    let build (root: TreeNode) : Result<Tree, RenderError> = create root

    let toComposition (tree: Tree) : Composition =
        DataDisplaysHelpers.lower (buildSpectre tree.Root)

// ──────────────────────────────────────────────────────────────────────────────
// Json (§3.2.2) — requires spectre.console.json 0.49.*
// ──────────────────────────────────────────────────────────────────────────────

/// JSON payload primitive.
type Json =
    private { JsonText: Spectre.Console.Json.JsonText }

module Json =

    let private tenMiB = 10 * 1024 * 1024

    let create (payload: string) : Result<Json, RenderError> =
        // Measure UTF-8 byte length (no allocation; walks UTF-16 once) to match
        // the "10 MiB" wording in data-model.md §3.2.2 and the error message.
        // payload.Length (UTF-16 code units) would allow ~20 MiB of ASCII text.
        let byteLen = System.Text.Encoding.UTF8.GetByteCount payload
        if byteLen > tenMiB then
            Error (RenderError.InvalidArgument ("Json",
                $"payload size {byteLen} bytes exceeds 10 MiB"))
        else
            try
                use _doc = System.Text.Json.JsonDocument.Parse payload
                Ok { JsonText = Spectre.Console.Json.JsonText payload }
            with
            | :? System.Text.Json.JsonException as ex ->
                Error (RenderError.InvalidArgument ("Json", ex.Message))
            | ex ->
                Error (RenderError.RenderFailed ("Json", ex.Message))

    let toComposition (json: Json) : Composition =
        DataDisplaysHelpers.lower json.JsonText

// ──────────────────────────────────────────────────────────────────────────────
// BarChart (§3.2.3)
// ──────────────────────────────────────────────────────────────────────────────

type BarChartItem =
    private { BciLabel: string; BciValue: float; BciColor: Spectre.Console.Color }

type BarChart =
    private { BcTitle: string option; BcItems: BarChartItem list; BcWidth: int }

module BarChartItem =

    let create (label: SafeText) (value: float) (colour: Colour) : Result<BarChartItem, RenderError> =
        match DataDisplaysHelpers.validateChartValue "BarChartItem" value with
        | Error e -> Error e
        | Ok () ->
            Ok { BciLabel = SafeText.unwrap label
                 BciValue = value
                 BciColor = DataDisplaysHelpers.toSpectreColor colour }

module BarChart =

    let private buildSpectre (chart: BarChart) : Spectre.Console.BarChart =
        let bc = Spectre.Console.BarChart ()
        bc.Width <- System.Nullable chart.BcWidth
        match chart.BcTitle with
        | Some t -> bc.Label <- t
        | None   -> ()
        for item in chart.BcItems do
            Spectre.Console.BarChartExtensions.AddItem(bc, item.BciLabel, item.BciValue, System.Nullable item.BciColor) |> ignore
        bc

    let create (title: SafeText option) (items: BarChartItem list) (width: int) : Result<BarChart, RenderError> =
        if items.IsEmpty then
            Error (RenderError.EmptyComposition "BarChart: at least one item required")
        else
            match DataDisplaysHelpers.validateChartWidth "BarChart" width with
            | Error e -> Error e
            | Ok () ->
                Ok { BcTitle = title |> Option.map SafeText.unwrap
                     BcItems = items
                     BcWidth = width }

    let toComposition (chart: BarChart) : Composition =
        DataDisplaysHelpers.lower (buildSpectre chart)

// ──────────────────────────────────────────────────────────────────────────────
// BreakdownChart (§3.2.4)
// ──────────────────────────────────────────────────────────────────────────────

type BreakdownChartItem =
    private { BrkLabel: string; BrkValue: float; BrkColor: Spectre.Console.Color }

type BreakdownChart =
    private { BrkItems: BreakdownChartItem list; BrkWidth: int }

module BreakdownChartItem =

    let create (label: SafeText) (value: float) (colour: Colour) : Result<BreakdownChartItem, RenderError> =
        match DataDisplaysHelpers.validateChartValue "BreakdownChartItem" value with
        | Error e -> Error e
        | Ok () ->
            Ok { BrkLabel = SafeText.unwrap label
                 BrkValue = value
                 BrkColor = DataDisplaysHelpers.toSpectreColor colour }

module BreakdownChart =

    let private buildSpectre (chart: BreakdownChart) : Spectre.Console.BreakdownChart =
        let bc = Spectre.Console.BreakdownChart ()
        bc.Width <- System.Nullable chart.BrkWidth
        for item in chart.BrkItems do
            Spectre.Console.BreakdownChartExtensions.AddItem(bc, item.BrkLabel, item.BrkValue, item.BrkColor) |> ignore
        bc

    let create (items: BreakdownChartItem list) (width: int) : Result<BreakdownChart, RenderError> =
        if items.IsEmpty then
            Error (RenderError.EmptyComposition "BreakdownChart: at least one item required")
        else
            match DataDisplaysHelpers.validateChartWidth "BreakdownChart" width with
            | Error e -> Error e
            | Ok () ->
                Ok { BrkItems = items; BrkWidth = width }

    let toComposition (chart: BreakdownChart) : Composition =
        DataDisplaysHelpers.lower (buildSpectre chart)

// ──────────────────────────────────────────────────────────────────────────────
// Calendar (§3.2.5)
// ──────────────────────────────────────────────────────────────────────────────

type CalendarEvent =
    private { CeDate: System.DateOnly; CeDescription: string }

type Calendar =
    private { CalYear: int; CalMonth: int; CalEvents: CalendarEvent list }

module CalendarEvent =

    let create (date: System.DateOnly) (description: SafeText) : Result<CalendarEvent, RenderError> =
        Ok { CeDate = date; CeDescription = SafeText.unwrap description }

module Calendar =

    let private buildSpectre (cal: Calendar) : Spectre.Console.Calendar =
        let c = Spectre.Console.Calendar (cal.CalYear, cal.CalMonth)
        for ev in cal.CalEvents do
            Spectre.Console.CalendarExtensions.AddCalendarEvent (c, ev.CeDescription, ev.CeDate.Year, ev.CeDate.Month, ev.CeDate.Day) |> ignore
        c

    let create (year: int) (month: int) (events: CalendarEvent list) : Result<Calendar, RenderError> =
        if year < 1 || year > 9999 then
            Error (RenderError.InvalidArgument ("Calendar", "year out of range [1, 9999]"))
        elif month < 1 || month > 12 then
            Error (RenderError.InvalidArgument ("Calendar", "month out of range [1, 12]"))
        else
            Ok { CalYear = year; CalMonth = month; CalEvents = events }

    let toComposition (calendar: Calendar) : Composition =
        DataDisplaysHelpers.lower (buildSpectre calendar)

// ──────────────────────────────────────────────────────────────────────────────
// FigletFont and FigletText (§3.2.6)
// ──────────────────────────────────────────────────────────────────────────────

type FigletFont =
    private { Font: Spectre.Console.FigletFont }

type FigletText =
    private { FtText: string; FtFont: Spectre.Console.FigletFont }

module FigletFont =

    let default_ : FigletFont =
        { Font = Spectre.Console.FigletFont.Default }

    let fromFile (path: string) : Result<FigletFont, RenderError> =
        if not (System.IO.File.Exists path) then
            Error (RenderError.InvalidArgument ("FigletFont", "file not found"))
        else
            try
                Ok { Font = Spectre.Console.FigletFont.Load path }
            with ex ->
                Error (RenderError.RenderFailed ("FigletFont", ex.Message))

    let fromStream (stream: System.IO.Stream) : Result<FigletFont, RenderError> =
        try
            Ok { Font = Spectre.Console.FigletFont.Load stream }
        with ex ->
            Error (RenderError.RenderFailed ("FigletFont", ex.Message))

module FigletText =

    let create (text: SafeText) (font: FigletFont) : Result<FigletText, RenderError> =
        let raw = SafeText.unwrap text
        if raw.Length > 100 then
            Error (RenderError.InvalidArgument ("FigletText", "text length must be ≤ 100"))
        else
            Ok { FtText = raw; FtFont = font.Font }

    let toComposition (figlet: FigletText) : Composition =
        let ft = Spectre.Console.FigletText (figlet.FtFont, figlet.FtText)
        DataDisplaysHelpers.lower ft

// ──────────────────────────────────────────────────────────────────────────────
// GridColumn and Grid (§3.2.7)
// ──────────────────────────────────────────────────────────────────────────────

type GridColumn =
    private { GcPaddingLeft: int; GcPaddingRight: int; GcPaddingTop: int; GcPaddingBottom: int
              GcAlign: Alignment }

type Grid =
    private { GcColumns: GridColumn list; GcRows: Composition list list }

module GridColumn =

    let create (padding: int * int * int * int) (alignment: Alignment) : GridColumn =
        let clamp v = max 0 v
        let (l, r, t, b) = padding
        { GcPaddingLeft   = clamp l
          GcPaddingRight  = clamp r
          GcPaddingTop    = clamp t
          GcPaddingBottom = clamp b
          GcAlign         = alignment }

module Grid =

    let private buildSpectre (grid: Grid) : Spectre.Console.Grid =
        let g = Spectre.Console.Grid ()
        for col in grid.GcColumns do
            let gc = Spectre.Console.GridColumn ()
            gc.Padding <-
                System.Nullable (Spectre.Console.Padding (
                    col.GcPaddingLeft,
                    col.GcPaddingTop,
                    col.GcPaddingRight,
                    col.GcPaddingBottom))
            gc.Alignment <- System.Nullable (DataDisplaysHelpers.toSpectreJustify col.GcAlign)
            g.AddColumn gc |> ignore
        // Render each Composition cell to a plain string, wrap as Spectre.Text.
        // Width 200 / no-colour gives deterministic cell text for Grid layout.
        // Width 200 / no-colour gives deterministic cell text for Grid layout.
        // Height is unconstrained (MaxValue sentinel = no vertical clipping).
        let ctx =
            match RenderContext.create 200 System.Int32.MaxValue false "default" with
            | Ok c    -> c
            // Width=200, height=MaxValue are both > 0 — this branch is unreachable
            // by construction. failwith is appropriate as a programmer-error guard.
            | Error e -> failwith $"DataDisplays: internal RenderContext failure: {e}"
        for row in grid.GcRows do
            let cells =
                row
                |> List.map (fun comp ->
                    match Renderer.toRawAnsi ctx comp with
                    | Ok s    -> Spectre.Console.Text s :> Spectre.Console.Rendering.IRenderable
                    | Error _ -> Spectre.Console.Text "" :> Spectre.Console.Rendering.IRenderable)
                |> List.toArray
            g.AddRow cells |> ignore
        g

    let create (columns: GridColumn list) (rows: Composition list list) : Result<Grid, RenderError> =
        if columns.IsEmpty then
            Error (RenderError.EmptyComposition "Grid: at least one column required")
        else
            let colCount = columns.Length
            let raggedRow = rows |> List.tryFind (fun row -> row.Length <> colCount)
            match raggedRow with
            | Some row ->
                Error (RenderError.InvalidArgument ("Grid",
                    $"ragged rows: expected {colCount} columns, got {row.Length}"))
            | None ->
                Ok { GcColumns = columns; GcRows = rows }

    let toComposition (grid: Grid) : Composition =
        DataDisplaysHelpers.lower (buildSpectre grid)

// ──────────────────────────────────────────────────────────────────────────────
// TextPath (§3.2.8)
// ──────────────────────────────────────────────────────────────────────────────

type TextPath =
    private { TpPath: string; TpJustify: Spectre.Console.Justify }

module TextPath =

    let create (path: SafeText) (alignment: Alignment) : Result<TextPath, RenderError> =
        let raw = SafeText.unwrap path
        // data-model.md §3.2.8: SafeText.ofLiteral null normalises to empty string.
        // An empty path is the degenerate signal — reject with the spec-mandated error.
        if raw.Length = 0 then
            Error (RenderError.InvalidArgument ("TextPath", "null content not permitted"))
        else
            Ok { TpPath    = raw
                 TpJustify = DataDisplaysHelpers.toSpectreJustify alignment }

    let toComposition (tp: TextPath) : Composition =
        let stp = Spectre.Console.TextPath tp.TpPath
        stp.Justification <- System.Nullable tp.TpJustify
        DataDisplaysHelpers.lower stp

// ──────────────────────────────────────────────────────────────────────────────
// RoundedMarkupTable — styled table with Rounded border, markup headers
// ──────────────────────────────────────────────────────────────────────────────

/// Adapter wrapper for building a Spectre rounded-border Table from markup
/// column headers and plain-string rows. Exposed as a standalone module so
/// `Surface.fs` (and future callers) do not need to reference Spectre types
/// directly (FR-011 gate).
///
/// columnDefs: list of (markupHeader, alignment) where alignment is
///   "left" | "center" | "right". Each row is a string array of cells.
module RoundedMarkupTable =

    let toComposition
        (columnDefs: (string * string) list)
        (rows: string[] list)
        : Result<Composition, RenderError> =
        // Validate every alignment string before touching Spectre.
        let inline parseAlign (raw: string) =
            match raw.ToLowerInvariant() with
            | "left"   -> Ok (System.Nullable Spectre.Console.Justify.Left)
            | "right"  -> Ok (System.Nullable Spectre.Console.Justify.Right)
            | "center" -> Ok (System.Nullable Spectre.Console.Justify.Center)
            | other    ->
                Error (RenderError.InvalidArgument (
                    "RoundedMarkupTable",
                    $"unknown alignment '{other}'; expected \"left\", \"right\", or \"center\""))
        let alignResults =
            columnDefs |> List.map (fun (_, a) -> parseAlign a)
        match alignResults |> List.tryFind Result.isError with
        | Some (Error e) -> Error e
        | _ ->
            let aligns = alignResults |> List.map (fun r -> match r with Ok v -> v | Error _ -> System.Nullable Spectre.Console.Justify.Left)
            let tbl = Spectre.Console.Table ()
            tbl.Border <- Spectre.Console.TableBorder.Rounded
            for ((header, _), justify) in List.zip columnDefs aligns do
                let col = Spectre.Console.TableColumn header
                col.Alignment <- justify
                tbl.AddColumn col |> ignore
            for row in rows do
                Spectre.Console.TableExtensions.AddRow(tbl, row) |> ignore
            Ok (DataDisplaysHelpers.lower tbl)
