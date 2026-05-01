---
command: port
args: [file, target]
description: Idiomatic cross-language code translation
---
Port the following code to idiomatic {target}:

```
{code}
```

Requirements:
1. Use the target language's idiomatic patterns — not a literal transliteration.
2. For OOP→FP: use DUs/sealed traits, immutable records, pattern matching.
3. For FP→OOP: map to classes, interfaces, exception handling.
4. Annotate each non-obvious translation decision with a brief inline comment.
5. Return the full translated file ready to use.
