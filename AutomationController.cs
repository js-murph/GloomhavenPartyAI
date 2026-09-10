using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ScenarioRuleLibrary;
using UnityEngine;

namespace GloomhavenPartyAI
{
    internal static class AutomationController
    {
        private readonly struct DecisionKey : IEquatable<DecisionKey>
        {
            internal readonly bool Damage;
            internal readonly string ActorGuid;
            internal readonly int ActorEpoch;

            internal DecisionKey(bool damage, string actorGuid, int actorEpoch)
            {
                Damage = damage;
                ActorGuid = actorGuid;
                ActorEpoch = actorEpoch;
            }

            public bool Equals(DecisionKey other)
            {
                return Damage == other.Damage && ActorGuid == other.ActorGuid &&
                    ActorEpoch == other.ActorEpoch;
            }

            public override bool Equals(object obj)
            {
                return obj is DecisionKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                int hash = (Damage.GetHashCode() * 397) ^ (ActorGuid?.GetHashCode() ?? 0);
                return (hash * 397) ^ ActorEpoch;
            }
        }

        private sealed class PromptStamp
        {
            internal object Token;
            internal string State;
            internal bool NeedsInput;
            internal string RestReadiness;

            internal bool Matches(PromptStamp other)
            {
                return other != null && ReferenceEquals(Token, other.Token) && State == other.State;
            }
        }

        private const float WaitTimeoutSeconds = 15f;
        private const float UiTimeoutSeconds = 3f;
        private static Plugin _plugin;
        private static string _humanActorGuid;
        private static readonly object StateLock = new object();
        private static readonly HashSet<DecisionKey> PendingDecisions = new HashSet<DecisionKey>();
        private static readonly Dictionary<string, PromptStamp> SettledPrompts = new Dictionary<string, PromptStamp>();
        private static readonly Dictionary<string, PromptStamp> ItemAttempts = new Dictionary<string, PromptStamp>();
        private static readonly HashSet<string> ItemSubmitted = new HashSet<string>();
        private static readonly HashSet<string> ForcedManualActors = new HashSet<string>();
        private static readonly HashSet<string> ForcedAutomatedActors = new HashSet<string>();
        private static readonly Dictionary<string, int> ActorEpochs = new Dictionary<string, int>();
        private static readonly Dictionary<string, CPlayerSelectingToAvoidDamageOrNot_MessageData> DamagePrompts =
            new Dictionary<string, CPlayerSelectingToAvoidDamageOrNot_MessageData>();
        private static readonly Dictionary<string, object> DamageOperations = new Dictionary<string, object>();
        private static readonly HashSet<CAbilityAttack> CommittedAttacks = new HashSet<CAbilityAttack>();
        private static readonly FieldInfo ActionTopCard = AccessTools.Field(typeof(CardsActionControlller), "topCard");
        private static readonly FieldInfo ActionBottomCard = AccessTools.Field(typeof(CardsActionControlller), "bottomCard");
        private static bool _onlineWarningLogged;
        private static int _generation;
        private static readonly DecisionClock WaitClock = new DecisionClock(() => Time.realtimeSinceStartup);
        private static bool _scenarioFaulted;

        // Queue/UI timeouts must not charge one actor for another actor's synchronous planning.
        private static double DecisionTime => WaitClock.Now;

        internal static void Attach(Plugin plugin)
        {
            _plugin = plugin;
        }

        internal static void Reset()
        {
            lock (StateLock)
            {
                _humanActorGuid = null;
                PendingDecisions.Clear();
                SettledPrompts.Clear();
                ItemAttempts.Clear();
                ItemSubmitted.Clear();
                ForcedManualActors.Clear();
                ForcedAutomatedActors.Clear();
                ActorEpochs.Clear();
                DamagePrompts.Clear();
                DamageOperations.Clear();
                CommittedAttacks.Clear();
                _onlineWarningLogged = false;
                _generation++;
                WaitClock.Reset();
                _scenarioFaulted = false;
            }
            TacticalPlanner.Reset();
            ShortRestPlanner.Reset();
            EndTurnController.Reset();
        }

        internal static bool IsAutomated(CActor actor)
        {
            if (!CanAutomate() || !(actor is CPlayerActor player))
            {
                return false;
            }

            EnsurePartyRegistered();
            lock (StateLock)
            {
                return IsAutomatedByState(player.ActorGuid);
            }
        }

        // Read-only lookup for diagnostics; do not register the party while taking a snapshot.
        internal static bool? AutomationState(CActor actor)
        {
            if (!(actor is CPlayerActor) || string.IsNullOrEmpty(actor.ActorGuid)) return null;
            if (Plugin.ModEnabled == null || !Plugin.ModEnabled.Value || FFSNetwork.IsOnline) return false;
            lock (StateLock)
                return string.IsNullOrEmpty(_humanActorGuid) ? (bool?)null : IsAutomatedByState(actor.ActorGuid);
        }

        internal static bool ToggleAutomation(CPlayerActor actor)
        {
            if (actor == null || actor.IsDead || !CanAutomate())
            {
                return false;
            }

            EnsurePartyRegistered();
            bool enabled;
            lock (StateLock)
            {
                bool currentlyEnabled = IsAutomatedByState(actor.ActorGuid);
                if (currentlyEnabled)
                {
                    ForcedAutomatedActors.Remove(actor.ActorGuid);
                    ForcedManualActors.Add(actor.ActorGuid);
                }
                else
                {
                    ForcedManualActors.Remove(actor.ActorGuid);
                    ForcedAutomatedActors.Add(actor.ActorGuid);
                }

                ActorEpochs[actor.ActorGuid] = GetActorEpochByState(actor.ActorGuid) + 1;
                ClearPromptState(actor);
                enabled = !currentlyEnabled;
            }

            TacticalPlanner.InvalidatePlan(actor);
            Record("toggle", actor, "action=toggle;reason=user_toggle;enabled=" + (enabled ? "1" : "0"));
            Decision(Describe(actor) + " AI toggled " +
                (enabled ? "on." : "off; manual control resumes at the next uncommitted prompt."));
            if (enabled)
            {
                ResumeAutomation(actor);
            }
            return enabled;
        }

        internal static bool CanToggleAutomation()
        {
            return _plugin != null && Plugin.ModEnabled.Value && !FFSNetwork.IsOnline;
        }

        internal static bool ConsumeCommittedAttack(CAbilityAttack attack)
        {
            lock (StateLock)
            {
                return attack != null && CommittedAttacks.Remove(attack);
            }
        }

        private static bool IsAutomatedByState(string actorGuid)
        {
            if (string.IsNullOrEmpty(actorGuid))
            {
                return false;
            }
            if (ForcedAutomatedActors.Contains(actorGuid))
            {
                return true;
            }
            return !ForcedManualActors.Contains(actorGuid) && !string.IsNullOrEmpty(_humanActorGuid) &&
                actorGuid != _humanActorGuid;
        }

        private static int GetActorEpochByState(string actorGuid)
        {
            return ActorEpochs.TryGetValue(actorGuid, out int epoch) ? epoch : 0;
        }

        internal static void HandleMessage(CMessageData message)
        {
            // Keep an open damage prompt resumable even if automation was disabled when it arrived.
            if (!FFSNetwork.IsOnline && message is CPlayerSelectingToAvoidDamageOrNot_MessageData damagePrompt)
                HandleDamageMessage(damagePrompt);
            if (!CanAutomate() || message == null)
            {
                return;
            }

            switch (message.m_Type)
            {
                case CMessageData.MessageType.Undo:
                case CMessageData.MessageType.RestartRound:
                    foreach (CPlayerActor player in ScenarioManager.Scenario?.PlayerActors.ToList() ?? new List<CPlayerActor>())
                    {
                        NotifyManualAction(player);
                    }
                    break;
                case CMessageData.MessageType.PlayerToSelectAbilityCardsOrLongRest:
                    EnsurePartyRegistered();
                    List<CPlayerActor> players = ScenarioManager.Scenario?.PlayerActors;
                    if (players == null)
                    {
                        break;
                    }
                    foreach (CPlayerActor actor in players.ToList())
                    {
                        if (IsAutomated(actor))
                        {
                            Schedule(actor, SelectRoundCards(actor));
                        }
                    }
                    break;
                case CMessageData.MessageType.ActionSelection:
                    if (IsAutomated(message.m_ActorSpawningMessage))
                    {
                        CPlayerActor actor = (CPlayerActor)message.m_ActorSpawningMessage;
                        Schedule(actor, actor.CharacterClass.HasLongRested && !actor.CharacterClass.LongRest && !actor.IsTakingExtraTurn
                            ? FinishLongRest(actor) : SelectAction(actor));
                    }
                    break;
                case CMessageData.MessageType.PlayerLongRested:
                    if (IsAutomated(message.m_ActorSpawningMessage))
                    {
                        CPlayerActor actor = (CPlayerActor)message.m_ActorSpawningMessage;
                        Schedule(actor, FinishLongRest(actor));
                    }
                    break;
                case CMessageData.MessageType.ActorIsSelectingMoveTile:
                    HandleMoveMessage(message as CActorIsSelectingMoveTile_MessageData);
                    break;
                case CMessageData.MessageType.ActorIsSelectingAttackFocusTargets:
                    HandleAttackMessage(message as CActorIsSelectingAttackFocusTargets_MessageData);
                    break;
                case CMessageData.MessageType.ActorIsSelectingTargetingFocus:
                    HandleTargetingMessage(message as CActorIsSelectingTargetingFocus_MessageData);
                    break;
                case CMessageData.MessageType.PlayerSelectingToAvoidDamageOrNot:
                    break;
            }
        }

        internal static bool IsScenarioReady()
        {
            return !_scenarioFaulted && !ScenarioRuleClient.ScenarioRuleClientStopped &&
                PhaseManager.CurrentPhase != null && Choreographer.s_Choreographer != null &&
                !Choreographer.s_Choreographer.IsRestarting &&
                SceneController.Instance?.GlobalErrorMessage?.ShowingMessage != true &&
                !FFSNetwork.IsStartingUp && !FFSNetwork.IsShuttingDown;
        }

        internal static void ObserveFault(CMessageData message)
        {
            if (message?.m_Type == CMessageData.MessageType.SRLExceptionMessage ||
                message?.m_Type == CMessageData.MessageType.SRLWrongPhaseExceptionMessage)
            {
                _scenarioFaulted = true;
                EndTurnController.MarkFailed(GameState.InternalCurrentActor as CPlayerActor);
            }
        }

        private static bool CanAutomate()
        {
            if (_plugin == null || !Plugin.ModEnabled.Value)
            {
                return false;
            }

            lock (StateLock)
            {
                if (!FFSNetwork.IsOnline)
                {
                    _onlineWarningLogged = false;
                    return true;
                }

                if (!_onlineWarningLogged)
                {
                    _onlineWarningLogged = true;
                    Plugin.Log.LogWarning("Party AI is disabled because this is an online game.");
                }
                return false;
            }
        }

        private static void EnsurePartyRegistered()
        {
            lock (StateLock)
            {
                CScenario scenario = ScenarioManager.Scenario;
                List<CPlayerActor> activePlayers = scenario?.PlayerActors;
                if (activePlayers == null)
                {
                    _humanActorGuid = null;
                    return;
                }

                List<CPlayerActor> players = activePlayers
                    .Concat(scenario.ExhaustedPlayers ?? new List<CPlayerActor>())
                    .ToList();
                if (players.Count == 0)
                {
                    _humanActorGuid = null;
                    return;
                }

                if (!string.IsNullOrEmpty(_humanActorGuid) && players.Any(p => p.ActorGuid == _humanActorGuid))
                {
                    return;
                }

                string configured = Plugin.HumanCharacter.Value.Trim();
                CPlayerActor human = null;
                if (configured.Length > 0)
                {
                    human = players.FirstOrDefault(p =>
                        string.Equals(p.CharacterName, configured, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.CharacterClass.CharacterID, configured, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.CharacterClass.ID, configured, StringComparison.OrdinalIgnoreCase));
                }

                human = human ?? players[0];
                _humanActorGuid = human.ActorGuid;
                Plugin.Log.LogInfo("Manual mercenary: " + Describe(human) + ". Automated: " +
                    string.Join(", ", activePlayers.Where(p => p != human).Select(Describe).ToArray()));
                if (configured.Length > 0 && !Matches(human, configured))
                {
                    Plugin.Log.LogWarning("HumanCharacter '" + configured + "' was not found; using the first mercenary.");
                }
            }
        }

        private static bool Matches(CPlayerActor actor, string value)
        {
            return string.Equals(actor.CharacterName, value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actor.CharacterClass.CharacterID, value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actor.CharacterClass.ID, value, StringComparison.OrdinalIgnoreCase);
        }

        private static void Schedule(CPlayerActor actor, IEnumerator routine,
            bool waitForQueue = true)
        {
            if (!IsScenarioReady() || !IsAutomated(actor) || ShortRestPlanner.IsPendingAny) return;
            DecisionKey key;
            int generation;
            int actorEpoch;
            PromptStamp prompt;
            lock (StateLock)
            {
                actorEpoch = GetActorEpochByState(actor.ActorGuid);
                key = new DecisionKey(!waitForQueue, actor.ActorGuid, actorEpoch);
                // Epoch changes cancel future AI steps, not the game's already queued command.
                if (PendingDecisions.Any(p => p.ActorGuid == actor.ActorGuid && p.Damage == key.Damage) ||
                    IsSettled(actor, key.Damage))
                {
                    return;
                }
                prompt = GetPrompt(actor, key.Damage);
                if (prompt == null) return;
                PendingDecisions.Add(key);
                generation = _generation;
            }
            try
            {
                _plugin.StartCoroutine(RunScheduled(key, generation, actorEpoch, actor, prompt, routine, waitForQueue));
            }
            catch (Exception exception)
            {
                RemovePending(key);
                Plugin.Log.LogError("Party AI scheduling failed: " + exception);
                Record("failure", actor, "reason=exception");
                Handoff(actor, "exception", key.Damage);
            }
        }

        private static string Channel(CActor actor, bool damage) => actor.ActorGuid + (damage ? ":damage" : ":exclusive");

        private static PromptStamp GetPrompt(CPlayerActor actor, bool damage)
        {
            if (actor == null || actor.IsDead) return null;
            if (damage)
            {
                if (!DamagePrompts.TryGetValue(actor.ActorGuid, out CPlayerSelectingToAvoidDamageOrNot_MessageData message) ||
                    !GameState.WaitingForPlayerToSelectDamageResponse ||
                    !DamageOperations.TryGetValue(actor.ActorGuid, out object operation) ||
                    !ReferenceEquals(operation, GameState.CurrentDamageData) ||
                    GameState.CurrentDamageData?.ActorDamaged != message.m_ActorBeingAttacked) return null;
                // Duplicate notifications share the damage operation, not necessarily the message instance.
                return new PromptStamp { Token = GameState.CurrentDamageData, State = "damage" };
            }
            CCharacterClass cards = actor.CharacterClass;
            if (ShortRestPlanner.IsPending(actor))
                return new PromptStamp { Token = actor, State = "short-rest:" + ShortRestPlanner.Status };
            if (PhaseManager.PhaseType == CPhase.PhaseType.SelectAbilityCardsOrLongRest)
            {
                return new PromptStamp { Token = PhaseManager.CurrentPhase, State = "cards:" + cards.LongRest + ":" +
                    string.Join(",", cards.RoundAbilityCards.Select(c => c.CardInstanceID.ToString()).ToArray()) + ":" +
                    cards.HandAbilityCards.Count + ":" + cards.DiscardedAbilityCards.Count + ":" +
                    (InitiativeTrack.Instance != null) + ":" + (CardsHandManager.Instance?.CurrentHand != null) };
            }
            if (GameState.InternalCurrentActor != actor) return null;
            if (PhaseManager.PhaseType == CPhase.PhaseType.ActionSelection)
            {
                return new PromptStamp { Token = PhaseManager.CurrentPhase, State = "action:" +
                    GameState.CurrentActionSelectionSequence + ":" + cards.LongRest + ":" + cards.HasLongRested + ":" +
                    GameState.PendingOnLongRestBonuses.Count + ":" + cards.RoundAbilityCards.Count + ":" +
                    GameState.WaitingForMercenarySpecialMechanicSlotChoice + ":" + actor.IsTakingExtraTurn + ":" +
                    CardsHandManager.Instance?.IsFullCardPreviewShowing + ":" +
                    CardsHandManager.Instance?.cardsActionController?.IsActionAvailable + ":" +
                    Choreographer.s_Choreographer?.readyButton?.IsInteractable };
            }
            CAbility ability = (PhaseManager.Phase as CPhaseAction)?.CurrentPhaseAbility?.m_Ability;
            if (ability is CAbilityMerged merged) ability = merged.ActiveAbility;
            string state;
            if (ability is CAbilityMove move && move.State == CAbilityMove.EMoveState.ActorIsSelectingMoveTile)
                state = "move:" + actor.ArrayIndex + ":" + move.RemainingMoves;
            else if (ability is CAbilityAttack attack && (attack.State == CAbilityAttack.EAttackState.SelectAttackFocus ||
                attack.State == CAbilityAttack.EAttackState.SelectAttackFocusAdditionalTargets))
                state = "attack:" + attack.State + ":" + attack.IsWaitingForSingleTargetItemOrActiveBonus() + ":" +
                    string.Join(",", attack.ActorsToTarget.Select(a => a.ActorGuid).ToArray());
            else if (ability is CAbilityHeal heal && heal.CanReceiveTileSelection())
                state = "heal:" + heal.IsWaitingForSingleTargetItemOrActiveBonus() + ":" +
                    string.Join(",", heal.ActorsToTarget.Select(a => a.ActorGuid).ToArray());
            else if (ability is CAbilityRecoverLostCards recovery && recovery.CanReceiveTileSelection())
                state = "recover:" + recovery.IsWaitingForSingleTargetItemOrActiveBonus() + ":" +
                    actor.CharacterClass.LostAbilityCards.Count + ":" +
                    string.Join(",", recovery.ActorsToTarget.Select(a => a.ActorGuid).ToArray());
            else if (ability != null && ability.CanReceiveTileSelection()) state = "unsupported";
            else return null;
            return new PromptStamp { Token = ability, State = state };
        }

        private static bool IsSettled(CPlayerActor actor, bool damage)
        {
            string channel = Channel(actor, damage);
            if (!SettledPrompts.TryGetValue(channel, out PromptStamp settled)) return false;
            if (settled.Matches(GetPrompt(actor, damage)) &&
                (settled.RestReadiness == null || ShortRestPlanner.IsPendingAny ||
                 settled.RestReadiness == ShortRestPlanner.PreflightSignature(actor))) return true;
            SettledPrompts.Remove(channel);
            return false;
        }

        internal static bool NeedsInput(CActor actor)
        {
            if (!(actor is CPlayerActor player) || !IsAutomated(actor)) return false;
            lock (StateLock)
            {
                return (IsSettled(player, false) && SettledPrompts[Channel(actor, false)].NeedsInput) ||
                    (IsSettled(player, true) && SettledPrompts[Channel(actor, true)].NeedsInput);
            }
        }

        private static void ClearPromptState(CActor actor)
        {
            SettledPrompts.Remove(Channel(actor, false));
            SettledPrompts.Remove(Channel(actor, true));
            ItemAttempts.Remove(actor.ActorGuid);
        }

        // Main's manual-input patches can call this for actions that do not change prompt identity.
        internal static void NotifyManualAction(CActor actor)
        {
            if (actor == null) return;
            lock (StateLock)
            {
                ClearPromptState(actor);
                ActorEpochs[actor.ActorGuid] = GetActorEpochByState(actor.ActorGuid) + 1;
            }
            TacticalPlanner.InvalidatePlan(actor);
        }

        private static void Handoff(CPlayerActor actor, string reason, bool damage = false)
        {
            lock (StateLock)
            {
                if (IsSettled(actor, damage)) return;
                PromptStamp prompt = GetPrompt(actor, damage);
                if (prompt == null) return;
                prompt.NeedsInput = true;
                if (reason == "short_rest_unavailable" && !ShortRestPlanner.IsPendingAny && ShortRestPlanner.IsTransientBlock)
                    prompt.RestReadiness = ShortRestPlanner.PreflightSignature(actor);
                SettledPrompts[Channel(actor, damage)] = prompt;
            }
            Record("handoff", actor, "reason=" + reason);
            Plugin.Log.LogWarning(Describe(actor) + " AI HELP: " + reason +
                (reason == "short_rest_unavailable" ? " (" + ShortRestPlanner.LastBlockReason + ")" : "") +
                "; manual input or a changed ready state is required.");
        }

        // Main-thread polling at about 0.5 seconds; never manufactures a prompt or drains commands.
        internal static void Reconcile()
        {
            if (!IsScenarioReady()) return;
            // Short-rest popups use shared UI. Finish only the helper's owned operation and
            // let a manually completed rest release its lock even if automation was switched off.
            if (ShortRestPlanner.IsPendingAny)
            {
                CPlayerActor owner = ShortRestPlanner.Owner;
                try
                {
                    ShortRestPlanner.TryContinue(owner);
                    if (ShortRestPlanner.Status == "manual") Handoff(owner, "short_rest_unavailable");
                    else if (!ShortRestPlanner.IsPendingAny)
                    {
                        ClearPromptState(owner);
                        TacticalPlanner.InvalidatePlan(owner);
                    }
                }
                catch (Exception exception)
                {
                    Plugin.Log.LogError("Party AI short-rest decision failed: " + exception);
                    Record("failure", owner, "reason=exception");
                    Handoff(owner, "exception");
                }
                return;
            }
            if (!CanAutomate()) return;
            foreach (CPlayerActor actor in ScenarioManager.Scenario?.PlayerActors.ToList() ?? new List<CPlayerActor>())
            {
                try
                {
                    lock (StateLock)
                    {
                        IsSettled(actor, false);
                        IsSettled(actor, true);
                        if (GetPrompt(actor, true) == null)
                        {
                            DamagePrompts.Remove(actor.ActorGuid);
                            DamageOperations.Remove(actor.ActorGuid);
                        }
                    }
                    if (IsAutomated(actor)) ResumeAutomation(actor);
                }
                catch (Exception exception)
                {
                    Plugin.Log.LogError("Party AI reconciliation failed: " + exception);
                    Record("failure", actor, "reason=exception");
                    Handoff(actor, "exception");
                }
            }
        }

        private static void ResumeAutomation(CPlayerActor actor)
        {
            CPlayerSelectingToAvoidDamageOrNot_MessageData damageMessage;
            lock (StateLock)
            {
                DamagePrompts.TryGetValue(actor.ActorGuid, out damageMessage);
            }
            if (Plugin.AutomateDamage.Value && damageMessage != null && IsCurrentDamagePrompt(actor, damageMessage) &&
                GameState.WaitingForPlayerToSelectDamageResponse && GameState.CurrentDamageData?.ActorDamaged ==
                damageMessage.m_ActorBeingAttacked)
            {
                Schedule(actor, ResolveDamage(damageMessage), waitForQueue: false);
                return;
            }
            if (GameState.WaitingForPlayerToSelectDamageResponse) return;
            lock (StateLock)
            {
                if (PendingDecisions.Any(p => p.ActorGuid == actor.ActorGuid && !p.Damage)) return;
            }
            if (ScenarioRuleClient.IsProcessingOrMessagesQueued) return;

            if (PhaseManager.PhaseType == CPhase.PhaseType.SelectAbilityCardsOrLongRest)
            {
                Schedule(actor, SelectRoundCards(actor));
                return;
            }

            if (PhaseManager.PhaseType == CPhase.PhaseType.ActionSelection && GameState.InternalCurrentActor == actor)
            {
                if (GameState.WaitingForMercenarySpecialMechanicSlotChoice)
                {
                    Handoff(actor, "unsupported");
                    return;
                }
                if (actor.CharacterClass.HasLongRested && !actor.CharacterClass.LongRest && !actor.IsTakingExtraTurn)
                {
                    Schedule(actor, FinishLongRest(actor));
                }
                else
                {
                    Schedule(actor, SelectAction(actor));
                }
                return;
            }

            if (PhaseManager.Phase is CPhaseAction actionPhase && GameState.InternalCurrentActor == actor)
            {
                CPhaseAction.CPhaseAbility phaseAbility = actionPhase.CurrentPhaseAbility;
                CAbility ability = phaseAbility?.m_Ability;
                if (ability is CAbilityMerged merged)
                {
                    ability = merged.ActiveAbility;
                }

                if (ability is CAbilityMove move && TacticalPlanner.IsSupportedMove(move) &&
                    move.State == CAbilityMove.EMoveState.ActorIsSelectingMoveTile)
                {
                    Schedule(actor, ContinueMove(actor, move));
                    return;
                }
                if (ability is CAbilityAttack attack && TacticalPlanner.IsSupportedAttack(attack) &&
                    (attack.State == CAbilityAttack.EAttackState.SelectAttackFocus ||
                     attack.State == CAbilityAttack.EAttackState.SelectAttackFocusAdditionalTargets))
                {
                    Schedule(actor, ContinueAttack(actor, attack));
                    return;
                }
                if (ability is CAbilityHeal heal &&
                    heal.CanReceiveTileSelection())
                {
                    Schedule(actor, ContinueHeal(actor, heal));
                    return;
                }
                if (ability is CAbilityRecoverLostCards recovery && recovery.CanReceiveTileSelection())
                {
                    Schedule(actor, ContinueRecovery(actor, recovery));
                    return;
                }
                if (GetPrompt(actor, false) != null) Handoff(actor, "unsupported");
            }
        }

        private static IEnumerator RunScheduled(DecisionKey key, int generation, int actorEpoch,
            CPlayerActor actor, PromptStamp initial, IEnumerator routine, bool waitForQueue)
        {
            try
            {
                yield return new WaitForSecondsRealtime(Math.Min(5f, Math.Max(0f, Plugin.DecisionDelay.Value)));
                double deadline = DecisionTime + WaitTimeoutSeconds;
                bool finished = false;
                bool started = false;
                if (!DecisionIsCurrent(key, generation, actorEpoch, actor)) yield break;
                Record("decision", actor, key.Damage ? "action=damage" : "reason=best_score");
                while (DecisionIsCurrent(key, generation, actorEpoch, actor))
                {
                    // A short-rest UI operation owns the global dialog, including while other
                    // actors' previously scheduled card-selection workers were waiting.
                    if (ShortRestPlanner.IsPendingAny) yield break;
                    if (DecisionTime >= deadline)
                    {
                        if (EndTurnController.IsPending(actor))
                        {
                            EndTurnController.MarkFailed(actor);
                            Record("failure", actor, "reason=timeout");
                            Handoff(actor, "timeout");
                            yield break;
                        }
                        // Stop only our iterator. Never undo/cancel a command already in the rule queue,
                        // and retain committed attack authorization until the game's Perform consumes it.
                        // A completed command may already have opened the next prompt. Never
                        // mark that successor as failed on behalf of this older worker.
                        if (initial.Matches(GetPrompt(actor, key.Damage)))
                        {
                            Record("failure", actor, "reason=timeout");
                            Handoff(actor, "timeout", key.Damage);
                        }
                        yield break;
                    }
                    if (waitForQueue && (ScenarioRuleClient.IsProcessingOrMessagesQueued ||
                        GameState.WaitingForPlayerToSelectDamageResponse))
                    {
                        yield return null;
                        continue;
                    }
                    if (finished)
                    {
                        bool itemSubmitted = !key.Damage && ItemSubmitted.Remove(actor.ActorGuid);
                        PromptStamp current = GetPrompt(actor, key.Damage);
                        if (!itemSubmitted && current != null && !IsSettled(actor, key.Damage))
                        {
                            if (current.Matches(initial)) Handoff(actor, "invalid_state", key.Damage);
                        }
                        yield break;
                    }
                    bool hasNext;
                    object next = null;
                    try
                    {
                        if (!started && !initial.Matches(GetPrompt(actor, key.Damage))) yield break;
                        started = true;
                        hasNext = routine.MoveNext();
                        if (hasNext) next = routine.Current;
                    }
                    catch (Exception exception)
                    {
                        Plugin.Log.LogError("Party AI decision failed: " + exception);
                        Record("failure", actor, "reason=exception");
                        Handoff(actor, "exception", key.Damage);
                        yield break;
                    }
                    finished = !hasNext;
                    // All decision iterators yield frames, so the deadline is checked even during UI/message waits.
                    yield return next;
                }
            }
            finally
            {
                (routine as IDisposable)?.Dispose();
                lock (StateLock)
                {
                    if (generation == _generation)
                    {
                        if (!key.Damage) ItemSubmitted.Remove(actor.ActorGuid);
                        RemovePending(key);
                    }
                }
            }
        }

        private static bool TryItem(CPlayerActor actor, Func<bool> submit)
        {
            if (Plugin.AutomateItems == null || !Plugin.AutomateItems.Value || !IsAutomated(actor)) return false;
            PromptStamp prompt = GetPrompt(actor, false);
            if (prompt == null || ItemAttempts.TryGetValue(actor.ActorGuid, out PromptStamp attempted) &&
                attempted.Matches(prompt)) return false;
            if (!submit()) return false;
            ItemAttempts[actor.ActorGuid] = prompt;
            ItemSubmitted.Add(actor.ActorGuid);
            // An override changes the active ability, not the committed card halves. Keep the
            // cross-card followup so boots do not erase the attack they were used to enable.
            if (PhaseManager.PhaseType == CPhase.PhaseType.ActionSelection)
                TacticalPlanner.InvalidatePlan(actor);
            return true;
        }

        private static bool DecisionIsCurrent(DecisionKey key, int generation, int actorEpoch, CPlayerActor actor)
        {
            lock (StateLock)
            {
                if (generation != _generation || actorEpoch != GetActorEpochByState(key.ActorGuid) ||
                    !PendingDecisions.Contains(key))
                {
                    return false;
                }
            }
            return IsScenarioReady() && (!key.Damage || Plugin.AutomateDamage.Value) && IsAutomated(actor);
        }

        private static void RemovePending(DecisionKey key)
        {
            lock (StateLock)
            {
                PendingDecisions.Remove(key);
            }
        }

        private static IEnumerator SelectRoundCards(CPlayerActor actor)
        {
            EnsurePartyRegistered();
            while (ScenarioRuleClient.IsProcessingOrMessagesQueued)
            {
                yield return null;
            }
            if (PhaseManager.PhaseType != CPhase.PhaseType.SelectAbilityCardsOrLongRest ||
                !IsAutomated(actor) || actor.IsDead)
            {
                yield break;
            }

            CCharacterClass character = actor.CharacterClass;
            double uiDeadline = DecisionTime + UiTimeoutSeconds;
            while (InitiativeTrack.Instance == null || CardsHandManager.Instance?.CurrentHand == null)
            {
                if (DecisionTime >= uiDeadline)
                {
                    Handoff(actor, "timeout");
                    yield break;
                }
                yield return null;
                if (PhaseManager.PhaseType != CPhase.PhaseType.SelectAbilityCardsOrLongRest) yield break;
            }
            if (character.LongRest)
            {
                RefreshCardSelectionReady(actor);
                yield break;
            }

            int cardsNeeded = Math.Max(0, 2 - character.RoundAbilityCards.Count);
            if (character.HandAbilityCards.Count < cardsNeeded)
            {
                // A half-selected pair must go back to hand before the game can offer a rest.
                foreach (CAbilityCard card in character.RoundAbilityCards.ToList())
                {
                    uint returned = ScenarioRuleClient.MoveAbilityCard(character, card, character.RoundAbilityCards,
                        character.HandAbilityCards, "RoundAbilityCards", "HandAbilityCards", networkAction: false);
                    while (returned > ScenarioRuleClient.s_SRLLastProcessedMessageID) yield return null;
                    if (PhaseManager.PhaseType != CPhase.PhaseType.SelectAbilityCardsOrLongRest) yield break;
                }
                character.SetInitiativeAbilityCard(null);
                character.SetSubInitiativeAbilityCard(null);
                if (character.RoundAbilityCards.Count == 0 && character.DiscardedAbilityCards.Count >= 2)
                {
                    if (Plugin.AutomateShortRests.Value && Plan(actor, "threat", budget => TacticalPlanner.IsUnderThreat(actor, budget)))
                    {
                        if ((long)character.HandAbilityCards.Count + character.DiscardedAbilityCards.Count - 1 < 2)
                        {
                            Handoff(actor, "no_cards");
                            yield break;
                        }
                        if (character.ImprovedShortRest)
                        {
                            Handoff(actor, "unsupported");
                            yield break;
                        }
                        double deadline = DecisionTime + UiTimeoutSeconds;
                        while (!ShortRestPlanner.TryStart(actor))
                        {
                            if (DecisionTime >= deadline)
                            {
                                Handoff(actor, "short_rest_unavailable");
                                yield break;
                            }
                            yield return null;
                            if (PhaseManager.PhaseType != CPhase.PhaseType.SelectAbilityCardsOrLongRest ||
                                character.RoundAbilityCards.Count != 0 || character.HandAbilityCards.Count >= 2)
                                yield break;
                        }
                        Record("rest", actor, "action=rest;reason=under_threat");
                        Decision(Describe(actor) + " starts a short rest rather than waiting under enemy threat.");
                        if (ShortRestPlanner.Status == "manual") Handoff(actor, "short_rest_unavailable");
                        yield break;
                    }
                    character.LongRest = true;
                    actor.IsLongRestSelected = true;
                    Decision(Describe(actor) + " selected a long rest.");
                    Record("rest", actor, "action=rest;reason=no_cards");
                }
                else
                {
                    Handoff(actor, "no_cards");
                }
                RefreshCardSelectionReady(actor);
                yield break;
            }

            List<CAbilityCard> cards = cardsNeeded == 0 ? new List<CAbilityCard>() :
                Plan(actor, "cards", budget => TacticalPlanner.ChooseRoundCards(actor, cardsNeeded, budget));
            if (cards.Count != cardsNeeded)
            {
                Handoff(actor, "no_cards");
                yield break;
            }

            foreach (CAbilityCard card in cards)
            {
                while (ScenarioRuleClient.IsProcessingOrMessagesQueued)
                {
                    yield return null;
                }
                if (PhaseManager.PhaseType != CPhase.PhaseType.SelectAbilityCardsOrLongRest ||
                    character.RoundAbilityCards.Count >= 2 || !character.HandAbilityCards.Contains(card))
                {
                    break;
                }

                uint messageId = ScenarioRuleClient.MoveAbilityCard(character, card, character.HandAbilityCards,
                    character.RoundAbilityCards, "HandAbilityCards", "RoundAbilityCards", networkAction: false);
                while (messageId > ScenarioRuleClient.s_SRLLastProcessedMessageID)
                {
                    yield return null;
                }
            }
            if (PhaseManager.PhaseType != CPhase.PhaseType.SelectAbilityCardsOrLongRest) yield break;
            if (character.RoundAbilityCards.Count < 2)
            {
                Handoff(actor, "no_cards");
                yield break;
            }
            List<CAbilityCard> roundCards = character.RoundAbilityCards.ToList();
            CAbilityCard initiativeCard = Plan(actor, "initiative", budget => TacticalPlanner.ChooseInitiativeCard(actor, roundCards));
            CAbilityCard subInitiativeCard = roundCards.First(card => card != initiativeCard);
            character.SetInitiativeAbilityCard(initiativeCard);
            character.SetSubInitiativeAbilityCard(subInitiativeCard);
            actor.IsLongRestSelected = false;
            Decision(Describe(actor) + " selected " + CardName(initiativeCard) +
                " (initiative) and " + CardName(subInitiativeCard) + " using tactical card scoring.");
            Record("card_selection", actor, "action=cards;cards=" + roundCards.Count + ";initiative=" + initiativeCard.Initiative);

            RefreshCardSelectionReady(actor);
        }

        private static void RefreshCardSelectionReady(CPlayerActor actor)
        {
            if (InitiativeTrack.Instance != null && CardsHandManager.Instance?.CurrentHand != null)
            {
                InitiativeTrack.Instance.CheckRoundAbilityCardsOrLongRestSelected();
                PromptStamp prompt = GetPrompt(actor, false);
                if (prompt != null && (actor.CharacterClass.LongRest || actor.CharacterClass.RoundAbilityCards.Count >= 2))
                    SettledPrompts[Channel(actor, false)] = prompt;
            }
        }

        private static IEnumerator SelectAction(CPlayerActor actor)
        {
            if (GameState.InternalCurrentActor != actor || PhaseManager.PhaseType != CPhase.PhaseType.ActionSelection)
            {
                yield break;
            }
            if (GameState.WaitingForMercenarySpecialMechanicSlotChoice)
            {
                Handoff(actor, "unsupported");
                yield break;
            }

            CCharacterClass character = actor.CharacterClass;
            double readyDeadline = DecisionTime + UiTimeoutSeconds;
            while (Choreographer.s_Choreographer?.readyButton == null)
            {
                if (DecisionTime >= readyDeadline)
                {
                    Handoff(actor, "timeout");
                    yield break;
                }
                yield return null;
                if (GameState.InternalCurrentActor != actor || PhaseManager.PhaseType != CPhase.PhaseType.ActionSelection) yield break;
            }
            if (character.LongRest && !character.HasLongRested)
            {
                CAbilityCard lost = Plan(actor, "card_loss", budget => TacticalPlanner.ChooseCardToLose(actor, character.DiscardedAbilityCards));
                if (lost == null)
                {
                    Handoff(actor, "no_cards");
                    yield break;
                }
                Decision(Describe(actor) + " long rests and loses " + CardName(lost) + ".");
                Choreographer.s_Choreographer.readyButton.ClearAlternativeAction();
                GameState.PlayerLongRested(lost, actor);
                Record("rest", actor, "action=rest;cards=1");
                yield break;
            }

            if (TryItem(actor, () => ItemPlanner.TryUseDuringActionSelection(actor))) yield break;

            if (GameState.CurrentActionSelectionSequence == GameState.ActionSelectionSequenceType.Complete)
            {
                IEnumerator finish = FinishTurn(actor);
                while (finish.MoveNext()) yield return finish.Current;
                yield break;
            }

            List<CAbilityCard> pile = actor.IsTakingExtraTurn
                ? character.ExtraTurnCards
                : character.RoundAbilityCards;
            if (pile.Count == 0)
            {
                Handoff(actor, "no_cards");
                yield break;
            }

            TacticalPlanner.PlannedAction planned = Plan(actor, "action", budget => TacticalPlanner.ChooseNextAction(actor, budget));
            if (planned == null || !pile.Contains(planned.Card))
            {
                Handoff(actor, "no_action");
                yield break;
            }
            CAbilityCard card = planned.Card;
            CBaseCard.ActionType action = planned.ActionType;
            PromptStamp prompt = GetPrompt(actor, false);
            FullAbilityCard cardUi;
            double cardDeadline = DecisionTime + UiTimeoutSeconds;
            while (true)
            {
                if (!prompt.Matches(GetPrompt(actor, false))) yield break;
                CardsActionControlller actionController = CardsHandManager.Instance?.cardsActionController;
                FullAbilityCard topCard = actionController == null ? null : ActionTopCard.GetValue(actionController) as FullAbilityCard;
                FullAbilityCard bottomCard = actionController == null ? null : ActionBottomCard.GetValue(actionController) as FullAbilityCard;
                cardUi = topCard != null && topCard.AbilityCard == card ? topCard : bottomCard;
                if (cardUi != null && cardUi.AbilityCard == card && actionController.IsActionAvailable &&
                    !CardsHandManager.Instance.IsFullCardPreviewShowing && cardUi.IsInteractable(action)) break;
                if (DecisionTime >= cardDeadline)
                {
                    Handoff(actor, "timeout");
                    yield break;
                }
                yield return null;
            }
            if (TryItem(actor, () => ItemPlanner.TryUseDuringActionSelection(actor))) yield break;
            Decision(Describe(actor) + " uses " + CardName(card) + " " + TacticalPlanner.Describe(planned) + ".");
            cardUi.OnAbilityClick(action, isProxyAction: false, checkValid: true);
            Record("action_selection", actor, "reason=best_score;card_id=" + card.ID);
        }

        private static IEnumerator FinishLongRest(CPlayerActor actor)
        {
            yield return null;
            if (!actor.CharacterClass.HasLongRested || actor.CharacterClass.LongRest || actor.IsTakingExtraTurn) yield break;
            double deadline = DecisionTime + UiTimeoutSeconds;
            while (Choreographer.s_Choreographer?.readyButton == null)
            {
                if (DecisionTime >= deadline)
                {
                    Handoff(actor, "timeout");
                    yield break;
                }
                yield return null;
            }
            if (GameState.InternalCurrentActor != actor || PhaseManager.PhaseType != CPhase.PhaseType.ActionSelection)
            {
                yield break;
            }

            if (GameState.PendingOnLongRestBonuses.Count == 0)
            {
                if (TryItem(actor, () => ItemPlanner.TryUseDuringActionSelection(actor))) yield break;
                IEnumerator finish = FinishTurn(actor);
                while (finish.MoveNext()) yield return finish.Current;
            }
            else
            {
                Handoff(actor, "unsupported");
            }
        }

        private static IEnumerator FinishTurn(CPlayerActor actor)
        {
            CPhase phase = PhaseManager.CurrentPhase;
            bool requested = EndTurnController.IsPending(actor);
            double deadline = DecisionTime + (requested ? WaitTimeoutSeconds : UiTimeoutSeconds);
            while (ReferenceEquals(phase, PhaseManager.CurrentPhase) && GameState.InternalCurrentActor == actor)
            {
                if (EndTurnController.HasFailed(actor))
                {
                    Handoff(actor, "invalid_state");
                    yield break;
                }
                if (!requested && EndTurnController.TryEndTurn(actor))
                {
                    requested = true;
                    deadline = DecisionTime + WaitTimeoutSeconds;
                    Record("end_turn_submitted", actor, "action=end_turn");
                    Decision(Describe(actor) + " confirms the end of its turn through the ready button.");
                }
                if (DecisionTime >= deadline)
                {
                    if (requested) EndTurnController.MarkFailed(actor);
                    Handoff(actor, "timeout");
                    yield break;
                }
                yield return null;
            }
        }

        private static T Plan<T>(CPlayerActor actor, string action, Func<PlanningBudget, T> choose)
        {
            var watch = Stopwatch.StartNew();
            var budget = new PlanningBudget();
            try { return choose(budget); }
            finally
            {
                watch.Stop();
                WaitClock.ExcludePlanning(watch.Elapsed.TotalSeconds);
                if (DeveloperDiagnostics.Enabled)
                    Record("planner_timing", actor, string.Format(CultureInfo.InvariantCulture,
                        "action={0};elapsed_ms={1};path_queries={2};los_queries={3};candidate_samples={4};" +
                        "cache_hits={5};budget_exhausted={6};time_budget_exceeded={7}", action,
                        watch.Elapsed.TotalMilliseconds, budget.PathQueries, budget.LosQueries,
                        budget.CandidateSamples, budget.CacheHits, budget.BudgetExhausted ? 1 : 0,
                        budget.TimeBudgetExceeded ? 1 : 0));
            }
        }

        private static void HandleMoveMessage(CActorIsSelectingMoveTile_MessageData message)
        {
            CPlayerActor actor = message?.m_ActorSpawningMessage as CPlayerActor;
            CAbilityMove move = message?.m_MoveAbility;
            if (move == null || !IsCurrentAbility(move)) return;
            if (actor != null && TacticalPlanner.IsSupportedMove(move) && IsAutomated(actor))
            {
                Schedule(actor, ContinueMove(actor, move));
            }
            else if (IsAutomated(actor)) Handoff(actor, "unsupported");
        }

        private static IEnumerator ContinueMove(CPlayerActor actor, CAbilityMove move)
        {
            if (GameState.InternalCurrentActor == actor && TacticalPlanner.IsSupportedMove(move) &&
                move.State == CAbilityMove.EMoveState.ActorIsSelectingMoveTile && IsCurrentAbility(move))
            {
                if (Plugin.AutomateItems.Value)
                {
                    int bonus = ItemPlanner.AvailableMoveBonus(actor, move);
                    int pathCost = bonus > 0 ? Plan(actor, "boots", budget => TacticalPlanner.UsefulBootsPathCost(actor, move, bonus, budget)) : 0;
                    if (pathCost > 0 && TryItem(actor, () => ItemPlanner.TryUseForMove(actor, move, pathCost)))
                        yield break;
                }
                CTile destination = Plan(actor, "move", budget => TacticalPlanner.ChooseMoveDestination(actor, move, budget));
                if (destination == null || destination.m_ArrayIndex == actor.ArrayIndex)
                {
                    Decision(Describe(actor) + " has no useful move and skips movement.");
                    ScenarioRuleClient.Pass();
                    Record("action_selection", actor, "action=skip;reason=no_path");
                }
                else
                {
                    Decision(Describe(actor) + " moves to " + destination.m_ArrayIndex + ".");
                    if (ScenarioRuleClient.TileSelected(destination, new List<CTile> { destination }) != 0)
                        Record("move_submitted", actor, "action=move;x=" + destination.m_ArrayIndex.X + ";y=" + destination.m_ArrayIndex.Y);
                    else Handoff(actor, "invalid_state");
                }
            }
            yield break;
        }

        private static void HandleAttackMessage(CActorIsSelectingAttackFocusTargets_MessageData message)
        {
            CPlayerActor actor = message?.m_AttackingActor as CPlayerActor;
            CAbilityAttack attack = message?.m_AttackAbility;
            if (attack == null || !IsCurrentAbility(attack)) return;
            if (actor != null && TacticalPlanner.IsSupportedAttack(attack) && IsAutomated(actor) &&
                (attack.State == CAbilityAttack.EAttackState.SelectAttackFocus ||
                 attack.State == CAbilityAttack.EAttackState.SelectAttackFocusAdditionalTargets))
            {
                if (attack.IsWaitingForSingleTargetItemOrActiveBonus())
                {
                    Handoff(actor, "unsupported");
                    return;
                }
                Schedule(actor, ContinueAttack(actor, attack));
            }
            else if (IsAutomated(actor)) Handoff(actor, "unsupported");
        }

        private static IEnumerator ContinueAttack(CPlayerActor actor, CAbilityAttack attack)
        {
            if (GameState.InternalCurrentActor != actor || !TacticalPlanner.IsSupportedAttack(attack) ||
                !IsCurrentAbility(attack))
            {
                yield break;
            }
            if (attack.IsWaitingForSingleTargetItemOrActiveBonus())
            {
                Handoff(actor, "unsupported");
                yield break;
            }
            if (attack.State == CAbilityAttack.EAttackState.SelectAttackFocusAdditionalTargets)
            {
                Decision(Describe(actor) + " declines optional additional attack targets.");
                ScenarioRuleClient.StepComplete();
                Record("action_selection", actor, "action=skip");
                yield break;
            }
            if (attack.State == CAbilityAttack.EAttackState.SelectAttackFocus)
            {
                CActor intendedTarget = attack.ActorsToTarget.FirstOrDefault(target =>
                    target != null && !target.IsDead && attack.ValidActorsInRange.Contains(target)) ??
                    Plan(actor, "attack", budget => TacticalPlanner.RankAttackTargets(actor, attack, attack.ValidActorsInRange, budget)).FirstOrDefault() ??
                    attack.ValidActorsInRange.FirstOrDefault(target => target != null && !target.IsDead);
                if (TryItem(actor, () => ItemPlanner.TryUseForAttack(actor, attack, intendedTarget))) yield break;
                lock (StateLock)
                {
                    CommittedAttacks.Add(attack);
                }
                if (ScenarioRuleClient.StepComplete() == 0)
                {
                    ConsumeCommittedAttack(attack);
                    Handoff(actor, "invalid_state");
                }
                else Record("attack_submitted", actor, "action=attack;targets=" + (intendedTarget == null ? "0" : "1"));
            }
            yield break;
        }

        private static void HandleTargetingMessage(CActorIsSelectingTargetingFocus_MessageData message)
        {
            CAbilityHeal heal = message?.m_TargetingAbility as CAbilityHeal;
            CPlayerActor actor = message?.m_ActorSpawningMessage as CPlayerActor;
            if (message?.m_TargetingAbility == null || !IsCurrentAbility(message.m_TargetingAbility)) return;
            if (actor != null && message.m_IsPositive && IsAutomated(actor) &&
                message.m_TargetingAbility is CAbilityRecoverLostCards recovery && recovery.CanReceiveTileSelection())
            {
                Schedule(actor, ContinueRecovery(actor, recovery));
                return;
            }
            if (actor != null && message.m_IsPositive && heal != null &&
                IsAutomated(actor) && heal.CanReceiveTileSelection())
            {
                Schedule(actor, ContinueHeal(actor, heal));
            }
            else if (IsAutomated(actor)) Handoff(actor, "unsupported");
        }

        private static IEnumerator ContinueHeal(CPlayerActor actor, CAbilityHeal heal)
        {
            if (GameState.InternalCurrentActor != actor ||
                !heal.CanReceiveTileSelection() || !IsCurrentAbility(heal))
            {
                yield break;
            }
            if (TryItem(actor, () => ItemPlanner.TryConfirmHealingItem(actor, heal)))
            {
                Record("heal_submitted", actor, "action=heal;targets=1");
                yield break;
            }
            if (!TacticalPlanner.IsSupportedHeal(heal) || heal.IsWaitingForSingleTargetItemOrActiveBonus())
            {
                Handoff(actor, "unsupported");
                yield break;
            }

            List<CActor> targets = Plan(actor, "heal", budget => TacticalPlanner.RankHealTargets(actor, heal,
                    heal.ValidActorsInRange.Where(target => !heal.ActorsToTarget.Contains(target)), budget))
                .Take(Math.Max(0, heal.NumberTargets - heal.ActorsToTarget.Count))
                .ToList();

            foreach (CActor target in targets)
            {
                if (!IsCurrentAbility(heal) || !heal.CanReceiveTileSelection() ||
                    heal.IsWaitingForSingleTargetItemOrActiveBonus()) yield break;
                // TileSelected toggles selection. A manual/game-selected target must not be toggled off.
                if (heal.ActorsToTarget.Contains(target) || target.IsDead ||
                    !heal.ValidActorsInRange.Contains(target) ||
                    Plan(actor, "heal", budget => TacticalPlanner.ScoreHealTarget(target, heal)) <= 0f) continue;
                if (heal.ActorsToTarget.Count >= heal.NumberTargets) break;
                uint messageId = ScenarioRuleClient.TileSelected(
                    ScenarioManager.Tiles[target.ArrayIndex.X, target.ArrayIndex.Y], null);
                if (messageId == 0)
                {
                    Handoff(actor, "invalid_state");
                    yield break;
                }
                while (messageId > ScenarioRuleClient.s_SRLLastProcessedMessageID)
                {
                    yield return null;
                }
                if (GameState.InternalCurrentActor != actor || !IsAutomated(actor) ||
                    !heal.CanReceiveTileSelection() || !IsCurrentAbility(heal))
                {
                    yield break;
                }
            }

            CActor usefulSelected = Plan(actor, "heal", budget => TacticalPlanner.RankHealTargets(actor, heal, heal.ActorsToTarget, budget)).FirstOrDefault();
            if (usefulSelected != null && heal.EnoughTargetsSelected() &&
                !heal.IsWaitingForSingleTargetItemOrActiveBonus())
            {
                Decision(Describe(actor) + " heals " +
                    string.Join(", ", heal.ActorsToTarget.Select(target => target.Class.ID).ToArray()) + ".");
                if (ScenarioRuleClient.StepComplete() != 0)
                    Record("heal_submitted", actor, "action=heal;targets=" + heal.ActorsToTarget.Count);
                else Handoff(actor, "invalid_state");
            }
            else if (usefulSelected == null && heal.CanSkip)
            {
                Decision(Describe(actor) + " has no useful heal target and skips healing.");
                ScenarioRuleClient.Pass();
                Record("action_selection", actor, "action=skip;reason=no_target");
            }
            else
            {
                Handoff(actor, "unsupported");
            }
        }

        private static IEnumerator ContinueRecovery(CPlayerActor actor, CAbilityRecoverLostCards recovery)
        {
            if (GameState.InternalCurrentActor != actor || !IsCurrentAbility(recovery) ||
                !recovery.CanReceiveTileSelection()) yield break;
            if (!RecoveryPlanner.IsSupported(recovery) || recovery.TargetingActor != actor ||
                actor.Tokens.HasKey(CCondition.ENegativeCondition.Stun) ||
                actor.Tokens.HasKey(CCondition.ENegativeCondition.Sleep) ||
                recovery.IsWaitingForSingleTargetItemOrActiveBonus())
            {
                Handoff(actor, "unsupported");
                yield break;
            }
            int recoverable = RecoveryPlanner.RecoverableCount(actor, recovery);
            if (recoverable == 0)
            {
                if (recovery.CanSkip)
                {
                    ScenarioRuleClient.Pass();
                    Record("action_selection", actor, "action=skip;reason=no_recoverable_cards");
                }
                else Handoff(actor, "no_recoverable_cards");
                yield break;
            }
            // Self is preselected by the targeting state machine. Selecting its tile again would
            // deselect it. Normal confirmation owns both recovery and the source card's final pile.
            if (recovery.ActorsToTarget.Count != 1 || recovery.ActorsToTarget[0] != actor ||
                !recovery.ValidActorsInRange.Contains(actor) || !recovery.EnoughTargetsSelected())
            {
                Handoff(actor, "invalid_state");
                yield break;
            }
            if (ScenarioRuleClient.StepComplete() == 0) Handoff(actor, "invalid_state");
            else
            {
                TacticalPlanner.InvalidatePlan(actor);
                Record("recovery_submitted", actor, "action=recover;cards=" + recoverable);
                Decision(Describe(actor) + " confirms recovery of " + recoverable + " lost cards.");
            }
        }

        private static bool IsCurrentAbility(CAbility ability)
        {
            if (!(PhaseManager.Phase is CPhaseAction actionPhase) || actionPhase.CurrentPhaseAbility == null)
            {
                return false;
            }

            CAbility current = actionPhase.CurrentPhaseAbility.m_Ability;
            return current == ability || (current is CAbilityMerged merged && merged.ActiveAbility == ability);
        }

        private static void HandleDamageMessage(CPlayerSelectingToAvoidDamageOrNot_MessageData message)
        {
            CPlayerActor actor = message?.m_ActorToShowCardsFor;
            if (actor == null)
            {
                return;
            }
            lock (StateLock)
            {
                if (DamageOperations.TryGetValue(actor.ActorGuid, out object operation) &&
                    ReferenceEquals(operation, GameState.CurrentDamageData) &&
                    DamagePrompts.TryGetValue(actor.ActorGuid, out CPlayerSelectingToAvoidDamageOrNot_MessageData existing))
                    message = existing;
                else
                {
                    DamagePrompts[actor.ActorGuid] = message;
                    DamageOperations[actor.ActorGuid] = GameState.CurrentDamageData;
                }
            }

            if (!Plugin.AutomateDamage.Value || !IsAutomated(actor))
            {
                return;
            }
            Schedule(actor, ResolveDamage(message), waitForQueue: false);
        }

        private static IEnumerator ResolveDamage(CPlayerSelectingToAvoidDamageOrNot_MessageData message)
        {
            yield return null;
            CPlayerActor owner = message.m_ActorToShowCardsFor;
            double deadline = DecisionTime + UiTimeoutSeconds;
            while (!Singleton<TakeDamagePanel>.IsInitialized ||
                !Singleton<TakeDamagePanel>.Instance.IsTakingDamage(message.m_ActorBeingAttacked))
            {
                if (!Plugin.AutomateDamage.Value || !IsCurrentDamagePrompt(owner, message) ||
                    GetPrompt(owner, true) == null) yield break;
                if (DecisionTime >= deadline)
                {
                    Handoff(owner, "timeout", true);
                    yield break;
                }
                yield return null;
            }
            GameState.DamageData damage = GameState.CurrentDamageData;
            if (!Plugin.AutomateDamage.Value || !GameState.WaitingForPlayerToSelectDamageResponse || !IsAutomated(owner) ||
                damage == null || damage.ActorDamaged != message.m_ActorBeingAttacked ||
                !IsCurrentDamagePrompt(owner, message) || GetPrompt(owner, true) == null)
            {
                yield break;
            }

            // GameState.ActorBeenDamaged calls actor.Damaged before opening this response prompt.
            bool lethal = message.m_ActorBeingAttacked.Health <= 0;
            CCharacterClass character = owner.CharacterClass;
            if (lethal && Plugin.PreventLethalDamage.Value && character.HandAbilityCards.Count > 0)
            {
                CAbilityCard card = Plan(owner, "card_loss", budget => TacticalPlanner.ChooseCardToLose(owner, character.HandAbilityCards));
                if (card == null)
                {
                    Handoff(owner, "no_cards", true);
                    yield break;
                }
                GameState.Lose1HandCardToAvoidAttack(owner, card);
                GameState.PlayerAvoidingDamage(GameState.EAvoidDamageOption.Lose1HandCard);
                ClearCompletedDamage(owner, message);
                Singleton<TakeDamagePanel>.Instance.ResetAndHide(true);
                Decision(Describe(owner) + " loses " + CardName(card) + " to prevent lethal damage.");
                Record("damage_response", owner, "action=damage;reason=lethal;cards=1");
                yield break;
            }

            if (lethal && Plugin.PreventLethalDamage.Value && character.DiscardedAbilityCards.Count >= 2)
            {
                List<CAbilityCard> cards = Plan(owner, "card_loss", budget => TacticalPlanner.ChooseCardsToLose(owner,
                    character.DiscardedAbilityCards, 2));
                if (cards.Count < 2)
                {
                    Handoff(owner, "no_cards", true);
                    yield break;
                }
                GameState.Lose2DiscardCardsToAvoidAttack(owner, cards[0], cards[1]);
                GameState.PlayerAvoidingDamage(GameState.EAvoidDamageOption.Lose2DiscardCards);
                ClearCompletedDamage(owner, message);
                Singleton<TakeDamagePanel>.Instance.ResetAndHide(true);
                Decision(Describe(owner) + " loses two discarded cards to prevent lethal damage.");
                Record("damage_response", owner, "action=damage;reason=lethal;cards=2");
                yield break;
            }

            Decision(Describe(owner) + " accepts " + message.m_ModifiedStrength + " damage.");
            Singleton<TakeDamagePanel>.Instance.TakeDamage();
            Record("damage_response", owner, "action=damage;damage=" + message.m_ModifiedStrength +
                ";reason=" + (lethal ? "lethal" : "nonlethal"));
            ClearCompletedDamage(owner, message);
            if (GameState.WaitingForPlayerToSelectDamageResponse)
            {
                Handoff(owner, "unsupported", true);
            }
        }

        private static void ClearCompletedDamage(CPlayerActor owner,
            CPlayerSelectingToAvoidDamageOrNot_MessageData message)
        {
            lock (StateLock)
            {
                if (IsCurrentDamagePrompt(owner, message) && GetPrompt(owner, true) == null)
                {
                    DamagePrompts.Remove(owner.ActorGuid);
                    DamageOperations.Remove(owner.ActorGuid);
                    SettledPrompts.Remove(Channel(owner, true));
                }
            }
        }

        private static bool IsCurrentDamagePrompt(CPlayerActor owner,
            CPlayerSelectingToAvoidDamageOrNot_MessageData message)
        {
            lock (StateLock)
            {
                return DamagePrompts.TryGetValue(owner.ActorGuid, out CPlayerSelectingToAvoidDamageOrNot_MessageData current) &&
                    ReferenceEquals(current, message);
            }
        }

        private static string Describe(CPlayerActor actor)
        {
            if (!string.IsNullOrEmpty(actor.CharacterName))
            {
                return actor.CharacterName + " [" + actor.CharacterClass.CharacterID + "]";
            }
            return actor.CharacterClass.CharacterID;
        }

        private static void Record(string kind, CActor actor, string detail)
        {
            if (!FFSNetwork.IsOnline) DeveloperDiagnostics.Record(kind, actor, detail);
        }

        private static string CardName(CAbilityCard card)
        {
            return card == null ? "no card" : card.StrictName + " (" + card.Initiative + ")";
        }

        internal static void Decision(string message)
        {
            if (Plugin.LogDecisions.Value)
            {
                Plugin.Log.LogInfo("[Decision] " + message);
            }
        }
    }
}
