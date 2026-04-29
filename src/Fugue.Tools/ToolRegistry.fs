module Fugue.Tools.ToolRegistry

open System
open Microsoft.Extensions.AI

let buildAll (cwd: string) : AIFunction list =
    [
      AIFunctionFactory.Create(
          Func<string, int option, int option, string>(fun path offset limit ->
              Fugue.Tools.ReadTool.read cwd path offset limit),
          name = "Read",
          description = "Read a text file. Returns lines prefixed with 1-based line numbers.")

      AIFunctionFactory.Create(
          Func<string, string, string>(fun path content ->
              Fugue.Tools.WriteTool.write cwd path content),
          name = "Write",
          description = "Write text content to a file. Overwrites if exists.")

      AIFunctionFactory.Create(
          Func<string, string, string, bool option, string>(fun path old_string new_string replace_all ->
              Fugue.Tools.EditTool.edit cwd path old_string new_string replace_all),
          name = "Edit",
          description = "Edit a text file by exact-string replacement.")

      AIFunctionFactory.Create(
          Func<string, int option, string>(fun command timeout_ms ->
              Fugue.Tools.BashTool.bash cwd command timeout_ms),
          name = "Bash",
          description = "Run a shell command via /bin/zsh -lc.")

      AIFunctionFactory.Create(
          Func<string, string option, string>(fun pattern root ->
              Fugue.Tools.GlobTool.glob cwd pattern root),
          name = "Glob",
          description = "Find files matching a glob pattern, sorted by mtime descending.")

      AIFunctionFactory.Create(
          Func<string, string option, string option, string>(fun pattern root glob ->
              Fugue.Tools.GrepTool.grep cwd pattern root glob),
          name = "Grep",
          description = "Regex-search files. Returns path:line:text matches.")
    ]

let names : string list = [ "Read"; "Write"; "Edit"; "Bash"; "Glob"; "Grep" ]
