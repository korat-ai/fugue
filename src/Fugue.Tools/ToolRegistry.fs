module Fugue.Tools.ToolRegistry

open System.Diagnostics.CodeAnalysis
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

/// Build all tools as no-reflection AIFunction subclasses.
/// The GetConversation tool reads the session via the supplied closure, which the
/// caller (Program.fs/Repl.fs) implements as a ref-deref. No module-level state.
[<RequiresUnreferencedCode("GetConversationFn uses AgentSessionExtensions over STJ state")>]
[<RequiresDynamicCode("GetConversationFn uses AgentSessionExtensions over STJ state")>]
let buildAll (cwd: string) (hooksConfig: Fugue.Core.Hooks.HooksConfig) (sessionId: string) (getSession: unit -> AgentSession | null) : AIFunction list = [
    ReadFn.create       cwd hooksConfig sessionId
    WriteFn.create      cwd hooksConfig sessionId
    WriteBatchFn.create cwd hooksConfig sessionId
    EditFn.create       cwd hooksConfig sessionId
    BashFn.create       cwd hooksConfig sessionId
    GlobFn.create       cwd hooksConfig sessionId
    GrepFn.create       cwd hooksConfig sessionId
    TreeFn.create       cwd hooksConfig sessionId
    GetConversationFn.create getSession hooksConfig sessionId
]

let names : string list = [ "Read"; "Write"; "WriteBatch"; "Edit"; "Bash"; "Glob"; "Grep"; "Tree"; "GetConversation" ]

let descriptions : (string * string) list = [
    "Read",       "Read a text file. Returns lines prefixed with 1-based line numbers."
    "Write",      "Write content to a file, creating directories as needed."
    "WriteBatch", "Atomically write multiple files in one operation (all-or-nothing)."
    "Edit",       "Replace an exact string in a file. Fails if old_string not found or not unique."
    "Bash",       "Run a shell command and return stdout, stderr, and exit code."
    "Glob",       "List files matching a glob pattern."
    "Grep",       "Search files for a regex pattern, returning matching lines with file and line number."
    "Tree",       "List directory contents as an ASCII tree."
    "GetConversation", "Return the current session's conversation history as JSON (turn count, status, messages with roles and tool-call markers)."
]
