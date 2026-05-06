---
command: scaffold
args: [arg]
description: Language-aware project scaffolding
---
Generate a project scaffold for "{arg}".

Inspect the project structure first (use Glob/Read tools) to understand:
1. Language and framework in use.
2. Naming conventions, folder layout, and existing patterns.
3. Test framework and tooling.

Then generate the scaffold files following project conventions. Include:
- Main implementation file(s)
- Unit test skeleton
- Any registration/wiring needed in existing entry points

Write each file using the Write tool.
