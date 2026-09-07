# Gloomhaven Party AI

An offline-only BepInEx mod for Gloomhaven Digital that lets each mercenary be controlled manually or by conservative tactical automation without changing actor allegiance.

This is an unofficial community project. It is not affiliated with or endorsed by Flaming Fowl Studios, Twin Sails Interactive, Cephalofair Games, or the BepInEx project. A legally obtained copy of Gloomhaven Digital is required; no game files are distributed in this repository or its releases.

## Safety and scope

- All automation is disabled and its controls are hidden in online games.
- The mod does not change actor type, ownership, or allegiance.
- Each living mercenary has an `AI ON` / `AI OFF` control on its initiative-track entry during an offline scenario.
- Unsupported abilities and mandatory choices remain under manual control rather than being guessed.
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

Set `HumanCharacter` to the campaign name or class ID of the mercenary that should start under manual control. If it is empty or does not match, the first mercenary seen when the scenario loads remains manual.

Other settings control the decision delay, decision logging, damage automation, and whether bots lose cards to prevent lethal damage. Setting `Enabled = false` disables all mod behavior without uninstalling it.

During an offline scenario, click a mercenary's initiative-track control to toggle automation. With a controller, focus the initiative track and press Down from that mercenary's portrait. Enabling AI while a supported card, action, movement, attack, heal, or damage prompt is open resumes automation after the configured delay.

## Current behavior

- The configured human mercenary starts manual; other mercenaries start automated.
- Switching AI off cancels delayed, uncommitted decisions. A choice already submitted to the game finishes before manual control resumes.
- Bots score complete card pairs for damage, emergency healing, movement, loss-card stamina cost, flexibility, and initiative coverage.
- Bots act faster when an enemy is close or health is low, and slower when immediate pressure is low.
- After cards are revealed, bots compare both legal top/bottom orientations and both action orders.
- Supported printed actions contain only ordinary Move, one-target enemy Attack, and simple finite-target self/ally Heal abilities. Universal Move 2 and Attack 2 remain legal fallbacks.
- Movement uses the player movement state machine, considers legal stopping hexes, preserves useful ranged distance, and penalizes exposure to nearby enemies.
- When no useful enemy-directed destination exists, bots approach the nearest safely reachable unlocked closed door and open it through normal movement. They never plan through another closed door or an unrevealed room.
- Attacks account for shields, Pierce, overkill, disabling conditions, target threat, and whether a kill prevents an enemy activation.
- Heals prioritize survival at 40% health or below and Poison or Wound removal. Routine and overhealing are discounted, and a second healing half normally becomes its universal action unless an emergency remains.
- Bots long-rest when fewer than two cards remain in hand and lose the card with the lowest estimated future tactical value.
- Bots accept ordinary damage and lose the lowest-value available card or cards to prevent lethal damage when possible.
- Optional additional attack targets are declined by the baseline automation.

Unsupported area attacks, summons, forced movement, persistent bonuses, element consumes, dynamic values, conditional abilities, special class mechanics, and mandatory item or active-bonus choices remain manual.

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

The equivalent `GLOOMHAVEN_GAME_ROOT` and `BEPINEX_CORE` environment variables are also supported. Game and BepInEx assemblies are referenced only from these local directories and must not be committed.

## Releases

GitHub-hosted runners cannot build the plugin without proprietary Gloomhaven assemblies. The tag-driven release workflow therefore publishes the reviewed plugin binary tracked at `artifacts/GloomhavenPartyAI.dll` rather than distributing game dependencies.

To publish a version:

1. Update `Plugin.Version` and `PartyAIVersion` to the same `X.Y.Z` value.
2. Build Release locally against a legally installed copy of the game.
3. Replace `artifacts/GloomhavenPartyAI.dll` with the new build and commit it with the source changes.
4. Create and push a matching `vX.Y.Z` tag.

The workflow rejects mismatched versions and publishes the DLL, an install-ready ZIP, generated release notes, and SHA-256 checksums.

## Runtime checks

Use a disposable offline scenario before relying on a campaign save:

1. Confirm each living mercenary has the correct initial `AI ON` or `AI OFF` label and the configured mercenary remains manual.
2. Toggle each mercenary with mouse and controller input, then verify switching AI on at an open prompt resumes automation.
3. Switch AI off during delayed card, action, movement, attack, heal, and damage decisions; each prompt should remain manually usable.
4. Confirm bots select exactly two cards, log pair/initiative decisions, and play supported actions through the normal card UI.
5. Exercise default and printed melee/ranged attacks, including universal multipass Attack 2.
6. Exercise ordinary, Jump, and Fly movement and verify the bot ends only on legal hexes.
7. Clear a room with an unlocked closed door both within and beyond the current Move. Verify the bot opens it normally or advances toward it without crossing another closed door or unrevealed tile.
8. Test heals above and below 40% health, at full health, Poisoned, Wounded, and affected by Block Healing. Verify routine double-heal turns use a universal second action while a still-critical target can receive another heal.
9. Test Target 2 heals and optional abilities with no useful target; they should pass without a stuck prompt.
10. Confirm long rests, lethal-damage card loss, exhaustion, toggle reset after scenario reload, and normal manual handoff.
11. Check `BepInEx/LogOutput.log` for `[Decision]`, Harmony, or `Party AI decision failed` entries.

## Validation status

The `0.1.0` baseline completed a two-mercenary offline scenario smoke test. The `0.2.0` build added per-mercenary controls. The `0.3.0` build added printed Move, Attack, and Heal planning and corrected the synchronous damage-response scheduler. Runtime testing exposed a stale Ready-button callback after automated long rests; `0.3.1` clears that callback and guards card-selection UI readiness. `0.3.2` corrects pre-start movement scoring, shifts healing toward emergencies, suppresses routine double-heal turns, and adds safe closed-door progression.

Core tactical action execution has been exercised in a live scenario. The `0.3.2` healing and door changes plus toggle timing, lethal-damage prevention, exhaustion, and scenario reload still need targeted runtime coverage.
