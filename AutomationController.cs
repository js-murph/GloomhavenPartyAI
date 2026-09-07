using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ScenarioRuleLibrary;
using UnityEngine;

namespace GloomhavenPartyAI
{
    internal static class AutomationController
    {
        private readonly struct DecisionKey : IEquatable<DecisionKey>
        {
            internal readonly object Token;
            internal readonly string ActorGuid;
            internal readonly int ActorEpoch;

            internal DecisionKey(object token, string actorGuid, int actorEpoch)
            {
                Token = token;
                ActorGuid = actorGuid;
                ActorEpoch = actorEpoch;
            }

            public bool Equals(DecisionKey other)
            {
                return ReferenceEquals(Token, other.Token) && ActorGuid == other.ActorGuid &&
                    ActorEpoch == other.ActorEpoch;
            }

            public override bool Equals(object obj)
            {
                return obj is DecisionKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                int hash = (RuntimeHelpers.GetHashCode(Token) * 397) ^ (ActorGuid?.GetHashCode() ?? 0);
                return (hash * 397) ^ ActorEpoch;
            }
        }

        private static Plugin _plugin;
        private static string _humanActorGuid;
        private static readonly object StateLock = new object();
        private static readonly HashSet<DecisionKey> PendingDecisions = new HashSet<DecisionKey>();
        private static readonly HashSet<string> ForcedManualActors = new HashSet<string>();
        private static readonly HashSet<string> ForcedAutomatedActors = new HashSet<string>();
        private static readonly Dictionary<string, int> ActorEpochs = new Dictionary<string, int>();
        private static readonly Dictionary<string, CPlayerSelectingToAvoidDamageOrNot_MessageData> DamagePrompts =
            new Dictionary<string, CPlayerSelectingToAvoidDamageOrNot_MessageData>();
        private static readonly HashSet<CAbilityAttack> CommittedAttacks = new HashSet<CAbilityAttack>();
        private static readonly FieldInfo ActionTopCard = AccessTools.Field(typeof(CardsActionControlller), "topCard");
        private static readonly FieldInfo ActionBottomCard = AccessTools.Field(typeof(CardsActionControlller), "bottomCard");
        private static bool _onlineWarningLogged;
        private static int _generation;

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
                ForcedManualActors.Clear();
                ForcedAutomatedActors.Clear();
                ActorEpochs.Clear();
                DamagePrompts.Clear();
                CommittedAttacks.Clear();
                _onlineWarningLogged = false;
                _generation++;
            }
            TacticalPlanner.Reset();
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
                enabled = !currentlyEnabled;
            }

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
            if (!CanAutomate() || message == null)
            {
                return;
            }

            switch (message.m_Type)
            {
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
                            Schedule(message, actor, SelectRoundCards(actor));
                        }
                    }
                    break;
                case CMessageData.MessageType.ActionSelection:
                    if (IsAutomated(message.m_ActorSpawningMessage))
                    {
                        CPlayerActor actor = (CPlayerActor)message.m_ActorSpawningMessage;
                        Schedule(message, actor, SelectAction(actor));
                    }
                    break;
                case CMessageData.MessageType.PlayerLongRested:
                    if (IsAutomated(message.m_ActorSpawningMessage))
                    {
                        CPlayerActor actor = (CPlayerActor)message.m_ActorSpawningMessage;
                        Schedule(message, actor, FinishLongRest(actor));
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
                    HandleDamageMessage(message as CPlayerSelectingToAvoidDamageOrNot_MessageData);
                    break;
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

        private static void Schedule(object token, CPlayerActor actor, IEnumerator routine,
            bool waitForQueue = true)
        {
            DecisionKey key;
            int generation;
            int actorEpoch;
            lock (StateLock)
            {
                actorEpoch = GetActorEpochByState(actor.ActorGuid);
                key = new DecisionKey(token, actor.ActorGuid, actorEpoch);
                if (!PendingDecisions.Add(key))
                {
                    return;
                }
                generation = _generation;
            }
            _plugin.StartCoroutine(RunScheduled(key, generation, actorEpoch, actor, routine, waitForQueue));
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
                Schedule(damageMessage, actor, ResolveDamage(damageMessage), waitForQueue: false);
                return;
            }

            if (PhaseManager.PhaseType == CPhase.PhaseType.SelectAbilityCardsOrLongRest)
            {
                Schedule(PhaseManager.CurrentPhase, actor, SelectRoundCards(actor));
                return;
            }

            if (PhaseManager.PhaseType == CPhase.PhaseType.ActionSelection && GameState.InternalCurrentActor == actor)
            {
                if (actor.CharacterClass.HasLongRested && !actor.CharacterClass.LongRest)
                {
                    Schedule(PhaseManager.CurrentPhase, actor, FinishLongRest(actor));
                }
                else
                {
                    Schedule(PhaseManager.CurrentPhase, actor, SelectAction(actor));
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
                    Schedule(move, actor, ContinueMove(actor, move));
                    return;
                }
                if (ability is CAbilityAttack attack && TacticalPlanner.IsSupportedAttack(attack) &&
                    (attack.State == CAbilityAttack.EAttackState.SelectAttackFocus ||
                     attack.State == CAbilityAttack.EAttackState.SelectAttackFocusAdditionalTargets))
                {
                    Schedule(attack, actor, ContinueAttack(actor, attack));
                    return;
                }
                if (ability is CAbilityHeal heal && TacticalPlanner.IsSupportedHeal(heal) &&
                    heal.CanReceiveTileSelection())
                {
                    Schedule(heal, actor, ContinueHeal(actor, heal));
                    return;
                }
            }
        }

        private static IEnumerator RunScheduled(DecisionKey key, int generation, int actorEpoch,
            CPlayerActor actor, IEnumerator routine, bool waitForQueue)
        {
            yield return new WaitForSecondsRealtime(Math.Max(0f, Plugin.DecisionDelay.Value));
            if (!DecisionIsCurrent(key, generation, actorEpoch, actor))
            {
                RemovePending(key);
                yield break;
            }

            while (waitForQueue && ScenarioRuleClient.IsProcessingOrMessagesQueued)
            {
                yield return null;
                if (!DecisionIsCurrent(key, generation, actorEpoch, actor))
                {
                    RemovePending(key);
                    yield break;
                }
            }

            while (true)
            {
                if (!DecisionIsCurrent(key, generation, actorEpoch, actor))
                {
                    break;
                }

                bool hasNext;
                object current = null;
                try
                {
                    hasNext = routine.MoveNext();
                    if (hasNext)
                    {
                        current = routine.Current;
                    }
                }
                catch (Exception exception)
                {
                    Plugin.Log.LogError("Party AI decision failed: " + exception);
                    break;
                }

                if (!hasNext)
                {
                    break;
                }
                yield return current;
            }
            RemovePending(key);
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
            return IsAutomated(actor);
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
            if (character.RoundAbilityCards.Count >= 2 || character.LongRest)
            {
                yield break;
            }

            int cardsNeeded = 2 - character.RoundAbilityCards.Count;
            if (character.HandAbilityCards.Count < cardsNeeded)
            {
                if (character.RoundAbilityCards.Count == 0 && character.DiscardedAbilityCards.Count >= 2)
                {
                    character.LongRest = true;
                    actor.IsLongRestSelected = true;
                    Decision(Describe(actor) + " selected a long rest.");
                }
                else
                {
                    Plugin.Log.LogWarning(Describe(actor) + " cannot select two cards or long rest; leaving the game to resolve exhaustion.");
                }
                RefreshCardSelectionReady();
                yield break;
            }

            List<CAbilityCard> cards = TacticalPlanner.ChooseRoundCards(actor, cardsNeeded);
            if (cards.Count != cardsNeeded)
            {
                Plugin.Log.LogWarning(Describe(actor) +
                    " tactical planner could not select a complete card pair; leaving card selection manual.");
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
            if (character.RoundAbilityCards.Count != 2)
            {
                Plugin.Log.LogWarning(Describe(actor) + " does not have exactly two selected cards; leaving card selection manual.");
                yield break;
            }
            List<CAbilityCard> roundCards = character.RoundAbilityCards.ToList();
            CAbilityCard initiativeCard = TacticalPlanner.ChooseInitiativeCard(actor, roundCards);
            CAbilityCard subInitiativeCard = roundCards.First(card => card != initiativeCard);
            character.SetInitiativeAbilityCard(initiativeCard);
            character.SetSubInitiativeAbilityCard(subInitiativeCard);
            actor.IsLongRestSelected = false;
            Decision(Describe(actor) + " selected " + CardName(initiativeCard) +
                " (initiative) and " + CardName(subInitiativeCard) + " using tactical card scoring.");

            RefreshCardSelectionReady();
        }

        private static void RefreshCardSelectionReady()
        {
            if (InitiativeTrack.Instance != null && CardsHandManager.Instance?.CurrentHand != null)
            {
                InitiativeTrack.Instance.CheckRoundAbilityCardsOrLongRestSelected();
            }
        }

        private static IEnumerator SelectAction(CPlayerActor actor)
        {
            if (GameState.InternalCurrentActor != actor || PhaseManager.PhaseType != CPhase.PhaseType.ActionSelection)
            {
                yield break;
            }

            CCharacterClass character = actor.CharacterClass;
            if (character.LongRest && !character.HasLongRested)
            {
                CAbilityCard lost = TacticalPlanner.ChooseCardToLose(actor, character.DiscardedAbilityCards);
                if (lost == null)
                {
                    Plugin.Log.LogWarning(Describe(actor) + " has no card to lose for its long rest; leaving the choice manual.");
                    yield break;
                }
                Decision(Describe(actor) + " long rests and loses " + CardName(lost) + ".");
                Choreographer.s_Choreographer.readyButton.ClearAlternativeAction();
                GameState.PlayerLongRested(lost, actor);
                yield break;
            }

            if (GameState.CurrentActionSelectionSequence == GameState.ActionSelectionSequenceType.Complete)
            {
                Decision(Describe(actor) + " ends its turn.");
                Choreographer.s_Choreographer.readyButton.ClearAlternativeAction();
                Choreographer.s_Choreographer.Pass();
                yield break;
            }

            List<CAbilityCard> pile = actor.IsTakingExtraTurn
                ? character.ExtraTurnCards
                : character.RoundAbilityCards;
            if (pile.Count == 0)
            {
                Plugin.Log.LogWarning(Describe(actor) + " has no round card available; leaving action selection manual.");
                yield break;
            }

            TacticalPlanner.PlannedAction planned = TacticalPlanner.ChooseNextAction(actor);
            if (planned == null || !pile.Contains(planned.Card))
            {
                Plugin.Log.LogWarning(Describe(actor) +
                    " has no safe tactical card action; leaving action selection manual.");
                yield break;
            }
            CAbilityCard card = planned.Card;
            CBaseCard.ActionType action = planned.ActionType;
            while (CardsHandManager.Instance != null && CardsHandManager.Instance.IsFullCardPreviewShowing)
            {
                yield return null;
            }
            if (GameState.InternalCurrentActor != actor ||
                PhaseManager.PhaseType != CPhase.PhaseType.ActionSelection)
            {
                yield break;
            }
            CardsActionControlller actionController = CardsHandManager.Instance?.cardsActionController;
            FullAbilityCard topCard = actionController == null
                ? null
                : ActionTopCard.GetValue(actionController) as FullAbilityCard;
            FullAbilityCard bottomCard = actionController == null
                ? null
                : ActionBottomCard.GetValue(actionController) as FullAbilityCard;
            FullAbilityCard cardUi = topCard != null && topCard.AbilityCard == card ? topCard : bottomCard;
            if (cardUi == null || cardUi.AbilityCard != card || !actionController.IsActionAvailable)
            {
                Plugin.Log.LogWarning(Describe(actor) +
                    " card UI is not ready; leaving action selection manual.");
                yield break;
            }
            if (!cardUi.IsInteractable(action))
            {
                Plugin.Log.LogWarning(Describe(actor) + " planned " + TacticalPlanner.Describe(planned) +
                    " is not currently interactable; leaving action selection manual.");
                yield break;
            }
            Decision(Describe(actor) + " uses " + CardName(card) + " " + TacticalPlanner.Describe(planned) + ".");
            cardUi.OnAbilityClick(action, isProxyAction: false, checkValid: true);
        }

        private static IEnumerator FinishLongRest(CPlayerActor actor)
        {
            yield return null;
            if (GameState.InternalCurrentActor != actor || PhaseManager.PhaseType != CPhase.PhaseType.ActionSelection)
            {
                yield break;
            }

            if (GameState.PendingOnLongRestBonuses.Count == 0)
            {
                Choreographer.s_Choreographer.readyButton.ClearAlternativeAction();
                Choreographer.s_Choreographer.Pass();
            }
            else
            {
                Plugin.Log.LogWarning(Describe(actor) + " has an unsupported long-rest bonus choice; leaving it manual.");
            }
        }

        private static void HandleMoveMessage(CActorIsSelectingMoveTile_MessageData message)
        {
            CPlayerActor actor = message?.m_ActorSpawningMessage as CPlayerActor;
            CAbilityMove move = message?.m_MoveAbility;
            if (actor != null && TacticalPlanner.IsSupportedMove(move) && IsAutomated(actor))
            {
                Schedule(message, actor, ContinueMove(actor, move));
            }
        }

        private static IEnumerator ContinueMove(CPlayerActor actor, CAbilityMove move)
        {
            if (GameState.InternalCurrentActor == actor && TacticalPlanner.IsSupportedMove(move) &&
                move.State == CAbilityMove.EMoveState.ActorIsSelectingMoveTile && IsCurrentAbility(move))
            {
                CTile destination = TacticalPlanner.ChooseMoveDestination(actor, move);
                if (destination == null || destination.m_ArrayIndex == actor.ArrayIndex)
                {
                    Decision(Describe(actor) + " has no useful move and skips movement.");
                    ScenarioRuleClient.Pass();
                }
                else
                {
                    Decision(Describe(actor) + " moves to " + destination.m_ArrayIndex + ".");
                    ScenarioRuleClient.TileSelected(destination, new List<CTile> { destination });
                }
            }
            yield break;
        }

        private static void HandleAttackMessage(CActorIsSelectingAttackFocusTargets_MessageData message)
        {
            CPlayerActor actor = message?.m_AttackingActor as CPlayerActor;
            CAbilityAttack attack = message?.m_AttackAbility;
            if (actor != null && TacticalPlanner.IsSupportedAttack(attack) && IsAutomated(actor) &&
                (attack.State == CAbilityAttack.EAttackState.SelectAttackFocus ||
                 attack.State == CAbilityAttack.EAttackState.SelectAttackFocusAdditionalTargets))
            {
                if (attack.IsWaitingForSingleTargetItemOrActiveBonus())
                {
                    Plugin.Log.LogWarning(Describe(actor) +
                        " has a mandatory single-target attack choice; leaving target selection manual.");
                    return;
                }
                Schedule(message, actor, ContinueAttack(actor, attack));
            }
        }

        private static IEnumerator ContinueAttack(CPlayerActor actor, CAbilityAttack attack)
        {
            if (GameState.InternalCurrentActor != actor || !TacticalPlanner.IsSupportedAttack(attack) ||
                !IsCurrentAbility(attack) ||
                attack.IsWaitingForSingleTargetItemOrActiveBonus())
            {
                yield break;
            }
            if (attack.State == CAbilityAttack.EAttackState.SelectAttackFocusAdditionalTargets)
            {
                Decision(Describe(actor) + " declines optional additional attack targets.");
                ScenarioRuleClient.StepComplete();
                yield break;
            }
            if (attack.State == CAbilityAttack.EAttackState.SelectAttackFocus)
            {
                lock (StateLock)
                {
                    CommittedAttacks.Add(attack);
                }
                if (ScenarioRuleClient.StepComplete() == 0)
                {
                    ConsumeCommittedAttack(attack);
                }
            }
            yield break;
        }

        private static void HandleTargetingMessage(CActorIsSelectingTargetingFocus_MessageData message)
        {
            CAbilityHeal heal = message?.m_TargetingAbility as CAbilityHeal;
            CPlayerActor actor = message?.m_ActorSpawningMessage as CPlayerActor;
            if (actor != null && message.m_IsPositive && TacticalPlanner.IsSupportedHeal(heal) &&
                IsAutomated(actor) && heal.CanReceiveTileSelection())
            {
                Schedule(message, actor, ContinueHeal(actor, heal));
            }
        }

        private static IEnumerator ContinueHeal(CPlayerActor actor, CAbilityHeal heal)
        {
            if (GameState.InternalCurrentActor != actor || !TacticalPlanner.IsSupportedHeal(heal) ||
                !heal.CanReceiveTileSelection() || !IsCurrentAbility(heal) ||
                heal.IsWaitingForSingleTargetItemOrActiveBonus())
            {
                yield break;
            }

            List<CActor> targets = heal.ValidActorsInRange
                .Where(target => target != null && !target.IsDead && !heal.ActorsToTarget.Contains(target) &&
                    TacticalPlanner.ScoreHealTarget(target, heal) > 0f)
                .OrderByDescending(target => TacticalPlanner.ScoreHealTarget(target, heal))
                .ThenByDescending(target => target is CPlayerActor)
                .ThenBy(target => target.MaxHealth == 0 ? 1f : (float)target.Health / target.MaxHealth)
                .ThenBy(target => SharedAbilityTargeting.GetDistanceBetweenActorsInHexes(target, actor))
                .ThenBy(target => target.Initiative())
                .ThenBy(target => target.ID)
                .Take(Math.Max(0, heal.NumberTargets - heal.ActorsToTarget.Count))
                .ToList();

            foreach (CActor target in targets)
            {
                uint messageId = ScenarioRuleClient.TileSelected(
                    ScenarioManager.Tiles[target.ArrayIndex.X, target.ArrayIndex.Y], null);
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

            CActor usefulSelected = heal.ActorsToTarget
                .Where(target => TacticalPlanner.ScoreHealTarget(target, heal) > 0f)
                .OrderByDescending(target => TacticalPlanner.ScoreHealTarget(target, heal))
                .FirstOrDefault();
            if (usefulSelected != null && heal.EnoughTargetsSelected() &&
                !heal.IsWaitingForSingleTargetItemOrActiveBonus())
            {
                Decision(Describe(actor) + " heals " +
                    string.Join(", ", heal.ActorsToTarget.Select(target => target.Class.ID).ToArray()) + ".");
                ScenarioRuleClient.StepComplete();
            }
            else if (usefulSelected == null && heal.CanSkip)
            {
                Decision(Describe(actor) + " has no useful heal target and skips healing.");
                ScenarioRuleClient.Pass();
            }
            else
            {
                Plugin.Log.LogWarning(Describe(actor) +
                    " has an unresolved mandatory heal choice; leaving target selection manual.");
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
                DamagePrompts[actor.ActorGuid] = message;
            }

            if (!Plugin.AutomateDamage.Value || !IsAutomated(actor))
            {
                return;
            }
            Schedule(message, actor, ResolveDamage(message), waitForQueue: false);
        }

        private static IEnumerator ResolveDamage(CPlayerSelectingToAvoidDamageOrNot_MessageData message)
        {
            yield return null;
            CPlayerActor owner = message.m_ActorToShowCardsFor;
            GameState.DamageData damage = GameState.CurrentDamageData;
            if (!GameState.WaitingForPlayerToSelectDamageResponse || !IsAutomated(owner) ||
                damage == null || damage.ActorDamaged != message.m_ActorBeingAttacked ||
                !IsCurrentDamagePrompt(owner, message))
            {
                yield break;
            }

            bool lethal = message.m_ActorBeingAttacked.Health <= 0;
            CCharacterClass character = owner.CharacterClass;
            if (lethal && Plugin.PreventLethalDamage.Value && character.HandAbilityCards.Count > 0)
            {
                CAbilityCard card = TacticalPlanner.ChooseCardToLose(owner, character.HandAbilityCards);
                GameState.Lose1HandCardToAvoidAttack(owner, card);
                GameState.PlayerAvoidingDamage(GameState.EAvoidDamageOption.Lose1HandCard);
                Singleton<TakeDamagePanel>.Instance.ResetAndHide(true);
                Decision(Describe(owner) + " loses " + CardName(card) + " to prevent lethal damage.");
                yield break;
            }

            if (lethal && Plugin.PreventLethalDamage.Value && character.DiscardedAbilityCards.Count >= 2)
            {
                List<CAbilityCard> cards = TacticalPlanner.ChooseCardsToLose(owner,
                    character.DiscardedAbilityCards, 2);
                GameState.Lose2DiscardCardsToAvoidAttack(owner, cards[0], cards[1]);
                GameState.PlayerAvoidingDamage(GameState.EAvoidDamageOption.Lose2DiscardCards);
                Singleton<TakeDamagePanel>.Instance.ResetAndHide(true);
                Decision(Describe(owner) + " loses two discarded cards to prevent lethal damage.");
                yield break;
            }

            Decision(Describe(owner) + " accepts " + message.m_ModifiedStrength + " damage.");
            Singleton<TakeDamagePanel>.Instance.TakeDamage();
            if (GameState.WaitingForPlayerToSelectDamageResponse)
            {
                Plugin.Log.LogWarning("Damage automation could not satisfy a mandatory item or active-bonus choice; leaving the prompt manual.");
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
