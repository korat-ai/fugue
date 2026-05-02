module Fugue.Cli.ApprovalPrompt

open System
open Fugue.Core

/// Map an AIFunction tool name to an approval-mode kind:
///   - "read"  → Read / Glob / Grep / Tree / GetConversation
///   - "edit"  → Write / WriteBatch / Edit
///   - "bash"  → Bash
///   - "edit"  → unknown (safer to prompt)
let toolKind (toolName: string) : string =
    match toolName with
    | "Read" | "Glob" | "Grep" | "Tree" | "GetConversation" -> "read"
    | "Write" | "WriteBatch" | "Edit"                       -> "edit"
    | "Bash"                                                -> "bash"
    | _                                                     -> "edit"

/// Truncate a long argument summary so the prompt stays on one line.
let private trim (s: string) (max: int) : string =
    if s.Length <= max then s
    else s.Substring(0, max - 1) + "…"

/// Headless / non-TTY default: in YOLO allow everything; otherwise deny
/// (no UI to prompt, safer to fail closed than to silently auto-execute).
let private headlessGate (mode: ApprovalMode) (toolName: string) : Async<bool> =
    async {
        if not (ApprovalMode.requiresApproval mode (toolKind toolName)) then
            return true
        else
            // Non-interactive context — fail closed. Agent will see "[denied]".
            return false
    }

/// Interactive prompt — reads a single key (y / n / Enter), echoes the choice,
/// returns true on y/Y. Anything else = deny.
/// Pre-condition: caller is on the REPL thread (Console is not in use elsewhere).
/// The Surface actor must NOT be writing to Console concurrently — at the moment
/// the gate fires, the agent is between tool calls so the actor queue is idle.
let private interactivePrompt (toolName: string) (argsJson: string) : Async<bool> =
    async {
        let summary = trim argsJson 80
        Console.Out.WriteLine ()
        Console.Out.WriteLine (sprintf "\x1b[33m? approve %s(%s) [y/N]\x1b[0m" toolName summary)
        Console.Out.Flush ()
        let key = Console.ReadKey(intercept = true)
        let approved = key.KeyChar = 'y' || key.KeyChar = 'Y'
        let label = if approved then "\x1b[32mallow\x1b[0m" else "\x1b[31mdeny\x1b[0m"
        Console.Out.WriteLine (sprintf "  → %s" label)
        Console.Out.Flush ()
        return approved
    }

/// Build the gate to install via `DelegatedFn.setApprovalGate`.
/// `getMode` is a closure that returns the current ApprovalMode — passing it
/// as a thunk (rather than a captured value) lets Shift+Tab cycle take effect
/// for the very next tool call.
let buildGate (getMode: unit -> ApprovalMode) (interactive: bool) : string -> string -> Async<bool> =
    fun toolName argsJson ->
        async {
            let mode = getMode ()
            let kind = toolKind toolName
            if not (ApprovalMode.requiresApproval mode kind) then
                return true
            elif interactive then
                return! interactivePrompt toolName argsJson
            else
                return! headlessGate mode toolName
        }
