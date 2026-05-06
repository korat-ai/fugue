---
command: scaffold-cqrs
args: [cmdName]
description: Generate CQRS Command/Handler/Event/Test slice
---
Generate a complete CQRS slice for "{cmdName}".

Include:
1. **Command record** — immutable, validated constructor.
2. **CommandHandler** — reads from the command, applies business logic, emits an event.
3. **Domain Event** — added as a new DU case (or new type).
4. **EventHandler** — updates read model or side-effects.
5. **xUnit test skeleton** — arrange/act/assert for the happy path and one error case.

Follow the naming and layering conventions visible in the existing codebase (use Glob/Read tools to discover them). Return separate fenced code blocks per file.
