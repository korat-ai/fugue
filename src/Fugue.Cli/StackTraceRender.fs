module Fugue.Cli.StackTraceRender

open System.Text.RegularExpressions
open Spectre.Console
open Spectre.Console.Rendering

// AOT-safe: Regex.Compiled is supported on .NET 10 Native AOT.
let private atFrameRx  = Regex(@"^\s+at\s+", RegexOptions.Compiled)
let private sourceRx   = Regex(@"\s+in\s+(.+):line\s+(\d+)\s*$", RegexOptions.Compiled)

/// Namespaces that indicate framework/noise frames.
let private frameworkPrefixes = [|
    "System."; "Microsoft."; "FSharp."; "Newtonsoft."; "Anthropic."
    "OllamaSharp."; "Google."; "Azure."; "Serilog."; "NLog."
    "xunit."; "Xunit."; "NUnit."; "MassTransit."; "Dapper."
|]

let private isFrameworkFrame (line: string) =
    let t = line.TrimStart()
    if not (t.StartsWith "at ") then false
    else frameworkPrefixes |> Array.exists (fun p -> t.Contains("at " + p) || t.Contains("at " + p.TrimEnd('.')))

let private isAsyncFrame (line: string) =
    let t = line.TrimStart()
    t.StartsWith("at ") && (t.Contains("MoveNext") || t.Contains(".Task<") || t.Contains("$cont"))

/// True if the text block looks like it contains a .NET stack trace.
let looksLikeTrace (text: string) : bool =
    text.Split('\n')
    |> Array.exists (fun l -> atFrameRx.IsMatch(l))

let private escapeMarkup (s: string) = Markup.Escape(s)

/// Render a stack trace block with highlighted user frames and dimmed framework noise.
/// Returns None if the text doesn't look like a stack trace.
let tryRender (text: string) : IRenderable option =
    if not (looksLikeTrace text) then None
    else
        let renderables = ResizeArray<IRenderable>()
        for line in text.Split('\n') do
            let markup =
                if atFrameRx.IsMatch(line) then
                    let m = sourceRx.Match(line)
                    let linkedLine =
                        if m.Success then
                            let file    = m.Groups.[1].Value
                            let lineNum = m.Groups.[2].Value
                            let uri     = sprintf "file://%s" file
                            let before  = line.[..line.Length - m.Value.Length - 1]
                            sprintf "%s [link=%s]in %s:line %s[/]" (escapeMarkup before) uri (escapeMarkup file) lineNum
                        else escapeMarkup line
                    if   isFrameworkFrame line then sprintf "[grey]%s[/]" linkedLine
                    elif isAsyncFrame     line then sprintf "[dim]%s[/]" linkedLine
                    else sprintf "[bold yellow]%s[/]" linkedLine
                elif line.TrimStart().StartsWith("---") then
                    sprintf "[grey]%s[/]" (escapeMarkup line)
                else
                    escapeMarkup line
            renderables.Add(Markup(markup) :> IRenderable)
        Some (Padder(Rows(renderables)).PadLeft(2) :> IRenderable)
