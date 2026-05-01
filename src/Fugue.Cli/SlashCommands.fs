module Fugue.Cli.SlashCommands

open Fugue.Core.Localization

/// Single source of truth for all slash commands. Used by:
///   - ReadLine.fs for inline autocomplete suggestions (prefix-match on name)
///   - Repl.fs for /help command rendering
///
/// Order is preserved: matches the historical /help output ordering, with
/// commands that previously lived only in ReadLine's suggestion list appended
/// at the end (variants like /diff --staged, /model set, /model suggest, /quit, /doctor).
let getAll (strings: Strings) : (string * string) list =
    [ // Core
      "/help",                       strings.CmdHelpDesc
      "/menu",                       strings.CmdMenuDesc
      "/ask <q>",                    strings.CmdAskDesc
      "/clear",                      strings.CmdClearDesc
      "/summary",                    strings.CmdSummaryDesc
      "/document [path]",            strings.CmdDocumentDesc
      "/activity",                   strings.CmdActivityDesc
      "/alias <n>=<exp>",            strings.CmdAliasDesc
      "/scratch <text>",             strings.CmdScratchDesc
      "/squash <N>",                 strings.CmdSquashDesc
      "/short",                      strings.CmdShortDesc
      "/long",                       strings.CmdLongDesc
      "/zen",                        strings.CmdZenDesc
      "/snippet …",                  strings.CmdSnippetDesc
      "/bookmark <name>",            strings.CmdBookmarkDesc
      "/bookmarks",                  strings.CmdBookmarksDesc
      "/clear-history",              strings.CmdClearHistoryDesc
      "/tools",                      strings.CmdToolsDesc
      "/todo",                       strings.CmdTodoDesc
      "/note <text>",                strings.CmdNoteDesc
      "/notes",                      strings.CmdNotesDesc
      "/undo",                       strings.CmdUndoDesc
      "/find <query>",               strings.CmdFindDesc
      "/summarize <p>",              strings.CmdSummarizeDesc
      "/session offload",            "save session to JSONL, clear in-memory history"
      "/session resume <id>",        "reload a previously offloaded session by ULID"
      "/new",                        strings.CmdNewDesc
      "/diff",                       strings.CmdDiffDesc
      "/init",                       strings.CmdInitDesc
      "/git-log",                    strings.CmdGitLogDesc
      "/issue <N>",                  strings.CmdIssueDesc
      "/model",                      "interactive model picker (or: /model set <name>, /model suggest)"
      "/onboard",                    strings.CmdOnboardDesc
      "/watch <p> <cmd>",            strings.CmdWatchDesc
      "/macro …",                    strings.CmdMacroDesc
      "/bench [n]",                  strings.CmdBenchDesc
      "/compat",                     strings.CmdCompatDesc
      "/history [n]",                strings.CmdHistoryDesc
      "/templates",                  "list available session templates"
      "/gen uuid|ulid|nanoid",       "generate a unique identifier"
      "/rename <title>",             "set a human-readable title for this session"
      "/env set|unset|list",         "manage session-scoped environment variables"
      "/cron \"description\"",       "convert NL schedule to cron + next 5 fire times"
      "/regex \"pat\" \"in\"",       "test a regex pattern against sample input"
      "/rate up|down [n]",           strings.CmdRateDesc
      "/annotate [n] [up|down] <note>", strings.CmdAnnotateDesc
      "/theme …",                    strings.CmdThemeDesc
      "/exit",                       strings.CmdExitDesc
      "/review pr <N>",              strings.CmdReviewPrDesc
      "/report-bug",                 "capture last failed turn as a GitHub issue draft"
      "/http METHOD URL …",          "inline HTTP client (HTTPie-style, -H/-d/--inject)"
      "/refactor pipeline [f:N-M]",  "rewrite F# let-bindings as |> pipe chain"
      "/check exhaustive [dir]",     "find FS0025 incomplete-pattern warnings and fix"
      "/compress",                   "summarise session history and reset context window"
      "/rop <description>",          "generate ROP Result/Either chain from plain-English"
      "/infer-type <expr>",          "infer and explain F# type signature of an expression"
      "/pointfree <fn>",             "offer point-free rewrite of an F# function"
      "/scaffold du <name>",         "generate F# DU with match examples and JSON attrs"
      "/monad <desc>",               "generate F# CE / Scala for-comprehension from pseudocode"
      "/migrate oop-to-fp [file]",   "convert OOP class hierarchy to F# DUs"
      "/derive codec <Type>",        "generate AOT-safe STJ JsonSerializerContext"
      "/scaffold cqrs <Cmd>",        "generate CQRS Command/Handler/Event/Test slice"
      "/scaffold actor <Name>",      "generate typed actor with command DU and supervision"
      "/check tail-rec <file>",      "detect non-tail-recursive functions and offer rewrites"
      "/check effects <file>",       "verify ZIO/Cats Effect/Async type consistency"
      "/translate comments <file>",  "translate code comments to English"
      "/port <file> [lang]",         "idiomatic cross-language code translation"
      "/scaffold <type> <name>",     "language-aware project scaffolding"
      "/complexity [file/dir]",      "cyclomatic complexity analysis and refactoring hints"
      "/breaking-changes <args>",    "detect breaking API changes and list call sites"
      "/idiomatic <file>",           "suggest idiomatic style improvements for the language"
      "/incremental <desc>",         "add a new function in-place without rewriting files"
      // ReadLine-only variants (visible in inline suggestions; absent from /help to avoid duplication)
      "/quit",                       strings.CmdExitDesc
      "/diff --staged",              strings.CmdDiffDesc
      "/doctor",                     "run environment diagnostics"
      "/model set",                  "switch model on current provider"
      "/model suggest",              "ask agent for model recommendation" ]
