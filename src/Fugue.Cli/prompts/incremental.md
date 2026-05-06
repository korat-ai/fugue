---
command: incremental
args: [desc]
description: Add a new function in-place without rewriting files
---
Add the following functionality incrementally to the existing codebase: "{desc}"

Rules:
1. Generate ONLY the new function(s) or type(s) — no file rewrites.
2. Find the best insertion point using Read and Grep tools.
3. Apply changes using the Edit tool to insert at the exact location.
4. Do not modify existing functions unless absolutely required.
5. Ensure the new code compiles by running `dotnet build --no-restore` after editing.
