---
command: scaffold-actor
args: [actorName]
description: Generate typed actor with command DU and supervision
---
Generate a typed actor stub for "{actorName}".

For F# / Proto.Actor:
1. Command DU with all message types.
2. `Actor` class implementing `IActor` with a `ReceiveAsync` dispatch.
3. Supervision strategy.
4. Props factory function.

For Scala / Akka Typed:
1. `Command` sealed trait hierarchy.
2. `Behavior[Command]` with `Behaviors.setup`.
3. `SupervisorStrategy` using `Behaviors.supervise`.

Infer the framework from the project (use Glob/Read to check dependencies). Return separate fenced blocks per file.
