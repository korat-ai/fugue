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
