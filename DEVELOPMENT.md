# Developing Party AI

The v0.5.1 candidate focuses on runtime regressions observed in v0.5.0: long main-thread searches, other actors timing out during those searches, movement oscillation, opaque short-rest preflight failures, and a mission-ending wrong-phase error. It retains the recovery and survival work, but the previous managed tests did not establish live-game reliability. Automated checks do not launch Gloomhaven; actual scenario testing remains with the user.

## Development Stages

These stages organize the requested improvements. Later version numbers are proposed, not published releases or fixed commitments.

| Stage | Status | Work |
| --- | --- | --- |
| v0.4.0: baseline and diagnostics | Merged; targeted runtime validation pending | Prompt serialization/recovery, visible handoffs, full printed/default alternatives, attack scoring corrections, safer repositioning, card-retention heuristics, structured captures, initial healing/attack/movement items, pure regression checks. |
| v0.5.0: recovery and survival | Tested; runtime regressions confirmed | Reviving Ether recovery and retention, retreat/support movement, bounded approach risk, door readiness, visible monster-intent estimates, ordinary short rests under threat, and synthetic game-reference tests. |
| v0.5.1: runtime regression candidate | Implemented locally; live validation pending | Cheap preselection, bounded refinement, per-move commitments, planner-excluding wait clocks, named short-rest preflight reasons, buffered diagnostics, and scoped end-turn completion. |
| Further endurance work | Follow-up | Defensive items before card loss, recovery/stamina item choices, retaliation, modifier-deck kill probabilities, improved short rests, and longer-horizon card/room budgeting. |
| v0.6.x: class and party play | Follow-up | A tested capability matrix for selected classes/loadouts, multi-target/area attacks, essential persistent effects and elements, shared destination/target intentions, coordinated rests and door opening. |
| Later: scenario coverage | Follow-up | Explicit exit, escort, interaction, and scenario-objective handling; broader class mechanics and repeatable scenario benchmarks. |

The planner still uses heuristic scores. It does not simulate cumulative nonlethal damage across attacks, compose a full route across successive moves, or forecast every condition and item interaction. Route validation deliberately rejects some risky paths instead of estimating whether their cost is worthwhile. Threat estimates do not fully simulate monster focus, pathfinding, shields, retaliation, or modifier draws. Card-retention scoring cannot protect every class engine it does not understand.

### Failure Cases Addressed

The v0.5.0 capture recorded 37.3 seconds between Spellweaver's opening decision and its last pair candidate. Tinkerer timed out without scoring a pair while that search occupied the main thread. Round 8 showed the reverse starvation pattern. The candidate replaces exhaustive route searches during card preselection with cheap estimates, limits refined work, and records planner elapsed time and query counts directly. The deadline is checked between engine calls, not inside them; synthetic millisecond timings are not proof of live frame latency.

Movement records also showed repeated `4:17 -> 5:17 -> 4:17` traversal during one move. Approach reward and retreat preference were being recomputed from each new origin. Endpoint commitment and visited-position checks now prevent that specific oscillation pattern.

The mission-ending fault followed an extra `EPASSMESSAGE` in `EndTurn`, then a delayed `EENDTURNSYNCHRONISEMESSAGE` arriving in `Action`. The log proves the extra ready-button path ran, not who initiated it. The candidate preserves the normal ready-button completion flow, binds owned delayed callbacks to their original phase, and blocks duplicate passes during synchronization. It never discards or manufactures the synchronization message. An SRL fault stops further automation, including unsubmitted owned completion callbacks. The later shutdown null-reference also appeared in the older run and is not established as the cause of the mission failure.

Two short-rest preflights failed before any automatic hand switch or draw. The old Boolean checks did not record which condition refused entry, so the precise original blocker remains unknown. Preflight now reports named reasons, caches capability checks, moves refresh-sensitive view checks into preparation, and avoids unrelated hidden-hand vetoes. Strict ownership checks remain on the actual dialog. These changes still require live validation.

The analysed run loaded v0.3.2, not v0.4.0. In round 9, Tinkerer crossed two traps for five damage and opened an eight-enemy room while Spellweaver rested. It remained in the doorway after healing, burned its remaining cards to prevent attacks, and died before its round-11 long rest. The new movement policy considers withdrawal without requiring a follow-up attack and treats door opening as a readiness decision rather than spare movement.

In round 14, Spellweaver selected Reviving Ether and Mana Bolt with three cards already in ordinary Lost, but used Ether as Attack 2 instead of recovering them. It exhausted in round 17. The recovery policy values additional playable turns, preserves the unused recovery card, and admits the actual printed recovery action with its fixed Dark infusion. It also handles the second-action state, where the companion card has already left the Round pile. Waiting until only two discards remained in round 16 would already have been too late; retaining Ether alone is not a complete fix.

## Developer Diagnostics

In `BepInEx/config/com.jsm.gloomhaven.partyai.cfg`, the opt-in setting is:

```ini
[Diagnostics]
DeveloperMode = true
```

The writer creates files only during an offline scenario. It does not upload them. Turning the setting off stops new capture and closes the file, flushing already captured records without creating a final summary. It does not delete existing files. Regular `LogDecisions` output is independent and can still contain character names.

### Files and Privacy

Files are stored under `BepInEx/PartyAI/diagnostics/`. `capture-0.jsonl` is newest; rotation retains at most five files of 5 MiB each. Older captures are replaced, so preserve a useful set before further testing. Read records by session and sequence rather than assuming one file equals one scenario. A session can span rotated files, and older portions can be missing.

Decision and outcome records contain game identifiers, actor class/GUID, positions, health, card-pile counts, condition flags, inventory-state counts, phase, round, timestamps, and counters. Candidate records use compact actor identity instead of rebuilding the full snapshot. Records do not include character names, save contents, chat, local paths, or exception text. Detail fields accept numeric values and fixed reason/action codes rather than arbitrary strings. Diagnostic I/O failures are contained and reported with a rate-limited warning in the BepInEx log.

Writes are buffered, with a periodic flush and immediate flushes for failures, handoffs, and session end. Abrupt process termination can lose recent buffered records. Rotation uses a logical byte count rather than a per-record stream-length query, which can flush buffers on Mono.

### What the Records Mean

| Record | Interpretation |
| --- | --- |
| `session_start` | Capture session and version context. Enabling capture partway through a scenario does not reconstruct earlier events. |
| `planner_timing` | Measured invocation time, path/LOS calls, candidate samples, cache hits, and work/time budget exhaustion. UI/queue waiting is not included. |
| `pair_candidate`, `action_candidate` | Scores for alternatives considered during card/action selection. Scores are heuristic utilities, not win probabilities. |
| `target_candidate`, `move_candidate` | Target/position evaluations. Position and actor state help explain which alternatives were available. |
| `retreat_candidate`, `door_decision` | Retreat/support scores and door-readiness outcomes. `future_damage` is estimated incoming damage; `threats` is the weighted exposure penalty, not a monster count. |
| `card_selection`, `action_selection`, `rest`, `damage_response` | Choices made by the controller. |
| `attack_submitted`, `heal_submitted`, `move_submitted` | A command was submitted. This does not prove it executed or helped. |
| `recovery_submitted`, `recovery_observed` | Recovery confirmation versus the game's recovery notification. The observed recovered-card count comes from the ability's starting Lost count and the remaining Lost pile. |
| `rest`, `short_rest_observed` | Rest intent or confirmation versus the game's completed short-rest notification. `redraw` records the normal one-HP redraw choice; it is not a planner-chosen loss. |
| `short_rest_blocked` | A named preflight refusal plus relevant numeric actor/UI state. Emitted on the first block and reason changes, not every polling frame. |
| `end_turn_submitted` | The ready button accepted an owned completion request, not proof that its animation callback or end-turn acknowledgement completed. |
| `item_candidate`, `item_evaluation`, `item_submitted`, `item_confirmed` | Item eligibility/scoring, an activation submission, or confirmation of the resulting supported item heal. Submission is not evidence that the item was consumed successfully. |
| `attack_observed`, `heal_observed`, `move_observed`, damage/target notifications | Game notifications, separate from submissions. Use the actor/target snapshots and round context to interpret effects. |
| `handoff`, `failure`, `invalid_action_observed` | Unsupported input, controller failure, or a game-reported invalid action. Handoffs explain `AI HELP` (called `AI WAIT` in earlier builds). |
| `scenario_result_observed`, `session_end` | Explicit result evidence and final counters when available. A generic stop is not counted as a loss. |

Counters appear on each record, so a missing final summary does not erase the latest totals. Observed-event counters include manual actors and enemies as well as bots; snapshots carry a nullable automation flag when that state is known. They count notifications, not unique tactical actions. Duplicate delivery of the same message object is suppressed, but undo/restart notifications do not subtract earlier outcomes. Damage totals use only explicitly populated damage fields and can be incomplete. Reported healing is not necessarily effective HP restored.

Win/loss/abandon results are captured from explicit game reporting, with scenario-state polling as a fallback. Abrupt termination, capture disabled mid-run, or a transition to a null/online scenario can leave no final summary. Treat those runs as incomplete or unknown, not successful or failed by inference.

### Assessing Competence

For an initial comparison, use the same scenario, difficulty, classes, levels, and loadouts across runs, and note the version and which characters are automated. Record any manual intervention and its reason. Variance from modifier draws means a single win or loss is weak evidence.

Useful measures include scenario completion, rounds to completion, exhausted mercenaries, cards lost to damage, manual interventions, unresolved prompts, invalid actions, attacks with no useful effect, and unused item opportunities. Candidate records can explain a bad choice, but deciding whether an alternative was better often still requires the board context or a screenshot.

No completion-rate target has been established yet. The first goal is to collect enough comparable runs to distinguish execution failures, tactical mistakes, unsupported mechanics, and ordinary bad draws. Pure scoring tests protect known invariants; scenario evidence is needed to establish competent play.

## Manual Validation

These are user-run checks, not tests already performed. Use the development DLL at `bin/Release/net472/GloomhavenPartyAI.dll`, not the unchanged tracked release artifact. The existing README smoke-test checklist still applies.

1. Enable developer mode in a disposable offline scenario. Confirm the startup log and JSONL records include the version being tested, planner timing, candidate scores, actor state, and round context. Disable it and confirm new capture stops. Verify online play creates no capture.
2. Toggle automation off while cards are being selected, then back on after the queued move finishes. Confirm exactly two cards and a usable initiative selection. Repeat with one manually selected card and an otherwise empty hand.
3. Keep a card preview open long enough to delay the bot, then close it. Confirm automation can resume. Exercise a mandatory bonus choice, resolve it manually, and check whether `AI HELP` clears. Record any case that requires a toggle to recover.
4. Exercise a multi-target heal and check that selected targets are not toggled off by duplicate processing. Trigger a damage prompt during another pending decision, including lethal damage with one hand card or two discarded cards available.
5. Give a character a printed attack plus a bottom heal where universal Move 2 enables the better turn. Check the candidate records and the executed card halves. Also compare targets with shields, low-health targets, and late-round Stun/Disarm opportunities.
6. Test moving away from ranged disadvantage, mixed-range attacks after one move, obstructed line of sight, difficult terrain, Jump trap landings, Fly, and doors. Record unexpectedly skipped moves as well as illegal or dangerous ones.
7. Test a healing potion at full health, moderately injured, critically injured, Poisoned, and Wounded. Check activation, self-heal confirmation, return to the card action, and item state. Repeat with `AutomateItems = false` and after a long rest.
8. Test boots when their bonus exactly enables a useful attack and when the attack is already reachable. Check that useful boots activate once, unneeded boots remain available, and the follow-up attack guidance survives activation.
9. Test an attack boost at a useful damage/kill breakpoint and against a shield that absorbs the entire boosted attack. Test goggles with and without existing advantage. Check item state, target choice, and completion of the attack.
10. Complete, lose, and abandon separate scenarios. Check explicit outcome records and summaries. Reload a scenario and confirm no decisions or plans from the previous run carry over.
11. Give Spellweaver three ordinary lost cards and a playable Ether/Mana Bolt pair. Check that recovery competes with the basic attack, returns only ordinary Lost cards, and allows the companion heal. Repeat with Ether as the second action, no hand cards, and at least two lost cards. Empty Lost should not spend Ether's top; its bottom remains normal Move 4/Jump.
12. After a heal or attack, leave a low-HP actor beside two enemies with movement available. Check for a safer retreat. Separately, a healthy melee actor outside a stationary ranged enemy's range should approach over successive moves rather than wait forever.
13. Approach a closed door while an ally rests or is too far behind. Confirm the bot stages before it, then can open once the party is ready and the opener has an attack left. Check the actual route for traps, not only its endpoint.
14. Run out of hand cards under threat with at least three discarded cards. Verify a normal random-loss short rest, one redraw only for an unused recovery card when HP permits, and fresh pair selection afterward. Toggle off during the dialog and complete it manually. Test hiding/switching a hand during the loss animation, another actor resting afterward, and unrelated dialogs. The AI should never click an unrelated popup or remain globally blocked by a cancelled animation. Zero hand plus two discards must not start a short rest that leaves no playable pair.
15. With full eight- and twelve-card hands, observe selection responsiveness and `planner_timing` records. A bot waiting on another bot's planning should not time out. Record any single-query overruns rather than assuming the 50 ms checkpoint deadline guarantees a frame bound.
16. Reproduce a move where approach and retreat favor opposite adjacent hexes. The same move should not repeatedly cross the edge; arrival should stop further optimization, while a new action can choose a new destination.
17. End a normal turn and a long rest, including a rapid extra confirm and a toggle during button effects. Verify exactly one turn-completion pass followed by the normal end-turn acknowledgement. Do not treat the managed callback/IL tests as proof of live Unity timing.

For a failure report, the diagnostic files, the relevant BepInEx log excerpt, the round, the actor class, and a short account of the expected versus actual action are more useful than a score alone. Include a screenshot when position or targeting is involved. Do not include game assemblies or save files in the repository.

## Verification Boundaries

The dependency-free regression project tests scoring invariants, copied-action identity, movement-rule cache keys, item thresholds, recovery/endurance, survival/door/approach rules, and JSON encoding. CI runs it on .NET 8 and 10.

The optional `tests/GameReferenceTests` project reads installed card definitions and links the real planner and recovery policy. Managed fixtures check Ether's Dark infusion, copied abilities, Lost versus Permanently Lost, retention, first/second-action selection, and approach/retreat decisions. These are synthetic board states, not a replay of the failed scenario. The fixture bypasses Unity-dependent rules-container constructors and seeds only the parser's character lookup. The main README includes its command and dependency requirements.

During v0.4.0 development, additional temporary harnesses checked installed item definitions and simulated diagnostic rotation, opt-out, and result handling. Those harnesses are not part of the repository test suite and do not replace runtime checks. Neither automated suite validates live short-rest dialogs, animation timing, recovery execution, or scenario completion. No release tag is implied by a development build or a local test installation.
