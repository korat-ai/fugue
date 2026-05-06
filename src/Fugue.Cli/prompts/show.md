---
command: show
args: [turn_or_range]
description: preview a past message by turn number (e.g., /show 5 or /show 5..7)
---

Call the `GetConversation` tool to retrieve the full conversation history,
then render the message(s) matching the turn argument: {turn_or_range}.

Argument interpretation:
- A single integer like `5` → render only turn 5.
- A range like `5..7` → render turns 5, 6, 7 inclusive.
- An open range like `-3..` → render the last 3 turns.
- A negative integer like `-1` → render the previous turn.
- The current turn counter starts at 1 (the first user prompt of the session).

For each matching turn, output in this exact format:

[turn N — role]
verbatim content

Separate multiple turns with a single blank line. Render the message
content verbatim — do NOT summarize, paraphrase, or add commentary.
If the turn argument is out of range or the conversation is empty,
respond with a single line: `(no such turn)`.

Tool calls within an assistant message should be shown as
`[tool: name(args)]` on their own line, preserved in original order.
