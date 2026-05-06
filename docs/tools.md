# Tools

Tools are F# functions exposed to the LLM via `AIFunction` subclasses. The agent decides when and how to invoke them based on user intent. All tools are registered in `src/Fugue.Tools/ToolRegistry.fs` and available to the LLM on every turn.

## Tool inventory

| Name | Purpose | Mode | Side-effect | Key params |
|------|---------|------|-------------|-----------|
| **Read** | Read file with optional line range | sync | read | `path`, `offset`, `limit` |
| **Write** | Overwrite file at path | sync | write | `path`, `content` |
| **WriteBatch** | Atomically write multiple files (all-or-nothing) | sync | write | `files` (JSON array) |
| **Edit** | Replace exact string in file | sync | write | `path`, `old_string`, `new_string`, `replace_all` |
| **Bash** | Execute shell command via `/bin/sh -c` | sync | exec | `command`, `timeout_ms`, `clean_env` |
| **Glob** | Find files by pattern, sorted mtime descending | sync | read | `pattern`, `root` |
| **Grep** | Regex-search files, return path:line:text matches | sync | read | `pattern`, `root`, `glob`, `symbol` |
| **Tree** | Directory listing as ASCII tree | sync | read | `path`, `depth`, `glob` |
| **GetConversation** | Read session history as JSON | sync | read | `filter_role` |

## Per-tool reference

### Read

Read a text file, optionally within a line range. Output is 1-based line-numbered.

**Parameters:**
- `path` (string, required) — absolute or cwd-relative file path
- `offset` (int, optional) — 1-based start line (useful for files >200 lines)
- `limit` (int, optional) — max lines to read (prefer 100–200 for large files)

**Example invocation:**
```json
{
  "path": "src/Fugue.Cli/Program.fs",
  "offset": 50,
  "limit": 30
}
```

**Example return (truncated):**
```
50	let main argv =
51	    let cwd = System.Environment.CurrentDirectory
52	    let config = loadConfig cwd
...
```

---

### Write

Overwrite a file with new content. Creates parent directories as needed.

**Parameters:**
- `path` (string, required) — absolute or cwd-relative file path
- `content` (string, required) — full text to write

**Example invocation:**
```json
{
  "path": "README.md",
  "content": "# Fugue\n\nA small, fast F# agent.\n"
}
```

**Example return:**
```
Wrote 45 bytes to README.md
```

---

### WriteBatch

Atomically write multiple files in a single operation. Either all succeed or all fail (no partial writes).

**Parameters:**
- `files` (string, required) — JSON-stringified array of `{path, content}` objects

**Example invocation:**
```json
{
  "files": "[{\"path\":\"a.txt\",\"content\":\"foo\"},{\"path\":\"b.txt\",\"content\":\"bar\"}]"
}
```

**Example return:**
```
Wrote 2 files (a.txt, b.txt) in one atomic operation.
```

---

### Edit

Replace an exact substring in a file. Fails if the substring is not found or is not unique (unless `replace_all` is true).

**Parameters:**
- `path` (string, required) — file path
- `old_string` (string, required) — exact text to replace
- `new_string` (string, required) — replacement text
- `replace_all` (bool, optional, default false) — replace every occurrence

**Example invocation:**
```json
{
  "path": "src/Main.fs",
  "old_string": "let x = 10",
  "new_string": "let x = 20"
}
```

**Example return:**
```
Edited src/Main.fs: replaced 1 occurrence.
```

---

### Bash

Execute a shell command via `/bin/sh -c` (POSIX sh on Unix, pwsh on Windows). Returns stdout, stderr, and exit code.

**Parameters:**
- `command` (string, required) — shell command to run
- `timeout_ms` (int, optional, default 60000) — timeout in milliseconds; process killed after timeout
- `clean_env` (bool, optional, default false) — strip secret env vars (API keys, tokens, passwords) before running

**Example invocation:**
```json
{
  "command": "git log --oneline -5",
  "timeout_ms": 10000
}
```

**Example return:**
```
stdout:
a1b2c3d feat: add new tool
d4e5f6g fix: crash on empty input
...
stderr: (empty)
exit_code: 0
```

---

### Glob

Find files matching a glob pattern. Results are sorted by modification time (newest first).

**Parameters:**
- `pattern` (string, required) — glob pattern (e.g., `**/*.fs`, `src/**/Test*.cs`)
- `root` (string, optional) — root directory to search; defaults to cwd

**Example invocation:**
```json
{
  "pattern": "**/*.fs",
  "root": "src"
}
```

**Example return:**
```
src/Fugue.Tools/AiFunctions/ReadFn.fs
src/Fugue.Tools/AiFunctions/WriteFn.fs
src/Fugue.Cli/Program.fs
```

---

### Grep

Regex-search files and return matching lines with file path, line number, and text. Optional `--symbol` mode matches word boundaries and skips comment lines.

**Parameters:**
- `pattern` (string, required) — regex pattern
- `root` (string, optional) — root directory to search; defaults to cwd
- `glob` (string, optional) — file glob filter (e.g., `**/*.fs`)
- `symbol` (bool, optional, default false) — word-boundary search, skip comments

**Example invocation:**
```json
{
  "pattern": "let create",
  "glob": "**/*Fn.fs",
  "symbol": true
}
```

**Example return:**
```
src/Fugue.Tools/AiFunctions/ReadFn.fs:16: let create (cwd: string) : AIFunction =
src/Fugue.Tools/AiFunctions/WriteFn.fs:15: let create (cwd: string) : AIFunction =
src/Fugue.Tools/AiFunctions/EditFn.fs:17: let create (cwd: string) : AIFunction =
```

---

### Tree

Display directory structure as an ASCII tree. Optionally filter by filename pattern.

**Parameters:**
- `path` (string, optional, default `.`) — directory path (absolute or cwd-relative)
- `depth` (int, optional, default 3) — max recursion depth
- `glob` (string, optional) — filename pattern filter (e.g., `*.fs`)

**Example invocation:**
```json
{
  "path": "src/Fugue.Tools",
  "depth": 2
}
```

**Example return:**
```
src/Fugue.Tools/
├── AiFunctions/
│   ├── ReadFn.fs
│   ├── WriteFn.fs
│   ├── EditFn.fs
│   └── ...
├── ToolRegistry.fs
└── RetryPolicy.fs
```

---

### GetConversation

Read the current session conversation history as JSON. Use this to review context, build summaries, debug session state, or export dialogue.

**Parameters:**
- `filter_role` (string, optional) — `'user'`, `'assistant'`, or `'tool'`. Currently a no-op in Phase 1; filtering will be enforced in Phase 2.

**Return shape:**
```json
{
  "status": "ok",
  "turn_count": 5,
  "messages": [
    {
      "index": 0,
      "role": "user",
      "content": "Read src/main.rs",
      "has_tool_calls": false
    },
    {
      "index": 1,
      "role": "assistant",
      "content": "I'll read the file for you.",
      "has_tool_calls": true,
      "tool_calls": [
        {
          "name": "Read",
          "call_id": "toolu_01ARZ3NDEKTSV4RRFFQ69G40"
        }
      ]
    }
  ]
}
```

**Status values:**
- `ok` — history is available and returned in full
- `no_session` — no session has been created yet
- `history_unavailable` — session exists but chat history is not accessible (rare)

**Message fields:**
- `index` (int) — 0-based position in conversation
- `role` (string) — `'user'`, `'assistant'`, or `'tool'`
- `content` (string) — concatenated text content from all text parts
- `has_tool_calls` (bool) — true if message contains function invocations
- `tool_calls` (array, optional) — present only if `has_tool_calls` is true; each call has `name` and `call_id`

**Important caveat:** The `filter_role` parameter is recognized in the schema but is currently a no-op (returns all messages regardless). This will be implemented in Phase 2. Document any filtering workarounds in upstream code until then.

---

## What's coming

Future phases plan:
- **Session offload/resume** (#899 Phase 2) — save and load conversation history to disk or remote storage
- **GetConversation filtering** — `filter_role` parameter will actually filter by role
- **PreToolUse / PostToolUse hooks** — intercept or modify tool invocations before/after execution (#191, Phase 2)
- **MCP client integration** — expose Fugue tools to external MCP servers (#188, Phase 2+)
