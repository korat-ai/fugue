namespace Fugue.Core

/// Approval policy applied to tool calls during a session.
///
/// Modes form a cycle (Shift+Tab in the REPL): Plan → Default → AutoEdit → YOLO → Plan.
///
/// Gating semantics (will be wired into the tool dispatcher in a follow-up PR):
///
///   | mode      | Read/Glob/Grep | Write/Edit | Bash         |
///   |-----------|----------------|------------|--------------|
///   | Plan      | prompt         | prompt     | prompt       |
///   | Default   | auto           | prompt     | prompt       |
///   | AutoEdit  | auto           | auto       | prompt       |
///   | YOLO      | auto           | auto       | auto         |
///
/// At present the mode is purely advisory: it cycles via Shift+Tab and is
/// shown in the status bar, but tool execution is not yet gated. Phase 1 of
/// v0.2 ships the cycling + display; Phase 2 wires the gate into Bash/Write/Edit.
[<RequireQualifiedAccess>]
type ApprovalMode =
    | Plan
    | Default
    | AutoEdit
    | YOLO

module ApprovalMode =

    /// Cycle order matches the user-facing Shift+Tab progression.
    let cycle (mode: ApprovalMode) : ApprovalMode =
        match mode with
        | ApprovalMode.Plan     -> ApprovalMode.Default
        | ApprovalMode.Default  -> ApprovalMode.AutoEdit
        | ApprovalMode.AutoEdit -> ApprovalMode.YOLO
        | ApprovalMode.YOLO     -> ApprovalMode.Plan

    /// Single-glyph indicator + lowercase label, suitable for a status-bar pill.
    /// Glyph hierarchy: ◆ (filled diamond, most restrictive) → ○ (empty circle, least).
    let icon (mode: ApprovalMode) : string =
        match mode with
        | ApprovalMode.Plan     -> "◆ plan"      // ◆
        | ApprovalMode.Default  -> "● default"   // ●
        | ApprovalMode.AutoEdit -> "◑ auto-edit" // ◑
        | ApprovalMode.YOLO     -> "○ yolo"      // ○

    /// Short label without the glyph, for log lines and CLI flag round-tripping.
    let label (mode: ApprovalMode) : string =
        match mode with
        | ApprovalMode.Plan     -> "plan"
        | ApprovalMode.Default  -> "default"
        | ApprovalMode.AutoEdit -> "auto-edit"
        | ApprovalMode.YOLO     -> "yolo"

    /// Parse a CLI-flag value (case-insensitive). `auto-edit` and `autoedit` are
    /// both accepted because users will type both. Returns None on unknown input
    /// so the caller can produce a structured error message.
    let tryParse (raw: string | null) : ApprovalMode option =
        let normalised =
            match raw with
            | null -> ""
            | s    -> s.Trim().ToLowerInvariant()
        match normalised with
        | "plan"      -> Some ApprovalMode.Plan
        | "default"   -> Some ApprovalMode.Default
        | "auto-edit"
        | "autoedit"  -> Some ApprovalMode.AutoEdit
        | "yolo"      -> Some ApprovalMode.YOLO
        | _           -> None

    /// Whether tool calls of a given category require user approval under this
    /// mode. The dispatcher will use this once gating ships.
    let requiresApproval (mode: ApprovalMode) (toolKind: string) : bool =
        // Tool kinds: "read" (Read/Glob/Grep), "edit" (Write/Edit), "bash" (Bash).
        // Unknown kinds default to "edit" — safer to prompt than to skip.
        match mode, toolKind with
        | ApprovalMode.YOLO, _              -> false
        | ApprovalMode.AutoEdit, "read"     -> false
        | ApprovalMode.AutoEdit, "edit"     -> false
        | ApprovalMode.AutoEdit, _          -> true
        | ApprovalMode.Default, "read"      -> false
        | ApprovalMode.Default, _           -> true
        | ApprovalMode.Plan, _              -> true
