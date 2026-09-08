# Developing Competent Party AI

The v0.4.0 development build addresses baseline tactical mistakes and adds evidence collection. It is not a claim that the AI can complete arbitrary scenarios or play every class well. Actual game testing remains with the user; the work below was built and checked without launching Gloomhaven or changing its installed plugin.

## Development Stages

These stages organize the requested improvements. Later version numbers are proposed, not published releases or fixed commitments.

| Stage | Status | Work |
| --- | --- | --- |
| v0.4.0: baseline and diagnostics | Implemented in development source; runtime validation pending | Prompt serialization/recovery, visible handoffs, full printed/default alternatives, attack scoring corrections, safer repositioning, card-retention heuristics, structured captures, initial healing/attack/movement items, pure regression checks. |
| v0.5.x: survival and endurance | Follow-up | Short-rest decisions, defensive items before card loss, recovery/stamina choices, revealed monster-action threat estimates, retaliation, modifier-deck kill probabilities, and longer-horizon card/room budgeting. |
| v0.6.x: class and party play | Follow-up | A tested capability matrix for selected classes/loadouts, multi-target/area attacks, essential persistent effects and elements, shared destination/target intentions, coordinated rests and door opening. |
| Later: scenario coverage | Follow-up | Explicit exit, escort, interaction, and scenario-objective handling; broader class mechanics and repeatable scenario benchmarks. |

The first build still uses heuristic scores. It does not simulate cumulative nonlethal damage across attacks, compose a full route across successive moves, or forecast every condition and item interaction. Route validation deliberately rejects some risky paths instead of estimating whether their cost is worthwhile. Card-retention scoring cannot yet protect class engines it does not understand.

## Developer Diagnostics

In `BepInEx/config/com.jsm.gloomhaven.partyai.cfg`, the opt-in setting is:

```ini
[Diagnostics]
DeveloperMode = true
```

The writer creates files only during an offline scenario. It does not upload them. Turning the setting off closes capture without a final write; it does not delete files already captured. Regular `LogDecisions` output is independent and can still contain character names.

### Files and Privacy

Files are stored under `BepInEx/PartyAI/diagnostics/`. `capture-0.jsonl` is newest; rotation retains at most five files of 5 MiB each. Older captures are replaced, so preserve a useful set before further testing. Read records by session and sequence rather than assuming one file equals one scenario. A session can span rotated files, and older portions can be missing.

Records contain game identifiers, actor class/GUID, positions, health, card-pile counts, condition flags, inventory-state counts, phase, round, timestamps, and counters. They do not include character names, save contents, chat, local paths, or exception text. Detail fields accept numeric values and fixed reason/action codes rather than arbitrary strings. Diagnostic I/O failures are contained and reported with a rate-limited warning in the BepInEx log.

### What the Records Mean

| Record | Interpretation |
| --- | --- |
| `session_start` | Capture session and version context. Enabling capture partway through a scenario does not reconstruct earlier events. |
| `pair_candidate`, `action_candidate` | Scores for alternatives considered during card/action selection. Scores are heuristic utilities, not win probabilities. |
| `target_candidate`, `move_candidate` | Target/position evaluations. Position and actor state help explain which alternatives were available. |
| `card_selection`, `action_selection`, `rest`, `damage_response` | Choices made by the controller. |
| `attack_submitted`, `heal_submitted`, `move_submitted` | A command was submitted. This does not prove it executed or helped. |
| `item_candidate`, `item_evaluation`, `item_submitted`, `item_confirmed` | Item eligibility/scoring, an activation submission, or confirmation of the resulting supported item heal. Submission is not evidence that the item was consumed successfully. |
| `attack_observed`, `heal_observed`, `move_observed`, damage/target notifications | Game notifications, separate from submissions. Use the actor/target snapshots and round context to interpret effects. |
| `handoff`, `failure`, `invalid_action_observed` | Unsupported input, controller failure, or a game-reported invalid action. Handoffs explain `AI WAIT`. |
| `scenario_result_observed`, `session_end` | Explicit result evidence and final counters when available. A generic stop is not counted as a loss. |

Counters appear on each record, so a missing final summary does not erase the latest totals. Observed-event counters include manual actors and enemies as well as bots; snapshots carry a nullable automation flag when that state is known. They count notifications, not unique tactical actions. Duplicate delivery of the same message object is suppressed, but undo/restart notifications do not subtract earlier outcomes. Damage totals use only explicitly populated damage fields and can be incomplete. Reported healing is not necessarily effective HP restored.

Win/loss/abandon results are captured from explicit game reporting, with scenario-state polling as a fallback. Abrupt termination, capture disabled mid-run, or a transition to a null/online scenario can leave no final summary. Treat those runs as incomplete or unknown, not successful or failed by inference.

### Assessing Competence

For an initial comparison, use the same scenario, difficulty, classes, levels, and loadouts across runs, and note the version and which characters are automated. Record any manual intervention and its reason. Variance from modifier draws means a single win or loss is weak evidence.

Useful measures include scenario completion, rounds to completion, exhausted mercenaries, cards lost to damage, manual interventions, unresolved prompts, invalid actions, attacks with no useful effect, and unused item opportunities. Candidate records can explain a bad choice, but deciding whether an alternative was better often still requires the board context or a screenshot.

No completion-rate target has been established yet. The first goal is to collect enough comparable runs to distinguish execution failures, tactical mistakes, unsupported mechanics, and ordinary bad draws. Pure scoring tests protect known invariants; scenario evidence is needed to establish competent play.

## Manual Validation

These are user-run checks, not tests already performed. Use the development DLL at `bin/Release/net472/GloomhavenPartyAI.dll`, not the unchanged tracked release artifact. The existing README smoke-test checklist still applies.

1. Enable developer mode in a disposable offline scenario. Confirm JSONL records include v0.4.0, candidate scores, actor state, and round context. Disable it and confirm capture stops. Verify online play creates no capture.
2. Toggle automation off while cards are being selected, then back on after the queued move finishes. Confirm exactly two cards and a usable initiative selection. Repeat with one manually selected card and an otherwise empty hand.
3. Keep a card preview open long enough to delay the bot, then close it. Confirm automation can resume. Exercise a mandatory bonus choice, resolve it manually, and check whether `AI WAIT` clears. Record any case that requires a toggle to recover.
4. Exercise a multi-target heal and check that selected targets are not toggled off by duplicate processing. Trigger a damage prompt during another pending decision, including lethal damage with one hand card or two discarded cards available.
5. Give a character a printed attack plus a bottom heal where universal Move 2 enables the better turn. Check the candidate records and the executed card halves. Also compare targets with shields, low-health targets, and late-round Stun/Disarm opportunities.
6. Test moving away from ranged disadvantage, mixed-range attacks after one move, obstructed line of sight, difficult terrain, Jump trap landings, Fly, and doors. Record unexpectedly skipped moves as well as illegal or dangerous ones.
7. Test a healing potion at full health, moderately injured, critically injured, Poisoned, and Wounded. Check activation, self-heal confirmation, return to the card action, and item state. Repeat with `AutomateItems = false` and after a long rest.
8. Test boots when their bonus exactly enables a useful attack and when the attack is already reachable. Check that useful boots activate once, unneeded boots remain available, and the follow-up attack guidance survives activation.
9. Test an attack boost at a useful damage/kill breakpoint and against a shield that absorbs the entire boosted attack. Test goggles with and without existing advantage. Check item state, target choice, and completion of the attack.
10. Complete, lose, and abandon separate scenarios. Check explicit outcome records and summaries. Reload a scenario and confirm no decisions or plans from the previous run carry over.

For a failure report, the diagnostic files, the relevant BepInEx log excerpt, the round, the actor class, and a short account of the expected versus actual action are more useful than a score alone. Include a screenshot when position or targeting is involved. Do not include game assemblies or save files in the repository.

## Verification Boundaries

The durable regression project runs without proprietary dependencies. It tests scoring invariants, copied-action identity matching, movement-rule cache keys, item thresholds, and JSON encoding. CI runs it on .NET 8 and 10. A local game-referenced build checks API compatibility, not live Harmony behavior.

During development, additional temporary harnesses checked installed item definitions and simulated diagnostic rotation, opt-out, and result handling. Those harnesses are not part of the repository test suite and do not replace runtime checks. The game was not launched, the installed mod was not updated, and no release was published.
