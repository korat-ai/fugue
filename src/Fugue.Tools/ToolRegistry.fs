module Fugue.Tools.ToolRegistry

open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

/// Build all six tools as no-reflection AIFunction subclasses.
let buildAll (cwd: string) : AIFunction list = [
    ReadFn.create  cwd
    WriteFn.create cwd
    EditFn.create  cwd
    BashFn.create  cwd
    GlobFn.create  cwd
    GrepFn.create  cwd
]

let names : string list = [ "Read"; "Write"; "Edit"; "Bash"; "Glob"; "Grep" ]

let descriptions : (string * string) list = [
    "Read",  "Read a text file. Returns lines prefixed with 1-based line numbers."
    "Write", "Write content to a file, creating directories as needed."
    "Edit",  "Replace an exact string in a file. Fails if old_string not found or not unique."
    "Bash",  "Run a shell command and return stdout, stderr, and exit code."
    "Glob",  "List files matching a glob pattern."
    "Grep",  "Search files for a regex pattern, returning matching lines with file and line number."
]
