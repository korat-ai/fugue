module Fugue.Core.EmojiMap

// Curated list of common AI-response emoji → ASCII fallbacks.
// No Dictionary (to avoid generic reflection in AOT) — plain array of tuples.
let private pairs : (string * string) array = [|
    "✅", "[ok]"
    "❌", "[x]"
    "⚠️", "[!]"
    "🔧", "[cfg]"
    "📁", "[dir]"
    "📄", "[file]"
    "🚀", "[go]"
    "💡", "[tip]"
    "🐛", "[bug]"
    "🔍", "[find]"
    "📝", "[note]"
    "⚡", "[fast]"
    "🎵", "[note]"
    "🎶", "[notes]"
    "🔒", "[lock]"
    "⭐", "[star]"
    "🏗️", "[build]"
    "📊", "[chart]"
    "🧪", "[test]"
    "🔑", "[key]"
    "🎯", "[aim]"
    "📌", "[pin]"
    "🔗", "[link]"
    "💻", "[code]"
    "📦", "[pkg]"
    "🔄", "[sync]"
    "⏳", "[wait]"
    "✨", "[new]"
    "🗑️", "[del]"
    "📋", "[list]"
    "💾", "[save]"
    "🌐", "[web]"
    "🛠️", "[tool]"
    "🔓", "[unlock]"
    "🎉", "[done]"
    "👀", "[see]"
    "💬", "[chat]"
    "🧩", "[piece]"
|]

/// Detect whether the current terminal likely supports emoji.
/// Heuristic: TERM_PROGRAM contains iTerm|WezTerm|vscode, or COLORTERM=truecolor, or LANG contains UTF-8.
let terminalSupportsEmoji () : bool =
    let termProg = System.Environment.GetEnvironmentVariable "TERM_PROGRAM" |> Option.ofObj |> Option.defaultValue ""
    let colorTerm = System.Environment.GetEnvironmentVariable "COLORTERM" |> Option.ofObj |> Option.defaultValue ""
    let lang = System.Environment.GetEnvironmentVariable "LANG" |> Option.ofObj |> Option.defaultValue ""
    let lcAll = System.Environment.GetEnvironmentVariable "LC_ALL" |> Option.ofObj |> Option.defaultValue ""
    termProg.Contains "iTerm" || termProg.Contains "WezTerm" || termProg.Contains "vscode"
    || colorTerm = "truecolor"
    || lang.Contains "UTF-8"
    || lcAll.Contains "UTF-8"

/// Replace emoji in text with ASCII fallbacks.
let normalise (text: string) : string =
    pairs |> Array.fold (fun (s: string) (emoji, ascii) -> s.Replace(emoji, ascii)) text
