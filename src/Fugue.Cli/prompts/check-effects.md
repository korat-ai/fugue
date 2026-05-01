---
command: check-effects
args: [file]
description: Verify ZIO/Cats Effect/Async type consistency
---
Review the following code for effect system type consistency:

```
{code}
```

Check:
1. Are all effectful operations wrapped in the same effect type (IO/ZIO/Task/F# Async) consistently?
2. Are effects mixed (e.g. ZIO and Future in the same for-comprehension) without proper lifting?
3. Are error channels compatible across composed effects?
4. Are any side effects performed outside the effect system (unsafe I/O, mutable state leaks)?

For each issue: show the problematic code, explain the violation, and provide a corrected version.

Source: {file}
