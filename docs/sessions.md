# Sessions

A session is a conversation thread with the agent. Each Fugue run creates a fresh session on startup. The agent automatically saves all turns to JSONL (JSON Lines) format, enabling session restore and offline analysis. Phase 2 (#899) adds persistent storage and load/resume commands.

## JSONL file location

Session JSONL files are stored at:
```
~/.fugue/sessions/<encoded-cwd>/<ulid>.jsonl
```

The `<encoded-cwd>` segment encodes the working directory where Fugue ran. This allows Fugue to maintain per-project session history without collisions.

### Path encoding rules

When encoding a filesystem path as a directory name:

1. Trim trailing `/` or `\`
2. Replace every `/` and `\` with `-`
3. Replace `:` (Windows drive separator) with `_`

Leading `-` characters (from absolute POSIX paths like `/Users/roman`) are intentional and preserved.

The original `cwd` is also stored in the `SessionStart` JSONL record, so it can be recovered if needed.

### Examples

| Original cwd | Encoded name |
|---|---|
| `/home/alice/projects/myapp` | `-home-alice-projects-myapp` |
| `C:\Users\Bob\repos\service` | `C_-Users-Bob-repos-service` |
| `/opt/fugue/.hidden/test` | `-opt-fugue-.hidden-test` |

Each encoded directory can contain multiple JSONL files (one per session), named by ULID:
```
~/.fugue/sessions/-home-alice-projects-myapp/
  01HVZK7F4X0000000000000001.jsonl
  01HVZK7F4X0000000000000002.jsonl
```

The ULID is a 26-character Crockford Base32 identifier generated per session. ULIDs are lexicographically sortable by timestamp — files in the directory naturally sort by session creation time.

## JSONL record types

Each line in a session JSONL file is a single JSON object representing one event. The file is written by `SessionPersistence.appendRecord` on each turn.

| Type | JSON field | Contents | Notes |
|---|---|---|---|
| **SessionStart** | `type: "session_start"` | `ts` (ISO 8601), `cwd` (original working dir), `model` (LLM model name), `provider` (e.g. `"anthropic"`) | Written once at session creation. Stores context. |
| **UserTurn** | `type: "user_turn"` | `ts`, `content` (user's prompt text) | One per user input. |
| **AssistantTurn** | `type: "assistant_turn"` | `ts`, `content` (assistant response text) | One per agent response. Phase 2: `content` is `""` (deferred to Phase 3 with FTS5). |
| **ToolCall** | `type: "tool_call"` | `ts`, `tool` (tool name), `args` (JSON stringified), `call_id` (unique ID) | One per tool invocation. |
| **ToolResult** | `type: "tool_result"` | `ts`, `call_id` (matches ToolCall), `output`, `is_error` (bool) | One per tool completion. |
| **SessionEnd** | `type: "session_end"` | `ts`, `turn_count` (int), `duration_ms` (int) | Written once on `/session offload` or normal exit. |

Example JSONL excerpt:
```json
{"type":"session_start","ts":"2026-05-01T10:00:00Z","cwd":"/home/alice/project","model":"claude-sonnet-4-6","provider":"anthropic"}
{"type":"user_turn","ts":"2026-05-01T10:00:05Z","content":"Read src/main.rs"}
{"type":"tool_call","ts":"2026-05-01T10:00:06Z","tool":"Read","args":"{\"path\":\"src/main.rs\"}","call_id":"call_001"}
{"type":"tool_result","ts":"2026-05-01T10:00:07Z","call_id":"call_001","output":"fn main() { ... }","is_error":false}
{"type":"assistant_turn","ts":"2026-05-01T10:00:08Z","content":""}
{"type":"session_end","ts":"2026-05-01T10:05:00Z","turn_count":1,"duration_ms":295000}
```

## `/session offload`

Manually flush the current session to JSONL and prepare for a new one.

**Steps:**

1. Write a `SessionEnd` record with the current timestamp, `turnCount`, and `0` as duration (not yet computed).
2. Flush the JSONL file to disk.
3. Print the session ID and file path to the user.
4. Dispose the old session object and create a new empty session.
5. Inject a synthetic system note so the agent acknowledges the context switch:
   ```
   Previous context offloaded to session <id>. Use GetConversation to inspect history if needed.
   ```
6. Return to the ready prompt.

**Example interaction:**

```
> /session offload
Session saved: 01HVZK7F4X0000000000000001
File: /home/alice/.fugue/sessions/-home-alice-projects-myapp/01HVZK7F4X0000000000000001.jsonl

```

The session history is now on disk. A new session is ready for new input. Tooling can later read the JSONL file for analysis or replay.

## `/session resume <id>`

Load a previously offloaded session back into memory.

**Parameters:**

- `<id>` — ULID (26-character hex/base32 string, e.g., `01HVZK7F4X0000000000000001`)

**Steps:**

1. Look up the JSONL file by ULID in the current working directory's session subdirectory.
   - Encoding uses the same rules as on offload: `~/.fugue/sessions/<encoded-cwd>/<id>.jsonl`.
2. Read all records from the file using `SessionPersistence.readRecords`.
   - Malformed or unknown lines are silently skipped (returns a list of `SessionRecord option`).
3. Reconstruct a `List<ChatMessage>`:
   - `UserTurn` → `ChatMessage(User, content)`
   - `AssistantTurn` → `ChatMessage(Assistant, content)`
   - `ToolCall` → `ChatMessage(Assistant, "[tool_call:ToolName call_id:…] args-json")` (encoded as text to avoid AOT issues; see **Tool-call replay** below)
   - `ToolResult` → `ChatMessage(User, "[tool_result:call-id] [error] output-text")` (error tag present if `is_error: true`)
   - `SessionStart` / `SessionEnd` → skipped
4. Create a new session and load the reconstructed message list using `AgentSessionExtensions.SetInMemoryChatHistory`.
5. Print the number of messages loaded.

**Example interaction:**

```
> /session resume 01HVZK7F4X0000000000000001
Resumed session 01HVZK7F4X0000000000000001 — 7 messages loaded.

```

The conversation history is now in memory. The agent can continue, inspect via `GetConversation`, or start a new direction.

### Tool-call replay

Tool calls are encoded as **text prefixes** rather than native `FunctionCallContent` objects. This design avoids AOT trimming issues (`MakeGenericMethod`, reflection on `FunctionCallContent`).

On resume, tool calls appear as:
```
[tool_call:Read call_id:call_001] {"path":"src/main.rs"}
```

And results appear as:
```
[tool_result:call_001] fn main() { ... }
[tool_result:call_001] [error] File not found
```

The agent sees the full context but **will not re-invoke the tool**. The history is replayed for context; actual tool re-execution would require Phase 3+ design.

## Failure modes

### JSONL file missing on resume

If the file is not found in the encoded cwd directory:

```
Session not found: 01HVZK7F4X0000000000000001
```

The active session is **not modified**. User can retry with a correct ULID or `cd` to the correct project directory.

### Malformed JSONL lines during read

Lines that fail JSON parsing or have an unknown `type` field are silently skipped by `SessionRecord.deserialize` (returns `None`). `SessionPersistence.readRecords` uses `List.choose` to filter these out.

**Result:** A partial session is loaded. If 8 of 10 records parse, the agent sees 8 messages. No error is raised.

### Concurrent Fugue processes in same project

Two Fugue instances running in the same cwd will both write to the same JSONL files. Writes from `File.AppendAllText` may interleave — records can be corrupted or mixed. This is an **accepted limitation in Phase 2** (same trade-off as `SessionSummary.save`).

**Mitigation:** Use `/session offload` to cleanly separate sessions before running concurrent instances.

### User offloads twice in a row

The first `/session offload` writes `SessionEnd`, resets history, and injects the orientation note. If `/session offload` is called again immediately (without new turns):

1. A second `SessionEnd` is written with `turn_count: 0` (no new user turns).
2. The session is cleared and reset again.
3. A new `SessionStart` begins.

The JSONL file now contains:
```
SessionStart → (no turns) → SessionEnd
SessionStart → SessionEnd
```

Both are valid and benign. `GetConversation` reflects the latest state (empty). Resume by ULID remains correct.

## Related features

### GetConversation tool (Phase 1)

The `GetConversation` tool reads the **in-memory** session history as JSON. Use this to review context, build summaries, or debug state during a session.

See `docs/tools.md` for full reference.

### SessionStart and SessionEnd hooks

Hooks can run custom scripts at session lifecycle events. See `docs/hooks.md` for configuration and examples.

Example: fire an async webhook when a session ends.

### Future: Phase 3 session search

Planned features (not yet shipped):

- **SQLite FTS5 index** — full-text search across all session JSONL files
- **`/sessions` command** — list all sessions in current project with summary stats
- **Cross-cwd resume** — `/session resume --all <id>` to find and load a session from any project
- **AssistantTurn full-text capture** — Phase 2 defers assistant response text; Phase 3 stores it for search
- **Session metadata** — tags, duration, model/provider, turn count in a lightweight index

## Limitations (Phase 2)

The following are **not yet supported**:

- **Cross-cwd resume** (`--all` flag) — Phase 3. Currently must `cd` to the original project directory to resume.
- **Search across sessions** — Phase 3 (requires SQLite FTS5 index).
- **`/sessions` listing command** — Phase 3. No CLI command to list available sessions.
- **AssistantTurn full-text storage** — Phase 2 writes `content: ""` for assistant turns. Full text is captured in memory but not persisted (defer to Phase 3 with FTS5).
- **Tool-call re-invocation on resume** — Tool calls and results are replayed as text for context, but no live re-execution. Useful for context but not automation replay.
- **Atomic multi-record writes** — Phase 2 uses `File.AppendAllText`; each record is written individually. Phase 3 may batch for crash recovery.

---

## See also

- `docs/tools.md` — `GetConversation` tool reference
- `docs/hooks.md` — `SessionStart` and `SessionEnd` hooks
- `CLAUDE.md` — project conventions and Phase 3 roadmap

