namespace Fugue.Surface

/// In-memory terminal surface for unit-testing DrawOp lists without a real TTY.
///
/// Layout (0-indexed rows, height = term.Height):
///   ThinkingLine   → row (height - 3)
///   StatusBarLine1 → row (height - 2)
///   StatusBarLine2 → row (height - 1)
///   InputArea r    → row (height - 3 - 1 - r)  — grows upward from ThinkingLine
///   ScrollContent  → term.CursorRow (advances on Append)
///   Modal          → rows 0..(height-1) (full screen)
///
/// Out-of-bounds regions are silently clamped to the nearest valid row.
/// This choice avoids crashing tests that probe edge-case geometries while
/// still recording the op in WriteLog for assertion.
type MockTerminal = {
    mutable Lines:     string[]
    mutable CursorRow: int
    mutable CursorCol: int
    mutable Width:     int
    mutable Height:    int
    mutable WriteLog:  DrawOp list
}

module MockExecutor =

    let private spaces n = System.String(' ', max 0 n)

    let create (width: int) (height: int) : MockTerminal =
        {
            Lines     = Array.create height (spaces width)
            CursorRow = 0
            CursorCol = 0
            Width     = width
            Height    = height
            WriteLog  = []
        }

    /// Resolve a Region to a 0-indexed row in the terminal grid.
    /// Returns None for Append (handled separately) and Modal.
    let private rowOf (region: Region) (height: int) : int option =
        match region with
        | Region.ThinkingLine   -> Some (max 0 (height - 3))
        | Region.StatusBarLine1 -> Some (max 0 (height - 2))
        | Region.StatusBarLine2 -> Some (max 0 (height - 1))
        | Region.InputArea r    ->
            // Input rows grow upward from just above ThinkingLine.
            // row 0 = height-4, row 1 = height-5, ...
            let baseRow = height - 4 - r
            Some (max 0 (min (height - 1) baseRow))
        | Region.ScrollContent  -> None  // uses CursorRow
        | Region.Modal          -> Some 0

    /// Write text into a specific row at cursor col, clipping to width.
    let private writeIntoRow (row: int) (col: int) (text: string) (term: MockTerminal) : unit =
        if row < 0 || row >= term.Height then ()
        else
            let line   = term.Lines.[row]
            let padded = if line.Length < term.Width then line.PadRight term.Width else line
            let startCol = max 0 (min col (term.Width - 1))
            let available = term.Width - startCol
            let snippet  = if text.Length > available then text.[..available - 1] else text
            let builder  = System.Text.StringBuilder(padded)
            for i in 0 .. snippet.Length - 1 do
                if startCol + i < term.Width then
                    builder.[startCol + i] <- snippet.[i]
            term.Lines.[row] <- builder.ToString()

    let rec private applyOp (op: DrawOp) (term: MockTerminal) : unit =
        term.WriteLog <- term.WriteLog @ [op]
        match op with
        | DrawOp.Paint(region, text, _style) ->
            match rowOf region term.Height with
            | Some row ->
                // Named regions: write at col 0 of the target row.
                // CursorRow (the scroll cursor) is NOT modified — only the named
                // region's row is painted.  Use MoveTo to position within a region.
                writeIntoRow row 0 text term
            | None ->
                // ScrollContent — write at current scroll cursor row and col.
                writeIntoRow term.CursorRow term.CursorCol text term
                term.CursorCol <- min term.Width (term.CursorCol + text.Length)

        | DrawOp.EraseLine region ->
            let row =
                match rowOf region term.Height with
                | Some r -> r
                | None   -> term.CursorRow
            if row >= 0 && row < term.Height then
                term.Lines.[row] <- spaces term.Width
            term.CursorCol <- 0

        | DrawOp.MoveTo(region, col) ->
            let row =
                match rowOf region term.Height with
                | Some r -> r
                | None   -> term.CursorRow
            term.CursorRow <- max 0 (min (term.Height - 1) row)
            term.CursorCol <- max 0 (min (term.Width  - 1) col)

        | DrawOp.SaveAndRestore inner ->
            let savedRow = term.CursorRow
            let savedCol = term.CursorCol
            for innerOp in inner do
                applyOp innerOp term
            term.CursorRow <- savedRow
            term.CursorCol <- savedCol

        | DrawOp.Append text ->
            writeIntoRow term.CursorRow term.CursorCol text term
            term.CursorRow <- min (term.Height - 1) (term.CursorRow + 1)
            term.CursorCol <- 0

        | DrawOp.ResetScrollRegion ->
            // No-op for mock — no real scroll region to reset.
            ()

        | DrawOp.RawAnsi _ ->
            // Mock cannot interpret arbitrary ANSI; recording in WriteLog (above)
            // is enough for tests that only assert "the actor processed it".
            ()

    /// Apply a list of DrawOps to the mock terminal.
    /// Mutates term.Lines, term.CursorRow/Col, term.WriteLog in place.
    /// Returns the same terminal for convenient chaining.
    let execute (ops: DrawOp list) (term: MockTerminal) : MockTerminal =
        for op in ops do
            applyOp op term
        term

    /// Return the raw line at 0-indexed row (empty string if out of range).
    let lineAt (row: int) (term: MockTerminal) : string =
        if row < 0 || row >= term.Height then ""
        else term.Lines.[row].TrimEnd()

    /// Return the content of the line(s) occupied by a logical Region.
    /// For Modal, returns the first row (row 0).
    /// For ScrollContent, returns the line at CursorRow.
    let regionContent (region: Region) (term: MockTerminal) : string =
        let row =
            match rowOf region term.Height with
            | Some r -> r
            | None   -> term.CursorRow
        lineAt row term
