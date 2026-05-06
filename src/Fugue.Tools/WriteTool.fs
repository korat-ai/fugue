module Fugue.Tools.WriteTool

open System
open System.IO
open System.Text
open System.ComponentModel
open Fugue.Tools.PathSafety

[<Description("Write text content to a file. Creates parent directories. Overwrites existing files.")>]
let write
    ([<Description("Working directory.")>] cwd: string)
    ([<Description("Destination file path.")>] path: string)
    ([<Description("Content to write (UTF-8, no BOM).")>] content: string)
    : string =
    let full = resolve cwd path
    if not (isUnder cwd full) then
        raise (UnauthorizedAccessException(sprintf "path outside working directory: %s" path))
    let dir = Path.GetDirectoryName full |> Option.ofObj |> Option.defaultValue "."
    if dir <> "." || not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore
    Fugue.Core.Checkpoint.snapshot full
    File.WriteAllText(full, content, UTF8Encoding(false))
    let bytes = Encoding.UTF8.GetByteCount content
    "wrote " + string bytes + " bytes to " + full
