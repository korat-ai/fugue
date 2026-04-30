module Fugue.Tools.InjectionGuard

open System.Text.RegularExpressions

type Severity = Warn | Block

type DetectedPattern =
    { Pattern  : string
      Severity : Severity
      Detail   : string }

// Patterns ordered: most dangerous first.
// All are static compiled for AOT safety.
let private bashPatterns : (Regex * Severity * string) list =
    [ Regex(@";\s*rm\s+-[a-zA-Z]*f",     RegexOptions.IgnoreCase), Block, "destructive rm after semicolon"
      Regex(@"\$\([^)]*curl[^)]*\)",      RegexOptions.IgnoreCase), Block, "command substitution with curl"
      Regex(@"\|\s*bash\b",               RegexOptions.IgnoreCase), Block, "pipe to bash"
      Regex(@"\|\s*sh\b",                 RegexOptions.IgnoreCase), Block, "pipe to sh"
      Regex(@">\s*/etc/",                 RegexOptions.IgnoreCase), Block, "write to /etc/"
      Regex(@"base64\s+--decode\s*\|",    RegexOptions.IgnoreCase), Block, "base64 decode pipe"
      Regex(@"&&\s*curl\b",               RegexOptions.IgnoreCase), Warn,  "curl chained with &&"
      Regex(@">\s*/dev/[a-z]",            RegexOptions.IgnoreCase), Warn,  "write to /dev/ device"
      Regex(@"\bwget\s+.*\|\s*",          RegexOptions.IgnoreCase), Warn,  "wget pipe" ]

let checkBash (command: string) : DetectedPattern list =
    bashPatterns
    |> List.choose (fun (rx, severity, detail) ->
        if rx.IsMatch command then
            Some { Pattern = rx.ToString(); Severity = severity; Detail = detail }
        else None)
