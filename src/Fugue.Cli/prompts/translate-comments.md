---
command: translate-comments
args: [file]
description: Translate code comments to English
---
Translate all code comments in the file "{file}" to English.

Rules:
1. Translate only comment text — do NOT touch identifiers, string literals, or code.
2. Preserve comment style (// vs /// vs /* */).
3. Keep the original meaning precisely; prefer clarity over literal translation.
4. Apply the translations using the Edit tool (one edit per comment block or file section).

Read the file first, then apply targeted edits.
