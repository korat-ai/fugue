module Fugue.Tools.ToolRegistry

open System
open Microsoft.Extensions.AI

// AIFunctionFactory reflects on parameter types and looks them up in
// JsonSerializerOptions.Default — which has no metadata for FSharpOption<_>.
// So we expose Nullable<T> / nullable string at the delegate boundary
// and convert to F# option just before calling the tool implementations.
let private ofNullable (n: Nullable<'a>) : 'a option =
    if n.HasValue then Some n.Value else None

let private ofNullStr (s: string | null) : string option =
    Option.ofObj s

let buildAll (cwd: string) : AIFunction list =
    [
      AIFunctionFactory.Create(
          Func<string, Nullable<int>, Nullable<int>, string>(fun path offset limit ->
              Fugue.Tools.ReadTool.read cwd path (ofNullable offset) (ofNullable limit)),
          name = "Read",
          description = "Read a text file. Returns lines prefixed with 1-based line numbers.")

      AIFunctionFactory.Create(
          Func<string, string, string>(fun path content ->
              Fugue.Tools.WriteTool.write cwd path content),
          name = "Write",
          description = "Write text content to a file. Overwrites if exists.")

      AIFunctionFactory.Create(
          Func<string, string, string, Nullable<bool>, string>(fun path old_string new_string replace_all ->
              Fugue.Tools.EditTool.edit cwd path old_string new_string (ofNullable replace_all)),
          name = "Edit",
          description = "Edit a text file by exact-string replacement.")

      AIFunctionFactory.Create(
          Func<string, Nullable<int>, string>(fun command timeout_ms ->
              Fugue.Tools.BashTool.bash cwd command (ofNullable timeout_ms)),
          name = "Bash",
          description = "Run a shell command via /bin/zsh -lc.")

      AIFunctionFactory.Create(
          Func<string, string | null, string>(fun pattern root ->
              Fugue.Tools.GlobTool.glob cwd pattern (ofNullStr root)),
          name = "Glob",
          description = "Find files matching a glob pattern, sorted by mtime descending.")

      AIFunctionFactory.Create(
          Func<string, string | null, string | null, string>(fun pattern root glob ->
              Fugue.Tools.GrepTool.grep cwd pattern (ofNullStr root) (ofNullStr glob)),
          name = "Grep",
          description = "Regex-search files. Returns path:line:text matches.")
    ]

let names : string list = [ "Read"; "Write"; "Edit"; "Bash"; "Glob"; "Grep" ]
