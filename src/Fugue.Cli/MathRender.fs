module Fugue.Cli.MathRender

// LaTeX math → Unicode approximation.
// Best-effort: covers common Greek letters, operators, arrows, sets,
// superscripts, subscripts, and \frac. No Regex (AOT constraint).

/// Replace all recognised \command sequences with Unicode equivalents.
let private replaceCommands (s: string) : string =
    s
     // Greek lowercase
     .Replace(@"\alpha",   "α")
     .Replace(@"\beta",    "β")
     .Replace(@"\gamma",   "γ")
     .Replace(@"\delta",   "δ")
     .Replace(@"\epsilon", "ε")
     .Replace(@"\zeta",    "ζ")
     .Replace(@"\eta",     "η")
     .Replace(@"\theta",   "θ")
     .Replace(@"\iota",    "ι")
     .Replace(@"\kappa",   "κ")
     .Replace(@"\lambda",  "λ")
     .Replace(@"\mu",      "μ")
     .Replace(@"\nu",      "ν")
     .Replace(@"\xi",      "ξ")
     .Replace(@"\pi",      "π")
     .Replace(@"\rho",     "ρ")
     .Replace(@"\sigma",   "σ")
     .Replace(@"\tau",     "τ")
     .Replace(@"\upsilon", "υ")
     .Replace(@"\phi",     "φ")
     .Replace(@"\chi",     "χ")
     .Replace(@"\psi",     "ψ")
     .Replace(@"\omega",   "ω")
     // Greek uppercase
     .Replace(@"\Alpha",   "Α")
     .Replace(@"\Beta",    "Β")
     .Replace(@"\Gamma",   "Γ")
     .Replace(@"\Delta",   "Δ")
     .Replace(@"\Epsilon", "Ε")
     .Replace(@"\Theta",   "Θ")
     .Replace(@"\Lambda",  "Λ")
     .Replace(@"\Xi",      "Ξ")
     .Replace(@"\Pi",      "Π")
     .Replace(@"\Sigma",   "Σ")
     .Replace(@"\Phi",     "Φ")
     .Replace(@"\Psi",     "Ψ")
     .Replace(@"\Omega",   "Ω")
     // Arithmetic / relational operators
     .Replace(@"\times",   "×")
     .Replace(@"\div",     "÷")
     .Replace(@"\pm",      "±")
     .Replace(@"\leq",     "≤")
     .Replace(@"\geq",     "≥")
     .Replace(@"\neq",     "≠")
     .Replace(@"\approx",  "≈")
     .Replace(@"\infty",   "∞")
     .Replace(@"\sum",     "∑")
     .Replace(@"\prod",    "∏")
     .Replace(@"\sqrt",    "√")
     .Replace(@"\int",     "∫")
     .Replace(@"\partial", "∂")
     .Replace(@"\nabla",   "∇")
     .Replace(@"\cdot",    "·")
     // Arrows
     .Replace(@"\Leftrightarrow", "⟺")
     .Replace(@"\Rightarrow",     "⇒")
     .Replace(@"\Leftarrow",      "⇐")
     .Replace(@"\leftarrow",      "←")
     .Replace(@"\rightarrow",     "→")
     .Replace(@"\to",             "→")
     .Replace(@"\uparrow",        "↑")
     .Replace(@"\downarrow",      "↓")
     // Sets / logic
     .Replace(@"\emptyset", "∅")
     .Replace(@"\forall",   "∀")
     .Replace(@"\exists",   "∃")
     .Replace(@"\notin",    "∉")
     .Replace(@"\in",       "∈")
     .Replace(@"\subset",   "⊂")
     .Replace(@"\supset",   "⊃")
     .Replace(@"\subseteq", "⊆")
     .Replace(@"\supseteq", "⊇")
     .Replace(@"\cup",      "∪")
     .Replace(@"\cap",      "∩")
     .Replace(@"\wedge",    "∧")
     .Replace(@"\vee",      "∨")
     .Replace(@"\neg",      "¬")
     // Miscellaneous
     .Replace(@"\ldots",    "…")
     .Replace(@"\cdots",    "⋯")
     .Replace(@"\equiv",    "≡")
     .Replace(@"\propto",   "∝")

/// Replace \frac{a}{b} with (a/b).
/// Uses a simple forward scan — no Regex.
let private replaceFrac (s: string) : string =
    let tag = @"\frac{"
    let rec loop (acc: System.Text.StringBuilder) (i: int) =
        if i >= s.Length then acc.ToString()
        else
            let pos = s.IndexOf(tag, i, System.StringComparison.Ordinal)
            if pos < 0 then
                acc.Append(s, i, s.Length - i) |> ignore
                acc.ToString()
            else
                // Copy text before \frac
                acc.Append(s, i, pos - i) |> ignore
                // Find closing } for numerator
                let numStart = pos + tag.Length
                let mutable depth = 1
                let mutable j = numStart
                while j < s.Length && depth > 0 do
                    if s.[j] = '{' then depth <- depth + 1
                    elif s.[j] = '}' then depth <- depth - 1
                    j <- j + 1
                let numEnd = j - 1  // index of the closing }
                let numerator = s.[numStart .. numEnd - 1]
                // Expect next char to be {
                if j < s.Length && s.[j] = '{' then
                    let denStart = j + 1
                    let mutable depth2 = 1
                    let mutable k = denStart
                    while k < s.Length && depth2 > 0 do
                        if s.[k] = '{' then depth2 <- depth2 + 1
                        elif s.[k] = '}' then depth2 <- depth2 - 1
                        k <- k + 1
                    let denEnd = k - 1
                    let denominator = s.[denStart .. denEnd - 1]
                    acc.Append(sprintf "(%s/%s)" numerator denominator) |> ignore
                    loop acc k
                else
                    // Malformed — emit as-is starting from numStart
                    acc.Append(sprintf "(%s/...)" numerator) |> ignore
                    loop acc j
    loop (System.Text.StringBuilder()) 0

/// Superscript digit/letter map.
let private superMap =
    dict [
        '0', '⁰'; '1', '¹'; '2', '²'; '3', '³'; '4', '⁴'
        '5', '⁵'; '6', '⁶'; '7', '⁷'; '8', '⁸'; '9', '⁹'
        'n', 'ⁿ'; 'i', 'ⁱ'; 'a', 'ᵃ'; 'b', 'ᵇ'; 'c', 'ᶜ'
        'k', 'ᵏ'; 'm', 'ᵐ'; 'r', 'ʳ'; 's', 'ˢ'; 't', 'ᵗ'
        'x', 'ˣ'; 'y', 'ʸ'; 'z', 'ᶻ'
        '+', '⁺'; '-', '⁻'; '=', '⁼'; '(', '⁽'; ')', '⁾'
    ]

/// Subscript digit map (letters mostly lack Unicode subscript forms).
let private subMap =
    dict [
        '0', '₀'; '1', '₁'; '2', '₂'; '3', '₃'; '4', '₄'
        '5', '₅'; '6', '₆'; '7', '₇'; '8', '₈'; '9', '₉'
        'n', 'ₙ'; 'i', 'ᵢ'; 'a', 'ₐ'; 'e', 'ₑ'; 'o', 'ₒ'
        'k', 'ₖ'; 'm', 'ₘ'; 'r', 'ᵣ'; 's', 'ₛ'; 't', 'ₜ'
        'x', 'ₓ'
        '+', '₊'; '-', '₋'; '=', '₌'; '(', '₍'; ')', '₎'
    ]

/// Replace ^X and _X sequences (single char or {group}) with super/subscript chars.
/// Only processes single-character targets outside braces; {group} is left as (group)
/// with a super/sub indicator when no per-char map exists.
let private replaceScripts (s: string) : string =
    let sb = System.Text.StringBuilder(s.Length)
    let mutable i = 0
    while i < s.Length do
        let c = s.[i]
        if (c = '^' || c = '_') && i + 1 < s.Length then
            let isSup = c = '^'
            let map = if isSup then superMap else subMap
            let next = s.[i + 1]
            if next = '{' then
                // Collect everything up to matching }
                let mutable depth = 1
                let mutable j = i + 2
                while j < s.Length && depth > 0 do
                    if s.[j] = '{' then depth <- depth + 1
                    elif s.[j] = '}' then depth <- depth - 1
                    j <- j + 1
                let inner = s.[i + 2 .. j - 2]
                // Try to map every char; if all map, emit directly; else wrap
                let allMap = inner |> Seq.forall (fun ch -> map.ContainsKey(ch))
                if allMap then
                    for ch in inner do sb.Append(map.[ch]) |> ignore
                else
                    sb.Append(inner) |> ignore
                i <- j
            else
                let mutable found = false
                let mutable m = Unchecked.defaultof<char>
                if map.TryGetValue(next, &m) then
                    sb.Append(m) |> ignore
                    found <- true
                if not found then
                    sb.Append(c) |> ignore
                    sb.Append(next) |> ignore
                i <- i + 2
        else
            sb.Append(c) |> ignore
            i <- i + 1
    sb.ToString()

/// Strip $$ and $ delimiters, leaving the content (already converted).
/// Processes $$...$$ first, then $...$.
let private stripDelimiters (s: string) : string =
    // Strip $$...$$
    let mutable result = System.Text.StringBuilder()
    let mutable i = 0
    while i < s.Length do
        if i + 1 < s.Length && s.[i] = '$' && s.[i + 1] = '$' then
            // Find closing $$
            let start = i + 2
            let close = s.IndexOf("$$", start, System.StringComparison.Ordinal)
            if close >= 0 then
                result.Append(' ') |> ignore
                result.Append(s, start, close - start) |> ignore
                result.Append(' ') |> ignore
                i <- close + 2
            else
                // No closing $$ — emit as-is
                result.Append(s.[i]) |> ignore
                i <- i + 1
        else
            result.Append(s.[i]) |> ignore
            i <- i + 1
    let s2 = result.ToString()
    // Strip $...$
    let result2 = System.Text.StringBuilder()
    let mutable j = 0
    while j < s2.Length do
        if s2.[j] = '$' then
            let start = j + 1
            let close = s2.IndexOf('$', start)
            if close >= 0 then
                result2.Append(s2, start, close - start) |> ignore
                j <- close + 1
            else
                result2.Append(s2.[j]) |> ignore
                j <- j + 1
        else
            result2.Append(s2.[j]) |> ignore
            j <- j + 1
    result2.ToString()

/// Pre-process a string, converting common LaTeX math to Unicode approximations.
/// Call this before passing text to Markdig so the rendered output contains
/// readable Unicode rather than raw LaTeX commands.
let preprocess (s: string) : string =
    s
    |> replaceFrac
    |> replaceCommands
    |> replaceScripts
    |> stripDelimiters
