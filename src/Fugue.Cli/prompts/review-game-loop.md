---
command: review-game-loop
args: [target]
description: review game-engine code for hot-path performance anti-patterns (Unity/Godot/MonoGame/Stride/etc.)
---

Review the source under {target} for game-loop performance anti-patterns.
If {target} is empty or missing, default to `src/`.

This is NOT a generic code review. Apply the curated rule set below, in
priority order. Use Glob to find code files, then Read to inspect them.
Focus on functions that run every frame: `Update`, `LateUpdate`,
`FixedUpdate`, `_Process`, `_PhysicsProcess`, `OnRender`, custom game
loops, physics callbacks, animation tick handlers.

## Rule set

**A. Per-frame heap allocations** — these cause GC spikes.
- `new` inside an Update-class method (especially `new Vector3()`,
  `new List<T>()`, `new Dictionary<...>()`). Common in math expressions
  that hide it: `transform.position + new Vector3(0, 1, 0)`.
- `string.Format`, `string.Concat`, `$"..."` interpolation, `string + string`
  inside hot paths.
- LINQ chains: `.Where`, `.Select`, `.ToList`, `.Sum`, `.Count` allocate
  iterators/closures.
- Array allocation: `new T[N]`, `array.Skip(...).ToArray()`.
- Boxed value types: passing `int`/`float`/`struct` to a `params object[]`
  or `IEnumerable<object>` parameter.

**B. Missed object pooling opportunities**
- `Instantiate(prefab)` / `new GameObject()` in spawn loops without a
  reusable pool.
- `MemoryStream` / `StringBuilder` created per frame instead of reused.
- Particle effects spawned via `Instantiate` instead of `ParticleSystem.Emit`.

**C. Reflection and dynamic dispatch in hot paths**
- `GetComponent<T>()` called every frame instead of cached in `Awake/Start`.
- `Find`, `FindObjectOfType`, `FindGameObjectWithTag` per frame.
- `Type.GetMethod`, `MemberInfo.Invoke`, `dynamic` dispatch.

**D. Layout-thrashing physics access**
- Reading and writing `transform.position` / `Rigidbody.velocity` /
  `RigidBody2D.linearVelocity` repeatedly in one frame; consolidate
  into one read + one write.
- `Camera.main` access per frame (it does a tag-find lookup).

**E. Costly per-frame I/O**
- `Debug.Log` / `print` inside Update.
- `PlayerPrefs.SetX` / `GetX` per frame.
- `JsonUtility.ToJson` / `JsonConvert.SerializeObject` per frame.

## Output format

For each finding, produce a single line in this format:

```
[<severity>] <file>:<line>  <rule-id>  <one-line explanation>
```

Severity: `HIGH` (allocation in inner loop), `MED` (allocation per frame
but not nested), `LOW` (defensive — likely-but-confirm).

Group findings by file. After all findings, append a final section:

```
## Summary
- N HIGH, M MED, K LOW issues across F files
- Top suggestion: <single most-impactful fix>
```

If no findings, respond with `clean — no game-loop anti-patterns
detected under {target}` and stop.

Do NOT propose generic refactors, naming improvements, or non-perf
issues. Game-loop perf only. Do NOT auto-fix — surface findings for
the developer to triage.
