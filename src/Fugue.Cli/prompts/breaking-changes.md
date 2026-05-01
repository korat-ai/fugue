---
command: breaking-changes
args: [arg]
description: Detect breaking API changes and list call sites
---
Analyse the following for breaking API changes: "{arg}"

Check:
1. Renamed or removed public functions, types, or module members.
2. Changed parameter types or return types.
3. Added required parameters (non-optional).
4. Changed DU cases that callers must pattern-match on.
5. Removed interface members.

For each breaking change:
- Quote the old signature vs new signature.
- List all known call sites (use Grep tool).
- Suggest a deprecation path (default argument, type alias, or wrapper).

Use Read/Grep tools to discover call sites.
