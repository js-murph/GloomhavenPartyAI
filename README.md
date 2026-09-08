# Gloomhaven Party AI

Choose which mercenaries you control in an offline Gloomhaven Digital scenario and let the mod play the others. Gloomhaven Party AI is a BepInEx plugin that selects cards and automates a limited set of movement, attack, healing, rest, and damage decisions. You can switch each living mercenary between manual and automated control from the initiative track.

This is an unofficial community project. It is not affiliated with or endorsed by Flaming Fowl Studios, Twin Sails Interactive, Cephalofair Games, or the BepInEx project. A legally obtained copy of Gloomhaven Digital is required; no game files are distributed in this repository or its releases.

The current source is a **v0.4.0 development build**, not an in-game-validated release. It adds tactical corrections, prompt recovery, developer diagnostics, and initial item automation. See the [development stages](DEVELOPMENT.md) for remaining competence gaps. The tracked release artifact has not been replaced by this development build.

## Limits

- The mod does not automate online games, and its controls are hidden while a game is online.
- The mod does not change actor type, ownership, or allegiance.
- The current planner handles simple Move abilities, including Jump and Fly when otherwise supported, single-target enemy attacks, and simple self or ally heals. It uses universal actions or waits for manual input when it cannot resolve a printed action.
- Toggle overrides last only for the current scenario and reset when the scenario stops or reloads.

## Installation

### 1. Install BepInEx 5

Download a BepInEx 5 64-bit Windows build from the [BepInEx releases page](https://github.com/BepInEx/BepInEx/releases). Extract it into the Gloomhaven installation directory next to `GH.exe`.

The resulting directory should contain:

```text
Gloomhaven/
|-- BepInEx/
|-- doorstop_config.ini
|-- GH.exe
`-- winhttp.dll
```

On Linux/Steam Deck with Proton, add this Steam launch option so Wine loads Doorstop's native `winhttp.dll`:

```text
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

Start and close the game once. Confirm that `BepInEx/LogOutput.log` was created. Steam library locations vary; a common Linux location is `~/.local/share/Steam/steamapps/common/Gloomhaven`.

### 2. Install Party AI

1. Download `GloomhavenPartyAI-vX.Y.Z.zip` from the [latest release](https://github.com/js-murph/GloomhavenPartyAI/releases/latest).
2. Extract the ZIP into `Gloomhaven/BepInEx/plugins/`.
3. Confirm the plugin is at `Gloomhaven/BepInEx/plugins/GloomhavenPartyAI/GloomhavenPartyAI.dll`.
4. Start Gloomhaven and confirm `BepInEx/LogOutput.log` contains `Gloomhaven Party AI vX.Y.Z loaded`.

To uninstall the mod, remove `BepInEx/plugins/GloomhavenPartyAI/`. This leaves BepInEx available for other mods.

## Configuration

After the first successful launch, edit:

`BepInEx/config/com.jsm.gloomhaven.partyai.cfg`

Set `HumanCharacter` to the in-game name or internal class ID of the mercenary that should start under manual control. Matching is case-insensitive. If the setting is empty or does not match, the first mercenary in the scenario player list starts under manual control. You can change that initial assignment with the scenario controls.

Other settings control the decision delay, decision logging, damage automation, and whether bots lose cards to prevent lethal damage. `Decisions.AutomateItems` defaults to `true` and enables the supported item choices described below. Setting `Enabled = false` disables automation and per-mercenary toggling without unloading the plugin. In an offline scenario, the controls remain visible but disabled.

During an offline scenario, click a mercenary's `AI ON` / `AI OFF` control on the initiative track to toggle automation. With a controller, focus the initiative track and press Down from that mercenary's portrait. Enabling AI can resume a supported open prompt after the configured delay. Damage prompts resume only when `AutomateDamage` is enabled.

`AI WAIT` means a prompt requires manual input or an automation attempt failed. The BepInEx log records the reason. A changed prompt can resume automation; toggling off and on also requests another attempt. Ordinary scheduled decisions retain the `AI ON` label. The controller retries transient card-UI readiness for up to three seconds and stops an individual worker after fifteen seconds, without cancelling commands already submitted to the game.

`Diagnostics.DeveloperMode` defaults to `false`. When enabled, it writes bounded offline JSONL captures under `BepInEx/PartyAI/diagnostics/`. It does not enable automation, upload data, or change the planner's scores. See [developer diagnostics](DEVELOPMENT.md#developer-diagnostics) for capture contents and interpretation.

## How automation works

- When enabled in an offline scenario, the configured human mercenary starts manual and the others start automated.
- Switching AI off cancels delayed, uncommitted decisions. A choice already submitted to the game finishes before manual control resumes.
- Bots compare printed and universal alternatives across card orientations and action orders. Movement forecasts use game path costs and line of sight; equivalent movement queries share a decision-local cache.
- Initiative favors early action when the selected plan has useful attacks or healing, or the actor is under pressure. Otherwise it favors the later initiative. It does not inspect unrevealed monster cards.
- After cards are revealed, bots compare supported top/bottom orientations and both action orders. A printed half is supported only when it consists of a simple Move, a single-target enemy Attack, or simple finite-target self/ally Heal abilities. Unsupported printed halves use a universal Move 2 or Attack 2 when available; prompts that still require unsupported input remain manual.
- Movement uses the player movement state machine and considers staying alongside reachable destinations. It scores the attack segment before the next move, penalizes hostile proximity, and applies a conservative ranged-adjacency disadvantage estimate.
- If enemy-focused movement has no usable destination, bots approach an eligible unlocked closed door. Entrances, exits, blocked doors, intact doors with health, and occupied door tiles are excluded. Route validation rejects unrevealed paths, intermediate closed doors, and unsafe trap or terrain activation; Jump and Fly receive separate landing checks. These conservative restrictions can leave movement manual or skipped where a human would choose to accept a hazard.
- Attacks account for shields, Pierce, capped damage, disabling conditions, and whether a projected kill prevents an enemy activation. Ineffective attacks no longer receive a target-threat bonus, and excess damage is not penalized. Stun and Disarm retain value after the target has acted. Modifier draws and monster intent are still approximated rather than simulated.
- Heals prioritize survival at 40% health or below and Poison or Wound removal. Routine healing and overhealing are discounted. The planner discourages a routine second heal and may choose the universal action instead; a second emergency heal remains eligible.
- Bots long-rest when the hand cannot supply two cards and at least two cards are discarded. Partial selections are returned before choosing that rest. Cards to lose are ranked by intrinsic abilities, initiative, and scarcity of reusable attack, movement, and healing roles, rather than only the current board. Short rests are not yet automated.
- With `AutomateDamage` enabled, bots accept nonlethal damage. With `PreventLethalDamage` also enabled, they lose the lowest-scored hand card if available, or the two lowest-scored discarded cards, to prevent lethal damage when possible.
- Optional additional attack targets are declined by the baseline automation.
- Unsupported area attacks, summons, forced movement, persistent bonuses, element consumes, dynamic values, conditional abilities, and special class mechanics are not planned. Mandatory item or active-bonus choices require manual input.

### Items

With `AutomateItems` enabled, bots evaluate available items by their effects rather than a list of item IDs. Availability still depends on the game's inventory state, requirements, and item UI. Items are activated through the normal item-use service; the mod does not mark items spent or consumed itself.

- Plain self-healing items are considered during action selection, including after a completed long rest. Poison/Wound removal and low health receive priority; consumable healing avoids small routine gains.
- Plain spent movement bonuses are considered when a verified safe route needs the extra movement to enable a useful follow-up attack. Boots are not spent speculatively to open another room.
- Plain attack-strength and advantage overrides are considered for supported single-target attacks. Consumable attack boosts need a meaningful gain or a projected kill breakpoint; redundant advantage is avoided.
- Recovery/stamina items, defensive damage-response choices, element selection, extra targets, complex effects, and stacking multiple item overrides are not automated yet. Passive equipment remains the game's responsibility.

Installed item definitions checked during development include Boots of Striding, Eagle-Eye Goggles, Minor/Major Healing Potions, and Minor/Major Power Potions. Passing definition checks does not establish that their live UI flows have been tested.

## Build

Prerequisites:

- .NET SDK 8 or newer.
- A local Gloomhaven Digital installation.
- BepInEx 5 installed in the game directory, or its `core` directory available separately.

Build with the game directory supplied as an MSBuild property:

```bash
dotnet build -c Release -p:GameRoot="/path/to/Gloomhaven"
```

The project uses `GameRoot/BepInEx/core` by default. Override it when necessary:

```bash
dotnet build -c Release \
  -p:GameRoot="/path/to/Gloomhaven" \
  -p:BepInExCore="/path/to/BepInEx/core"
```

The equivalent `GLOOMHAVEN_GAME_ROOT` and `BEPINEX_CORE` environment variables are also supported. These dependencies stay local; do not commit game or BepInEx assemblies.

The development DLL is produced at `bin/Release/net472/GloomhavenPartyAI.dll`. Building does not install it into the game or replace `artifacts/GloomhavenPartyAI.dll`.

### Regression checks

The isolated regression runner links the production scoring and JSON helpers without loading any game assemblies or launching the game:

```bash
dotnet run --project tests/RegressionTests.csproj --configuration Release
```

The default target is .NET 8. On a .NET 10-only machine, add `-p:RegressionTargetFramework=net10.0`. CI runs this runner on .NET 8 and 10. These checks cover numerical invariants and serialization, not actual game paths, coroutine timing, item UI behavior, or scenario completion.

## Releases

The release workflow does not build the plugin because the required local Gloomhaven assemblies are not available on the GitHub runner. Instead, it packages the plugin binary tracked at `artifacts/GloomhavenPartyAI.dll`. Game dependencies are not included.

To publish a version:

1. Update `Plugin.Version` and `PartyAIVersion` to the same `X.Y.Z` value.
2. Build Release locally against a legally installed copy of the game.
3. Replace `artifacts/GloomhavenPartyAI.dll` with the new build and commit it with the source changes.
4. Create and push a matching `vX.Y.Z` tag.

The workflow requires the tag, `Plugin.Version`, and `PartyAIVersion` to match. It also checks that the tracked DLL contains the release version string before publishing the DLL, an install-ready ZIP, generated release notes, and SHA-256 checksums.

## Runtime checks

To smoke-test current behavior, run these checks in a disposable offline scenario:

1. Confirm each living mercenary has the correct initial `AI ON` or `AI OFF` label and the configured mercenary starts in manual mode.
2. Toggle each mercenary with mouse and controller input, then verify switching AI on at a supported open prompt resumes automation. Enable `AutomateDamage` when checking a damage prompt.
3. Switch AI off during delayed card, action, movement, attack, heal, and damage decisions; each prompt should remain manually usable.
4. With `LogDecisions` enabled, confirm bots select exactly two cards, log pair and initiative decisions, and play supported actions through the normal card UI.
5. Exercise default and printed melee/ranged attacks, including universal multipass Attack 2.
6. Exercise ordinary, Jump, and Fly movement and verify the bot ends only on legal hexes.
7. Clear a room with an eligible unlocked closed door both within and beyond the current Move. Verify the bot opens it normally or advances toward it without crossing another closed door or unrevealed tile.
8. Test heals above and below 40% health, at full health, Poisoned, Wounded, and affected by Block Healing. Exercise routine and emergency double-heal cases and record the selected second action; routine second heals are penalized rather than prohibited.
9. Test Target 2 heals, supported attacks with no target, and skippable heals with no useful target; they should pass without a stuck prompt.
10. Confirm long rests, lethal-damage card loss, exhaustion, toggle reset after scenario reload, and normal manual handoff.
11. Review `BepInEx/LogOutput.log` for `[Decision]`, Harmony, or `Party AI decision failed` entries.

## Manual validation

The separate regression workflow runs the dependency-free checks. The release workflow checks version fields and creates packages from the tracked artifact; it still does not build against the game or run in-game tests. The maintainer reports completing a two-mercenary offline smoke test for `0.1.0` and manually exercising core tactical action execution in a later build.

The v0.4.0 development source compiles against the locally installed game assemblies. No game launch or installed-mod update was performed during this implementation. Prompt recovery, movement changes, item activation, and diagnostic lifecycle hooks still need user-run in-game validation. The [development guide](DEVELOPMENT.md#manual-validation) adds targeted checks for those changes.
