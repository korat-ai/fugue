---
command: complexity
args: [target]
description: Cyclomatic complexity analysis and refactoring hints
---
Analyse cyclomatic complexity of the code in "{target}".

For each function/method:
1. Compute approximate cyclomatic complexity (count decision points: if/match/loop/exception handlers).
2. Flag functions with complexity > 10 as high-risk.
3. Suggest refactorings for the top 3 most complex functions (extract method, simplify conditions, etc.).

Present results as a table: Function | Complexity | Risk | Refactoring suggestion.

Use Glob and Read tools to discover and examine the source files.
