# Regression Tests

These console runners test linked production source without launching Gloomhaven. Run commands from the repository directory. Each runner exits nonzero on failure.

## Pure Runner

```sh
dotnet run --project tests/RegressionTests.csproj
```

The default target is .NET 8. To use an installed .NET 10 SDK/runtime instead, add `-p:RegressionTargetFramework=net10.0` to any command here. The pure runner needs no game installation, Unity, Harmony, or external packages. `tests/NuGet.Config` clears package sources; optional reference projects are not included in the pure project.

`PlanningBudgetTests` links the actual `PlanningBudget.cs`. It checks the 32-path, 128-LOS and 512-sample hard caps, smaller/negative limits, the clamped 50ms deadline, sticky expiry, and a deadline reached after one admitted operation. Injected clocks use `Stopwatch` ticks; none of these assertions depend on scheduler timing or sleeping.

`DecisionClockTests` links the actual `DecisionClock.cs`. A simulated actor A occupies the shared thread for 37 seconds while actor B has a three-second UI deadline. The test excludes all measured planner work, preserves prior UI waiting, and expires B only after three seconds of non-planner time. Other cases cover UI/queue advancement between planning calls, reset, invalid durations and accumulated-duration overflow. These are clock arithmetic tests, not live coroutine scheduling tests.

## Optional Game References

Set `GLOOMHAVEN_GAME_ROOT` to an existing installation, or pass `-p:GameRoot="/path/to/Gloomhaven"`:

```sh
dotnet run --project tests/GameReferenceTests/GameReferenceTests.csproj
dotnet run --project tests/EndTurnReferenceTests/EndTurnReferenceTests.csproj
```

The planner runner reads installed rule data and managed libraries. The EndTurn runner also needs the installation's `BepInEx/core/0Harmony.dll` and `Mono.Cecil.dll`. Both embed `GameRoot` as assembly metadata and resolve the installed libraries read-only at runtime. They do not copy game DLLs or rule archives into test outputs. The repository's existing `bin/` and `obj/` ignore rules cover all three runners.

### Planner Coverage

- Actual linked `TacticalPlanner`, `RecoveryPlanner` and evaluation code, with only the disabled diagnostics sink replaced.
- Installed Ether parsing and copying, real card-pile recovery scoring, legal attack alternatives, and movement safety assertions.
- 8/12-card preselection with zero planner path/LOS queries and 28/66 pair samples; four refined plans from a larger shortlist with shared normal, tiny and zero operation budgets.
- Zero-budget and expired-deadline recovery fallback, including the last pair after its companion has been discarded.
- A clock that expires after one real path or LOS call; invocation-local successful/failed path caching and expired-cache validation.
- Attack/heal ranking restricted to supplied candidates, duplicate removal, stable score/focus/distance/ID ordering, useful-heal filtering, 512-entry caps, shared allowances and empty results on cold expiry. Ranking-only actor subclasses count shield/initiative reads while delegating to the actual engine implementations. Ranking still works with the pathfinder unavailable, checking that geometric tie-breaking does not hide engine path queries. Threat queries consume the supplied LOS allowance and treat expired/unknown visibility conservatively.
- Real engine pathfinding on synthetic boards: same-ability endpoint continuation, arrival with unused movement, obstruction, visited endpoint/route rejection, invalidation and reset. The `4:17 -> 5:17` case uses seven usable corridor hexes with blocked padding for the engine's LOS geometry. It is not a replay of a saved room.

`MEASURE` lines report warm-run median/max milliseconds and maximum operation counts for 8/12-card preselection, next-action refinement and movement. Counts are asserted; elapsed milliseconds are informational, not a performance pass/fail threshold. A synchronous engine call cannot be interrupted by this budget.

### EndTurn Coverage

The EndTurn runner links the actual `EndTurnController.cs`. `ControllerHost.cs` replaces only `AutomationController.IsScenarioReady()` with a controllable flag, defaulting to true. Reflection seeds inert real actor, phase, request and button fields. Test callbacks and synthetic rule-return values exercise phase ownership, duplicate Pass rejection, stale callbacks, reset, nested/unowned confirmations, failure before callback, explicit rejection, uncertain submission and callback exceptions.

The readiness case proves that a delayed request acquires neither callback execution nor `PassIssued` while the flag is false. An explicit `MarkFailed` then prevents that captured callback from resuming when readiness returns. The test calls this boundary directly; it does not execute production `ObserveFault` or establish that fault-message delivery reaches it.

Cecil reads the entire installed `ReadyButton.OnClickInternal` instruction stream. The actual transpiler processes it and must insert four callback captures while retaining instruction order; zero/three/five-match layouts fail closed. Callback-field operands are resolved to real `FieldInfo` objects. Other Cecil operands remain opaque because the transpiler does not inspect them. This checks transformation structure, not Harmony patch installation or execution of emitted IL.

The online case temporarily seeds Bolt's inert mode enum and the separate FFS shutdown flag so the real `FFSNetwork.IsOnline` getter returns true, then restores them. It does not start a network session.

## Limits

No runner invokes the Unity lifecycle, `TryEndTurn` UI acceptance path, delayed effect queue, actual rule submission, or live end-turn handshake. These runners do not link production `AutomationController` or `ShortRestPlanner`, so they do not verify the timing wrappers' integration with `DecisionClock`, the scenario fault latch/`ObserveFault` delivery, or named short-rest UI preflight behavior. Diagnostics batching/flush timing and short-rest log truncation are not covered here; the existing pure JSON tests cover serialization only. Reflection-based reference tests may require fixture updates when installed engine fields or private production APIs change.
