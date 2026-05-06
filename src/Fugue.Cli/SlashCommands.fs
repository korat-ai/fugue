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
    let hardcoded =
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
          "/sessions",                   "list sessions for current project"
          "/sessions all",               "list all sessions across all projects"
          "/index",                        "build local vector index of workspace files"
          "/index --update",             "incremental re-index of changed files"
          "/sem <query>",                "semantic search across indexed workspace files"
          "/search <query>",             "full-text search across session history (FTS5)"
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
          "/turns",                      "scrollable picker of turns in current session"
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
          "/check exhaustive [dir]",     "find FS0025 incomplete-pattern warnings and fix"
          "/compress",                   "summarise session history and reset context window"
          // ReadLine-only variants (visible in inline suggestions; absent from /help to avoid duplication)
          "/quit",                       strings.CmdExitDesc
          "/diff --staged",              strings.CmdDiffDesc
          "/doctor",                     "run environment diagnostics"
          "/model set",                  "switch model on current provider"
          "/model suggest",              "ask agent for model recommendation" ]
    // Prompt-template commands sourced from PromptRegistry (embedded + user overrides)
    let promptEntries =
        PromptRegistry.all ()
        |> List.map (fun t ->
            let argStr = t.Args |> List.map (fun a -> $"<{a}>") |> String.concat " "
            let label  = if argStr = "" then $"/{t.Command}" else $"/{t.Command.Replace('-', ' ')} {argStr}"
            label, t.Description)
    hardcoded @ promptEntries
