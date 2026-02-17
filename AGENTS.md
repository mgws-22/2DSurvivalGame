# AGENTS.md — Codex rules for this Unity project

## 0) Prime directive
Performance is a hard requirement. Assume this project must scale to large entity counts.
When in doubt: choose the more data-oriented, cache-friendly, Burst/jobified approach.

Codex must:
- read relevant code before changing it
- keep hot paths allocation-free and Burst-compatible
- verify changes with profiler-minded reasoning + basic in-Unity validation steps

## 1) Repo + Unity invariants
- Unity project settings must keep:
  - Version Control: **Visible Meta Files**
  - Asset Serialization: **Force Text**
- Never commit generated folders: `Library/`, `Temp/`, `Obj/`, `Logs/`, `Build/`, `Builds/`.

## 2) Change policy (how Codex should work)
Before implementing:
- Identify the target system(s)/files and read them.
- State expected perf impact and risk (GC, structural changes, sync points).

When implementing:
- Prefer minimal, surgical diffs.
- Avoid large refactors unless explicitly requested or a plan exists in `Plans/`.

After implementing:
- Provide a short verification checklist (how to test in Unity + what counters to watch).

## 2.5 Automation-first (code over manual)
Codex should do the majority of work by:
- writing/patching code
- creating editor tooling
- generating assets procedurally when possible

Manual steps in the Unity Editor should be suggested only when:
- Unity requires it (e.g. one-time Project Settings toggles), or
- the cost of automation is clearly higher than the benefit.

If manual steps are unavoidable, list them as a short, exact checklist with menu paths.

### Placeholder art & sprites
When placeholder visuals are needed (sprites, icons, simple VFX):
- Prefer generating them via code (Editor scripts) rather than asking the user to draw/import manually.
- Put generators in `Assets/_Project/Editor/Tools/` (or similar).
- Put generated placeholder sprites in `Assets/_Project/Art/Generated/`.
- Keep placeholders small and deterministic (regeneratable).
- If generated outputs are large, commit the generator code and either:
  - keep outputs minimal, or
  - use Git LFS for the assets.


## 3) ECS / DOTS / Burst rules (mandatory)
### Burst usage
- Hot code must be Burst-compatible whenever possible:
  - Use `[BurstCompile]` on jobs and (where applicable) systems.
  - Avoid managed types and managed calls inside Burst regions.
- Avoid `Debug.Log` inside jobs and hot update loops.

### Jobs and scheduling
- Prefer `IJobEntity` / `IJobChunk` (or equivalent modern Entities patterns) for hot loops.
- Do not introduce `.Run()` on heavy queries unless explicitly justified.
- Avoid sync points: no unnecessary `Complete()`; complete only when correctness requires it.

### Structural changes
- Structural changes are expensive. Use:
  - `EntityCommandBuffer` for batched changes.
  - Prefer buffering changes and applying once per frame or in controlled phases.
- If structural changes happen frequently, call it out and propose mitigation.

### Update order
- Update order is **allowed to change** if it improves correctness/perf.
- If update order is changed, document it in the PR summary and keep it coherent (no accidental dependency cycles).

## 4) Memory + GC rules (mandatory)
### No GC in hot paths
- No LINQ, no `foreach` over managed collections, no string building in Update loops.
- No per-frame allocations (boxing, closures, new lists, etc.).
- Avoid `ToArray()`, `Select()`, `Where()` etc anywhere performance-critical.

### Native containers
- Use `NativeArray/NativeList/NativeParallelHashMap` etc as needed.
- Correct allocator choice:
  - `Temp` only within a frame
  - `TempJob` for jobs, disposed within 4 frames
  - `Persistent` for long-lived (must be disposed in `OnDestroy` / system cleanup)
- Every allocation must have an explicit disposal path.

## 5) Data layout + algorithms (performance heuristics)
- Prefer SoA-like component layouts and contiguous data access.
- Use spatial partitioning (grid / hashing) for neighbor queries; avoid O(n²).
- Avoid frequent random memory access; batch and reuse buffers.
- Prefer integer math / fixed-point where appropriate; avoid heavy trig in tight loops.

## 6) Logging / diagnostics / instrumentation
- Any profiling instrumentation must be cheap:
  - Use `ProfilerMarker` / scoped markers (not strings per frame).
- Add counters only when requested or when debugging a performance regression.
- Never spam logs during gameplay.

## 7) Safety and determinism
- Jobs must respect safety:
  - correct `[ReadOnly]`, correct container access, no race conditions
- Determinism: do not introduce non-deterministic ordering where gameplay depends on it (e.g. random iteration order)
  - If randomness is needed, use explicit seeded RNG.

## 8) Tests / validation (what Codex should do after changes)
Codex should include in its response:
- **Manual verification steps** in Unity (scene to run, what to observe).
- **Performance watchlist**:
  - GC Alloc should stay at 0 in hot gameplay
  - main-thread time not worse
  - no new sync points without justification

If a change likely affects performance, mention:
- expected big-O change
- likely bottleneck moved (CPU main thread vs jobs)

## 9) Output format for Codex responses
For each task, Codex must output:
1) Summary of changes (what and why)
2) Files changed (list)
3) Performance notes (GC, jobs, structural changes, sync points)
4) Verification checklist (Unity steps)
5) Manual steps (ONLY if required, with exact menu paths)
6) Risks / follow-ups (if any)

## 10) Planning rule (for large work)
If requested work is broad (new subsystem, major refactor, pathfinding, large spawning/steering changes):
- First create/update an ExecPlan in `Plans/<Topic>_ExecPlan.md` (unless user explicitly says “no plan”).
- Keep the plan short: goals, non-goals, steps, perf risks, verification.

## Documentation is a deliverable (mandatory)
After every task that changes runtime behavior or adds new code, Codex must:
1) Update or create the relevant doc in `Docs/Systems/...` for any new/changed ECS system or major module.
2) Append an entry to `Docs/DevLog.md` describing what changed, why, and how to test.
3) Update `Docs/Architecture/Index.md` to link any new docs.

Docs should focus on: purpose, data/components, invariants, perf constraints, and verification.
Do NOT create one doc per trivial helper class; group helpers under their owning system/module doc.
