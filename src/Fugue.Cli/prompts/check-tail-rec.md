---
command: check-tail-rec
args: [file]
description: Detect non-tail-recursive functions and offer rewrites
---
Analyse the following F# code for tail-recursion correctness:

```fsharp
{code}
```

For each recursive function:
1. Determine whether it is tail-recursive (the recursive call is the last operation in all branches).
2. If NOT tail-recursive, explain which call site is the problem and why it would stack-overflow on large inputs.
3. Offer a rewrite using:
   - Accumulator pattern (preferred)
   - Continuation-passing style (CPS) if accumulator doesn't fit
   - `[<TailCall>]` attribute hint where F# can optimise
4. Show the rewritten function alongside the original.

Source: {file}
