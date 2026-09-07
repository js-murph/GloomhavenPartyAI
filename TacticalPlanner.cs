using System;
using System.Collections.Generic;
using System.Linq;
using AStar;
using ScenarioRuleLibrary;

namespace GloomhavenPartyAI
{
    internal static class TacticalPlanner
    {
        internal sealed class PlannedAction
        {
            internal CAbilityCard Card;
            internal CBaseCard.ActionType ActionType;
            internal CAction Action;
            internal float Score;

            internal CAbilityAttack FirstAttack => Action?.Abilities.OfType<CAbilityAttack>().FirstOrDefault();
            internal CAbilityMove FirstMove => Action?.Abilities.OfType<CAbilityMove>().FirstOrDefault();
            internal CAbilityHeal FirstHeal => Action?.Abilities.OfType<CAbilityHeal>().FirstOrDefault();
            internal bool IsTop => ActionType == CBaseCard.ActionType.TopAction ||
                ActionType == CBaseCard.ActionType.DefaultAttackAction;
        }

        private sealed class TurnPlan
        {
            internal CAbilityCard FirstCard;
            internal PlannedAction Followup;
        }

        private static readonly object PlanLock = new object();
        private static readonly Dictionary<string, TurnPlan> TurnPlans = new Dictionary<string, TurnPlan>();

        internal static void Reset()
        {
            lock (PlanLock)
            {
                TurnPlans.Clear();
            }
        }

        internal static List<CAbilityCard> ChooseRoundCards(CPlayerActor actor, int cardsNeeded)
        {
            List<CAbilityCard> hand = actor.CharacterClass.HandAbilityCards.ToList();
            List<CAbilityCard> selected = actor.CharacterClass.RoundAbilityCards.ToList();
            if (cardsNeeded <= 0 || hand.Count < cardsNeeded)
            {
                return new List<CAbilityCard>();
            }

            if (selected.Count == 1 && cardsNeeded == 1)
            {
                CAbilityCard partner = hand
                    .OrderByDescending(card => ScorePair(actor, selected[0], card))
                    .ThenBy(card => card.Initiative)
                    .ThenBy(card => card.ID)
                    .ThenBy(card => card.CardInstanceID)
                    .First();
                return new List<CAbilityCard> { partner };
            }

            CAbilityCard bestFirst = null;
            CAbilityCard bestSecond = null;
            float bestScore = float.MinValue;
            for (int firstIndex = 0; firstIndex < hand.Count - 1; firstIndex++)
            {
                for (int secondIndex = firstIndex + 1; secondIndex < hand.Count; secondIndex++)
                {
                    CAbilityCard first = hand[firstIndex];
                    CAbilityCard second = hand[secondIndex];
                    float score = ScorePair(actor, first, second);
                    if (score > bestScore || Math.Abs(score - bestScore) < 0.001f &&
                        ComparePair(first, second, bestFirst, bestSecond) < 0)
                    {
                        bestScore = score;
                        bestFirst = first;
                        bestSecond = second;
                    }
                }
            }

            return bestFirst == null
                ? hand.OrderBy(card => card.Initiative).Take(cardsNeeded).ToList()
                : new List<CAbilityCard> { bestFirst, bestSecond };
        }

        internal static CAbilityCard ChooseInitiativeCard(CPlayerActor actor, List<CAbilityCard> cards)
        {
            if (cards == null || cards.Count == 0)
            {
                return null;
            }

            int nearestEnemy = NearestEnemyDistance(actor);
            bool underPressure = nearestEnemy <= 3 || actor.Health * 2 <= actor.MaxHealth;
            return underPressure
                ? cards.OrderBy(card => card.Initiative).ThenBy(card => card.ID).First()
                : cards.OrderByDescending(card => card.Initiative).ThenBy(card => card.ID).First();
        }

        internal static PlannedAction ChooseNextAction(CPlayerActor actor)
        {
            List<CAbilityCard> cards = (actor.IsTakingExtraTurn
                    ? actor.CharacterClass.ExtraTurnCards
                    : actor.CharacterClass.RoundAbilityCards)
                .ToList();
            if (cards.Count == 0)
            {
                return null;
            }

            if (GameState.CurrentActionSelectionSequence == GameState.ActionSelectionSequenceType.SecondAction)
            {
                CAbilityCard usedCard = GameState.RoundAbilityCardselected;
                CAbilityCard remaining = cards.FirstOrDefault(card => card != usedCard);
                if (remaining == null)
                {
                    return null;
                }
                bool needTop = GameState.HasPlayedBottomAction;
                bool firstActionWasHeal = usedCard?.LastSelectedAction?.Abilities?
                    .OfType<CAbilityHeal>().Any() == true;
                PlannedAction result = BestHalf(actor, remaining, needTop, firstActionWasHeal);
                lock (PlanLock)
                {
                    TurnPlans.Remove(actor.ActorGuid);
                }
                return result;
            }

            if (cards.Count < 2)
            {
                return null;
            }

            PlannedAction firstTop = BestHalf(actor, cards[0], top: true);
            PlannedAction firstBottom = BestHalf(actor, cards[0], top: false);
            PlannedAction secondTop = BestHalf(actor, cards[1], top: true);
            PlannedAction secondBottom = BestHalf(actor, cards[1], top: false);

            PlannedAction[][] sequences =
            {
                BuildOrderedSequence(actor, firstTop, secondBottom),
                BuildOrderedSequence(actor, secondBottom, firstTop),
                BuildOrderedSequence(actor, secondTop, firstBottom),
                BuildOrderedSequence(actor, firstBottom, secondTop)
            };
            PlannedAction[] bestSequence = sequences
                .OrderByDescending(sequence => ScoreActionOrder(actor, sequence[0], sequence[1]))
                .ThenByDescending(sequence => sequence[0].Score)
                .First();
            PlannedAction firstAction = bestSequence[0];
            PlannedAction followup = bestSequence[1];

            lock (PlanLock)
            {
                TurnPlans[actor.ActorGuid] = new TurnPlan
                {
                    FirstCard = firstAction.Card,
                    Followup = followup
                };
            }
            return firstAction;
        }

        internal static CAbilityAttack GetFollowupAttack(CActor actor)
        {
            if (actor == null)
            {
                return null;
            }
            if (PhaseManager.Phase is CPhaseAction actionPhase)
            {
                CAbilityAttack sameActionAttack = actionPhase.RemainingPhaseAbilities
                    .Select(phaseAbility => phaseAbility.m_Ability)
                    .OfType<CAbilityAttack>()
                    .FirstOrDefault(IsSupportedAttack);
                if (sameActionAttack != null)
                {
                    return sameActionAttack;
                }
            }
            lock (PlanLock)
            {
                if (!TurnPlans.TryGetValue(actor.ActorGuid, out TurnPlan plan) ||
                    GameState.RoundAbilityCardselected != plan.FirstCard)
                {
                    return null;
                }
                if (plan.FirstCard.LastSelectedAction?.Abilities?.OfType<CAbilityHeal>().Any() == true)
                {
                    return null;
                }
                return plan.Followup?.FirstAttack;
            }
        }

        internal static CTile ChooseMoveDestination(CPlayerActor actor, CAbilityMove move)
        {
            if (actor == null || !IsSupportedMove(move))
            {
                return null;
            }

            CAbilityAttack followupAttack = GetFollowupAttack(actor);
            int desiredRange = EffectiveAttackRange(actor, followupAttack);
            if (followupAttack != null && HasAttackTargetFrom(actor, followupAttack, actor.ArrayIndex,
                    desiredRange))
            {
                return ScenarioManager.Tiles[actor.ArrayIndex.X, actor.ArrayIndex.Y];
            }

            List<CTile> legalDestinations = GameState.GetTilesInRange(actor, move.RemainingMoves,
                CAbility.EAbilityTargeting.Range, emptyTilesOnly: true,
                ignoreBlocked: move.Jump || move.Fly, innerRange: null, ignorePathLength: false,
                ignoreBlockedWithActor: false, ignoreLOS: true, emptyOpenDoorTiles: true,
                ignoreMoveCost: false, ignoreDifficultTerrain: true)
                .Where(tile => IsReachableMoveDestination(actor, move, tile))
                .ToList();
            if (followupAttack != null)
            {
                CTile attackDestination = legalDestinations
                    .Where(tile => HasAttackTargetFrom(actor, followupAttack, tile.m_ArrayIndex,
                        desiredRange))
                    .OrderByDescending(tile => ScoreMoveEndpoint(actor, followupAttack,
                        tile.m_ArrayIndex, desiredRange))
                    .ThenBy(tile => ScenarioManager.GetTileDistance(actor.ArrayIndex.X,
                        actor.ArrayIndex.Y, tile.m_ArrayIndex.X, tile.m_ArrayIndex.Y))
                    .FirstOrDefault();
                if (attackDestination != null)
                {
                    return attackDestination;
                }
            }

            actor.Move(move.RemainingMoves, move.Jump, move.Fly, desiredRange, allowMove: false,
                move.IgnoreDifficultTerrain, followupAttack, firstMove: true, moveTest: false,
                move.CarryOtherActorsOnHex);
            List<Point> path = actor.AIMoveFocusPath;
            if (path == null || path.Count == 0)
            {
                return ChooseClosedDoorDestination(actor, move, legalDestinations);
            }

            return path
                .Select(point => ScenarioManager.Tiles[point.X, point.Y])
                .LastOrDefault(legalDestinations.Contains) ??
                ChooseClosedDoorDestination(actor, move, legalDestinations);
        }

        internal static CAbilityCard ChooseCardToLose(CPlayerActor actor, IEnumerable<CAbilityCard> cards)
        {
            return RankCardsToLose(actor, cards).FirstOrDefault();
        }

        internal static List<CAbilityCard> ChooseCardsToLose(CPlayerActor actor,
            IEnumerable<CAbilityCard> cards, int count)
        {
            return RankCardsToLose(actor, cards).Take(count).ToList();
        }

        internal static string Describe(PlannedAction action)
        {
            string half = action.IsTop ? "top" : "bottom";
            string kind = action.ActionType == CBaseCard.ActionType.DefaultAttackAction ||
                action.ActionType == CBaseCard.ActionType.DefaultMoveAction
                ? "default "
                : "printed ";
            string abilities = string.Join(" + ", action.Action.Abilities.Select(ability =>
                ability.AbilityType + (ability.Strength > 0 ? " " + ability.Strength : string.Empty)).ToArray());
            return kind + half + " action (" + abilities + ", score " + action.Score.ToString("0.0") + ")";
        }

        private static IOrderedEnumerable<CAbilityCard> RankCardsToLose(CPlayerActor actor,
            IEnumerable<CAbilityCard> cards)
        {
            return cards
                .OrderBy(card => ScoreFutureCard(actor, card))
                .ThenByDescending(card => card.Initiative)
                .ThenByDescending(card => card.ID);
        }

        internal static bool IsSupportedMove(CAbilityMove move)
        {
            return move != null && move.MoveRestrictionType == CAbilityMove.EMoveRestrictionType.None &&
                !move.CarryOtherActorsOnHex && IsSimpleAbility(move);
        }

        internal static bool IsSupportedAttack(CAbilityAttack attack)
        {
            return attack != null && attack.AreaEffect == null && !attack.ChainAttack &&
                !attack.AllTargets && attack.NumberTargets == 1 && attack.Range > 0 &&
                attack.Targeting == CAbility.EAbilityTargeting.Range &&
                attack.AbilityFilter != null &&
                attack.AbilityFilter.HasTargetTypeFlag(CAbilityFilter.EFilterTargetType.Enemy, exclusive: true) &&
                attack.MiscAbilityData?.TargetOneEnemyWithAllAttacks != true &&
                (attack.AttackEffects == null || attack.AttackEffects.Count == 0) &&
                (attack.StartAbilityRequirements == null ||
                 attack.StartAbilityRequirements.StartAbilityRequirementType ==
                    CAbilityRequirements.EStartAbilityRequirementType.None) &&
                !attack.AllTargetsOnMovePath && !attack.AllTargetsOnAttackPath && IsSimpleAbility(attack);
        }

        internal static bool IsSupportedHeal(CAbilityHeal heal)
        {
            if (heal == null || heal.Strength <= 0 || heal.Range <= 0 || heal.NumberTargets <= 0 ||
                heal.Targeting != CAbility.EAbilityTargeting.Range || heal.AreaEffect != null ||
                heal.AllTargets || heal.OneTargetAtATime || heal.AllTargetsOnMovePath ||
                heal.AllTargetsOnAttackPath || heal.IsSubAbility || heal.IsInlineSubAbility ||
                heal.UseSubAbilityTargeting || heal.IsMergedAbility || heal.ParentAbility != null ||
                !IsSimpleAbility(heal) ||
                heal.StartAbilityRequirements != null &&
                heal.StartAbilityRequirements.StartAbilityRequirementType !=
                    CAbilityRequirements.EStartAbilityRequirementType.None)
            {
                return false;
            }

            CAbilityHeal.HealAbilityData healData = heal.HealData;
            if (healData == null || healData.IgnoreTokens ||
                (healData.PositiveConditionsToAddIfHealRemovesPoison?.Count ?? 0) > 0 ||
                (healData.NegativeConditionsToAddIfHealRemovesPoison?.Count ?? 0) > 0 ||
                (healData.PositiveConditionsToAddIfHealRemovesWound?.Count ?? 0) > 0 ||
                (healData.NegativeConditionsToAddIfHealRemovesWound?.Count ?? 0) > 0 ||
                heal.MiscAbilityData?.HealPercentageOfHealth != null ||
                heal.MiscAbilityData?.AlsoTargetSelf != null ||
                heal.MiscAbilityData?.AlsoTargetAdjacent != null ||
                heal.MiscAbilityData?.AutotriggerAbility == true)
            {
                return false;
            }

            if (heal.AbilityFilter == null || heal.AbilityFilter.AbilityFilters == null ||
                heal.AbilityFilter.AbilityFilters.Count != 1 || heal.AbilityFilter.HasNonTargetTypeFilters())
            {
                return false;
            }
            CAbilityFilter filter = heal.AbilityFilter.AbilityFilters[0];
            CAbilityFilter.EFilterTargetType allowed = CAbilityFilter.EFilterTargetType.Self |
                CAbilityFilter.EFilterTargetType.Ally;
            return !filter.Invert && filter.FilterTargetType != CAbilityFilter.EFilterTargetType.None &&
                (filter.FilterTargetType & ~allowed) == CAbilityFilter.EFilterTargetType.None;
        }

        internal static float ScoreAttackTarget(CActor attacker, CActor target, CAbilityAttack attack)
        {
            int shield = Math.Max(0, target.CalculateShield(attack) - attack.Pierce);
            int damage = Math.Max(0, attack.Strength - shield);
            float effectiveDamage = Math.Min(target.Health, damage);
            float score = effectiveDamage * 4f;
            if (damage >= target.Health)
            {
                score += target.ActorActionHasHappened ? 14f : 26f;
                score -= Math.Max(0, damage - target.Health) * 2f;
            }
            score += ThreatValue(target);
            score += ScoreConditions(attack, target);
            return score;
        }

        internal static float ScoreHealTarget(CActor target, CAbilityHeal heal)
        {
            return ScoreHealTarget(target, heal?.ModifiedStrength() ?? 0);
        }

        private static float ScorePair(CPlayerActor actor, CAbilityCard first, CAbilityCard second)
        {
            PlannedAction firstTop = BestHalf(actor, first, top: true);
            PlannedAction firstBottom = BestHalf(actor, first, top: false);
            PlannedAction secondTop = BestHalf(actor, second, top: true);
            PlannedAction secondBottom = BestHalf(actor, second, top: false);
            PlannedAction[][] sequences =
            {
                BuildOrderedSequence(actor, firstTop, secondBottom),
                BuildOrderedSequence(actor, secondBottom, firstTop),
                BuildOrderedSequence(actor, secondTop, firstBottom),
                BuildOrderedSequence(actor, firstBottom, secondTop)
            };
            float orientationOne = Math.Max(ScoreOrientation(actor, sequences[0][0], sequences[0][1]),
                ScoreOrientation(actor, sequences[1][0], sequences[1][1]));
            float orientationTwo = Math.Max(ScoreOrientation(actor, sequences[2][0], sequences[2][1]),
                ScoreOrientation(actor, sequences[3][0], sequences[3][1]));
            float flexibility = Math.Min(orientationOne, orientationTwo) * 0.18f;
            float initiativeCoverage = Math.Abs(first.Initiative - second.Initiative) >= 25 ? 0.75f : 0f;
            float execution = sequences.Max(sequence => ScoreActionOrder(actor, sequence[0], sequence[1]));
            return execution + flexibility + initiativeCoverage;
        }

        private static float ScoreOrientation(CPlayerActor actor, PlannedAction top, PlannedAction bottom)
        {
            float score = top.Score + bottom.Score;
            CAbilityAttack attack = top.FirstAttack ?? bottom.FirstAttack;
            CAbilityMove move = bottom.FirstMove ?? top.FirstMove;
            if (attack != null && move != null)
            {
                int gap = Math.Max(0, NearestEnemyDistance(actor) - Math.Max(1, attack.Range));
                int movement = PlannedMoveStrength(actor, move);
                if (movement >= gap)
                {
                    score += attack.Strength * 1.4f;
                }
                score += Math.Min(movement, gap) * 0.8f;
            }
            return score;
        }

        private static float ScoreActionOrder(CPlayerActor actor, PlannedAction first,
            PlannedAction second)
        {
            float score = first.Score + second.Score;
            if (first.FirstAttack != null)
            {
                float immediateAttack = BestCurrentAttackScore(actor, first.FirstAttack);
                score += immediateAttack > 0f
                    ? immediateAttack * 0.2f
                    : -Math.Max(2f, first.FirstAttack.Strength * 1.5f);
            }

            if (first.FirstMove != null && second.FirstAttack != null)
            {
                float immediateAttack = BestCurrentAttackScore(actor, second.FirstAttack);
                float reachableAttack = BestApproachableAttackScore(actor, second.FirstAttack,
                    PlannedMoveStrength(actor, first.FirstMove));
                score += reachableAttack * 0.22f;
                if (immediateAttack > 0f && reachableAttack <= immediateAttack + 2f)
                {
                    score -= 2f;
                }
            }
            return score;
        }

        private static PlannedAction[] BuildOrderedSequence(CPlayerActor actor, PlannedAction first,
            PlannedAction second)
        {
            PlannedAction followup = second;
            if (first?.FirstHeal != null && second != null)
            {
                bool canForecast = first.Action.Abilities.Count == 1 &&
                    CActiveBonus.FindApplicableActiveBonuses(actor,
                        CAbility.EAbilityType.AddTarget).Count == 0;
                followup = BestHalf(actor, second.Card, second.IsTop, discourageRoutineHeal: true,
                    precedingHeal: canForecast ? first.FirstHeal : null,
                    forceHealPenalty: !canForecast);
            }
            return new[] { first, followup };
        }

        private static PlannedAction BestHalf(CPlayerActor actor, CAbilityCard card, bool top,
            bool discourageRoutineHeal = false, CAbilityHeal precedingHeal = null,
            bool forceHealPenalty = false)
        {
            CBaseCard.ActionType printedType = top
                ? CBaseCard.ActionType.TopAction
                : CBaseCard.ActionType.BottomAction;
            CBaseCard.ActionType defaultType = top
                ? CBaseCard.ActionType.DefaultAttackAction
                : CBaseCard.ActionType.DefaultMoveAction;
            PlannedAction printed = BuildOption(actor, card, printedType);
            PlannedAction fallback = BuildOption(actor, card, defaultType);
            if (discourageRoutineHeal)
            {
                PenalizeRoutineHeal(actor, printed, precedingHeal, forceHealPenalty);
                PenalizeRoutineHeal(actor, fallback, precedingHeal, forceHealPenalty);
            }
            if (printed == null)
            {
                return fallback;
            }
            if (fallback == null)
            {
                return printed;
            }
            return printed.Score > fallback.Score + 0.2f ? printed : fallback;
        }

        private static PlannedAction BuildOption(CPlayerActor actor, CAbilityCard card,
            CBaseCard.ActionType actionType)
        {
            CAction action = card?.GetActionForType(actionType);
            if (!IsSupportedAction(action))
            {
                return null;
            }
            return new PlannedAction
            {
                Card = card,
                ActionType = actionType,
                Action = action,
                Score = ScoreAction(actor, action)
            };
        }

        private static bool IsSupportedAction(CAction action)
        {
            if (action == null || action.Abilities == null || action.Abilities.Count == 0 ||
                action.Augmentations != null && action.Augmentations.Count > 0 ||
                action.CardPile == CBaseCard.ECardPile.PermanentlyLost)
            {
                return false;
            }
            return action.Abilities.All(ability =>
                ability is CAbilityMove move && IsSupportedMove(move) ||
                ability is CAbilityAttack attack && IsSupportedAttack(attack) ||
                ability is CAbilityHeal heal && IsSupportedHeal(heal));
        }

        private static bool IsSimpleAbility(CAbility ability)
        {
            return ability != null && !ability.AbilityTextOnly && !ability.IsConsumeAbility &&
                (ability.SubAbilities == null || ability.SubAbilities.Count == 0) &&
                (ability.ConditionalOverrides == null || ability.ConditionalOverrides.Count == 0) &&
                (ability.StatIsBasedOnXEntries == null || ability.StatIsBasedOnXEntries.Count == 0) &&
                (ability.ActiveBonusData == null ||
                 ability.ActiveBonusData.Duration == CActiveBonus.EActiveBonusDurationType.NA);
        }

        private static float ScoreAction(CPlayerActor actor, CAction action)
        {
            float score = (action.ActionXP * 0.25f) + ((action.Infusions?.Count ?? 0) * 0.35f);
            foreach (CAbility ability in action.Abilities)
            {
                if (ability is CAbilityMove move)
                {
                    int movement = PlannedMoveStrength(actor, move);
                    int usefulMovement = Math.Min(movement,
                        Math.Max(0, NearestEnemyDistance(actor) - 1));
                    score += usefulMovement * 1.6f;
                    score += Math.Max(0, movement - usefulMovement) * 0.25f;
                    if (move.Jump)
                    {
                        score += 1.25f;
                    }
                    if (move.Fly)
                    {
                        score += 1.75f;
                    }
                }
                else if (ability is CAbilityAttack attack)
                {
                    score += ScoreAttack(actor, attack);
                }
                else if (ability is CAbilityHeal heal)
                {
                    score += ScoreHeal(actor, heal);
                }
            }

            if (action.CardPile == CBaseCard.ECardPile.Lost)
            {
                int cyclingCards = actor.CharacterClass.HandAbilityCards.Count +
                    actor.CharacterClass.DiscardedAbilityCards.Count +
                    actor.CharacterClass.RoundAbilityCards.Count;
                int turnsLost = Math.Max(1, (cyclingCards - 1) / 2);
                float endgameFactor = HostileActors(actor).Count <= 2 ? 0.35f : 1f;
                score -= turnsLost * 5f * endgameFactor;
            }
            return score;
        }

        private static float ScoreAttack(CPlayerActor actor, CAbilityAttack attack)
        {
            int range = Math.Max(1, attack.Range);
            List<CActor> targets = HostileActors(actor)
                .Where(target => IsValidTarget(actor, target, attack) &&
                    Distance(actor, target) <= range && HasLineOfSight(actor, target))
                .OrderByDescending(target => ScoreStaticAttackTarget(target, attack))
                .ToList();
            int targetCount = Math.Max(1, attack.NumberTargets);
            if (targets.Count == 0)
            {
                return Math.Max(0, attack.Strength) * 0.65f + Math.Max(0, range - 1) * 0.3f;
            }
            return targets.Take(targetCount).Sum(target => ScoreStaticAttackTarget(target, attack));
        }

        private static float BestCurrentAttackScore(CPlayerActor actor, CAbilityAttack attack)
        {
            if (attack == null)
            {
                return 0f;
            }
            int range = EffectiveAttackRange(actor, attack);
            return HostileActors(actor)
                .Where(target => IsValidTarget(actor, target, attack) &&
                    Distance(actor, target) <= range && HasLineOfSight(actor, target))
                .Select(target => ScoreAttackTarget(actor, target, attack))
                .DefaultIfEmpty(0f)
                .Max();
        }

        private static float BestApproachableAttackScore(CPlayerActor actor, CAbilityAttack attack,
            int movement)
        {
            if (attack == null)
            {
                return 0f;
            }
            int reach = Math.Max(0, movement) + EffectiveAttackRange(actor, attack);
            return HostileActors(actor)
                .Where(target => IsValidTarget(actor, target, attack) && Distance(actor, target) <= reach)
                .Select(target => ScoreAttackTarget(actor, target, attack))
                .DefaultIfEmpty(0f)
                .Max();
        }

        private static float ScoreStaticAttackTarget(CActor target, CAbilityAttack attack)
        {
            int shield = Math.Max(0, target.CalculateShield(attack) - attack.Pierce);
            int damage = Math.Max(0, attack.Strength - shield);
            float score = Math.Min(target.Health, damage) * 3f;
            if (damage >= target.Health)
            {
                score += target.ActorActionHasHappened ? 8f : 15f;
                score -= Math.Max(0, damage - target.Health) * 1.5f;
            }
            score += ScoreConditions(attack, target);
            return score;
        }

        private static float ScoreHeal(CPlayerActor actor, CAbilityHeal heal)
        {
            int strength = EffectiveHealStrength(actor, heal);
            return HealTargetsInRange(actor, heal)
                .Select(target => ScoreHealTarget(target, strength))
                .Where(score => score > 0f)
                .OrderByDescending(score => score)
                .Take(Math.Max(1, EnhancedValue(heal, heal.NumberTargets, EEnhancement.PlusTarget)))
                .Sum();
        }

        private static float ScoreHealTarget(CActor target, int strength)
        {
            if (target == null || target.IsDead || target.Deactivated || target.PhasedOut || strength <= 0 ||
                CActiveBonus.FindApplicableActiveBonuses(target, CAbility.EAbilityType.BlockHealing).Count > 0)
            {
                return 0f;
            }

            bool poisoned = target.Tokens.HasKey(CCondition.ENegativeCondition.Poison);
            bool wounded = target.Tokens.HasKey(CCondition.ENegativeCondition.Wound);
            int missingHealth = Math.Max(0, target.MaxHealth - target.Health);
            int restored = poisoned ? 0 : Math.Min(missingHealth, strength);
            float healthRatio = target.MaxHealth > 0 ? (float)target.Health / target.MaxHealth : 1f;
            float urgency = healthRatio <= 0.25f ? 10f : healthRatio <= 0.4f ? 5f : 0f;
            float score = restored * 1.25f + (poisoned ? 7f : 0f) + (wounded ? 5f : 0f) + urgency;
            score += target.MaxHealth > 0 ? missingHealth * 2f / target.MaxHealth : 0f;
            if (!poisoned && !wounded)
            {
                score -= Math.Max(0, strength - missingHealth) * 0.75f;
            }
            if (target is CPlayerActor)
            {
                score += 0.5f;
            }
            return Math.Max(0f, score);
        }

        private static void PenalizeRoutineHeal(CPlayerActor actor, PlannedAction action,
            CAbilityHeal precedingHeal, bool forceHealPenalty)
        {
            if (action?.FirstHeal != null &&
                (forceHealPenalty || !HasEmergencyHealTarget(actor, action.FirstHeal, precedingHeal)))
            {
                action.Score -= 8f;
            }
        }

        private static bool HasEmergencyHealTarget(CPlayerActor actor, CAbilityHeal heal,
            CAbilityHeal precedingHeal = null)
        {
            if (actor == null || heal == null)
            {
                return false;
            }
            int strength = EffectiveHealStrength(actor, heal);
            if (strength <= 0)
            {
                return false;
            }

            Dictionary<CActor, int> forecastHealth = new Dictionary<CActor, int>();
            if (precedingHeal != null)
            {
                int precedingStrength = EffectiveHealStrength(actor, precedingHeal);
                IEnumerable<CActor> precedingTargets = HealTargetsInRange(actor, precedingHeal)
                    .Where(target => ScoreHealTarget(target, precedingStrength) > 0f)
                    .OrderByDescending(target => ScoreHealTarget(target, precedingStrength))
                    .Take(Math.Max(1, EnhancedValue(precedingHeal, precedingHeal.NumberTargets,
                        EEnhancement.PlusTarget)));
                foreach (CActor target in precedingTargets)
                {
                    bool poisoned = target.Tokens.HasKey(CCondition.ENegativeCondition.Poison);
                    forecastHealth[target] = poisoned
                        ? target.Health
                        : Math.Min(target.MaxHealth, target.Health + precedingStrength);
                }
            }

            return HealTargetsInRange(actor, heal).Any(target =>
            {
                if (target == null || target.IsDead || target.MaxHealth <= 0)
                {
                    return false;
                }
                int health = forecastHealth.TryGetValue(target, out int forecast)
                    ? forecast
                    : target.Health;
                return health * 5 <= target.MaxHealth * 2 &&
                    CActiveBonus.FindApplicableActiveBonuses(target,
                        CAbility.EAbilityType.BlockHealing).Count == 0;
            });
        }

        private static List<CActor> HealTargetsInRange(CPlayerActor actor, CAbilityHeal heal)
        {
            int range = EnhancedValue(heal, heal.Range, EEnhancement.PlusRange);
            return ScenarioRuleClient.GetActorsInRange(actor, actor, range, heal.ActorsToIgnore,
                heal.AbilityFilter, null, null, heal.IsTargetedAbility,
                heal.MiscAbilityData?.CanTargetInvisible);
        }

        private static int EffectiveHealStrength(CPlayerActor actor, CAbilityHeal heal)
        {
            int strength = PrintedStrength(heal, EEnhancement.PlusHeal);
            return strength + CActiveBonus.FindApplicableActiveBonuses(actor,
                    CAbility.EAbilityType.AddHeal)
                .Where(activeBonus => activeBonus.Ability == null ||
                    activeBonus.Ability.ActiveBonusData?.Behaviour !=
                        CActiveBonus.EActiveBonusBehaviourType.BuffIncomingHeal)
                .Sum(activeBonus => activeBonus.ReferenceStrength(heal, actor));
        }

        private static float ScoreConditions(CAbilityAttack attack, CActor target)
        {
            if (attack?.NegativeConditions == null)
            {
                return 0f;
            }
            float score = 0f;
            foreach (CCondition.ENegativeCondition condition in attack.NegativeConditions.Keys)
            {
                if (target.Tokens.HasKey(condition))
                {
                    continue;
                }
                switch (condition)
                {
                    case CCondition.ENegativeCondition.Stun:
                    case CCondition.ENegativeCondition.Sleep:
                        score += target.ActorActionHasHappened ? 2f : 12f;
                        break;
                    case CCondition.ENegativeCondition.Disarm:
                        score += target.ActorActionHasHappened ? 1f : 9f;
                        break;
                    case CCondition.ENegativeCondition.Immobilize:
                        score += target.ActorActionHasHappened ? 1f : 5f;
                        break;
                    case CCondition.ENegativeCondition.Poison:
                    case CCondition.ENegativeCondition.Wound:
                        score += 4f;
                        break;
                    case CCondition.ENegativeCondition.Muddle:
                    case CCondition.ENegativeCondition.Curse:
                        score += 3f;
                        break;
                    default:
                        score += 1f;
                        break;
                }
            }
            return score;
        }

        private static float ScoreFutureCard(CPlayerActor actor, CAbilityCard card)
        {
            PlannedAction top = BestHalf(actor, card, top: true);
            PlannedAction bottom = BestHalf(actor, card, top: false);
            float initiativeCoverage = card.Initiative <= 25 || card.Initiative >= 75 ? 1.5f : 0f;
            return (top?.Score ?? 0f) + (bottom?.Score ?? 0f) + initiativeCoverage;
        }

        private static bool HasAttackTarget(CPlayerActor actor, CAbilityAttack attack)
        {
            if (attack == null)
            {
                return false;
            }
            int range = Math.Max(1, attack.Range);
            return HostileActors(actor).Any(target => IsValidTarget(actor, target, attack) &&
                Distance(actor, target) <= range && HasLineOfSight(actor, target));
        }

        private static bool HasAttackTargetFrom(CPlayerActor actor, CAbilityAttack attack,
            Point position, int range)
        {
            CTile sourceTile = ScenarioManager.Tiles[position.X, position.Y];
            return sourceTile != null && HostileActors(actor).Any(target =>
            {
                if (!IsValidTarget(actor, target, attack) ||
                    ScenarioManager.GetTileDistance(position.X, position.Y,
                        target.ArrayIndex.X, target.ArrayIndex.Y) > range)
                {
                    return false;
                }
                CTile targetTile = ScenarioManager.Tiles[target.ArrayIndex.X, target.ArrayIndex.Y];
                return targetTile != null && CActor.HaveLOS(sourceTile, targetTile);
            });
        }

        private static bool IsReachableMoveDestination(CPlayerActor actor, CAbilityMove move,
            CTile destination)
        {
            return MovePathCost(actor, move, destination) <= move.RemainingMoves;
        }

        private static int MovePathCost(CPlayerActor actor, CAbilityMove move, CTile destination,
            bool requireSafeDoorPath = false)
        {
            if (actor == null || move == null || destination == null)
            {
                return int.MaxValue;
            }
            bool foundPath;
            List<Point> path = CActor.FindCharacterPath(actor, actor.ArrayIndex,
                destination.m_ArrayIndex, move.Jump || move.Fly, ignoreMoveCost: false,
                out foundPath, avoidTraps: true, move.IgnoreDifficultTerrain,
                move.IgnoreHazardousTerrain, move.CarryOtherActorsOnHex);
            int cost = foundPath
                ? CAbilityMove.CalculateMoveCost(path, !move.Fly, !move.Jump,
                    ignoreMoveCost: false, move.IgnoreDifficultTerrain,
                    move.IgnoreBlockedTileMoveCost)
                : int.MaxValue;
            if (foundPath && cost <= move.RemainingMoves)
            {
                return !requireSafeDoorPath || IsSafeClosedDoorPath(path, destination)
                    ? cost
                    : int.MaxValue;
            }

            path = CActor.FindCharacterPath(actor, actor.ArrayIndex,
                destination.m_ArrayIndex, move.Jump || move.Fly, ignoreMoveCost: false,
                out foundPath, avoidTraps: false, move.IgnoreDifficultTerrain,
                move.IgnoreHazardousTerrain, move.CarryOtherActorsOnHex);
            cost = foundPath
                ? CAbilityMove.CalculateMoveCost(path, !move.Fly, !move.Jump,
                    ignoreMoveCost: false, move.IgnoreDifficultTerrain,
                    move.IgnoreBlockedTileMoveCost)
                : int.MaxValue;
            return foundPath && (!requireSafeDoorPath || IsSafeClosedDoorPath(path, destination))
                ? cost
                : int.MaxValue;
        }

        private static bool IsSafeClosedDoorPath(List<Point> path, CTile destination)
        {
            if (path == null || path.Count == 0 || path[path.Count - 1] != destination.m_ArrayIndex)
            {
                return false;
            }
            for (int index = 0; index < path.Count; index++)
            {
                CTile tile = ScenarioManager.Tiles[path[index].X, path[index].Y];
                if (tile == null || tile.m_HexMap?.Revealed != true && tile.m_Hex2Map?.Revealed != true)
                {
                    return false;
                }
                CObjectDoor door = tile.FindProp(ScenarioManager.ObjectImportType.Door) as CObjectDoor;
                if (index < path.Count - 1 && door != null && !door.DoorIsOpen)
                {
                    return false;
                }
            }
            return true;
        }

        private static CTile ChooseClosedDoorDestination(CPlayerActor actor, CAbilityMove move,
            List<CTile> legalDestinations)
        {
            if (move.IgnoreBlockedTileMoveCost || ScenarioManager.CurrentScenarioState?.DoorProps == null)
            {
                return null;
            }

            CTile bestDestination = null;
            int bestDoorCost = int.MaxValue;
            foreach (CObjectDoor door in ScenarioManager.CurrentScenarioState.DoorProps.OfType<CObjectDoor>())
            {
                if (door.DoorIsOpen || door.DoorIsLocked || door.IsDungeonEntrance || door.IsDungeonExit ||
                    door.PropHealthDetails != null && door.PropHealthDetails.HasHealth &&
                    door.PropHealthDetails.CurrentHealth > 0)
                {
                    continue;
                }
                CTile doorTile = ScenarioManager.Tiles[door.ArrayIndex.X, door.ArrayIndex.Y];
                if (doorTile == null ||
                    doorTile.m_HexMap?.Revealed != true && doorTile.m_Hex2Map?.Revealed != true ||
                    !ScenarioManager.PathFinder.Nodes[doorTile.m_ArrayIndex.X,
                        doorTile.m_ArrayIndex.Y].Walkable ||
                    ScenarioManager.PathFinder.Nodes[doorTile.m_ArrayIndex.X,
                        doorTile.m_ArrayIndex.Y].Blocked ||
                    ScenarioManager.Scenario.FindActorsAt(doorTile.m_ArrayIndex).Count > 0)
                {
                    continue;
                }

                List<Point> doorPath = FindSafeClosedDoorPath(actor, move, doorTile, out int doorCost);
                if (doorPath == null || doorCost >= bestDoorCost)
                {
                    continue;
                }
                CTile destination = doorCost <= move.RemainingMoves
                    ? doorTile
                    : doorPath.Select(point => ScenarioManager.Tiles[point.X, point.Y])
                        .Where(legalDestinations.Contains)
                        .LastOrDefault(tile => MovePathCost(actor, move, tile,
                            requireSafeDoorPath: true) <= move.RemainingMoves);
                if (destination != null)
                {
                    bestDoorCost = doorCost;
                    bestDestination = destination;
                }
            }
            return bestDestination;
        }

        private static List<Point> FindSafeClosedDoorPath(CPlayerActor actor, CAbilityMove move,
            CTile doorTile, out int cost)
        {
            bool foundPath;
            List<Point> path = CActor.FindCharacterPath(actor, actor.ArrayIndex,
                doorTile.m_ArrayIndex, move.Jump || move.Fly, ignoreMoveCost: false,
                out foundPath, avoidTraps: true, move.IgnoreDifficultTerrain,
                move.IgnoreHazardousTerrain, move.CarryOtherActorsOnHex);
            if (!foundPath)
            {
                path = CActor.FindCharacterPath(actor, actor.ArrayIndex,
                    doorTile.m_ArrayIndex, move.Jump || move.Fly, ignoreMoveCost: false,
                    out foundPath, avoidTraps: false, move.IgnoreDifficultTerrain,
                    move.IgnoreHazardousTerrain, move.CarryOtherActorsOnHex);
            }
            if (!foundPath || !IsSafeClosedDoorPath(path, doorTile))
            {
                cost = int.MaxValue;
                return null;
            }
            cost = CAbilityMove.CalculateMoveCost(path, !move.Fly, !move.Jump,
                ignoreMoveCost: false, move.IgnoreDifficultTerrain,
                move.IgnoreBlockedTileMoveCost);
            return path;
        }

        private static int PlannedMoveStrength(CPlayerActor actor, CAbilityMove move)
        {
            if (actor == null || move == null)
            {
                return 0;
            }
            int strength = PrintedStrength(move, EEnhancement.PlusMove);
            List<CActiveBonus> bonuses = CActiveBonus.FindApplicableActiveBonuses(actor,
                CAbility.EAbilityType.Move);
            foreach (CActiveBonus bonus in bonuses)
            {
                strength += bonus.ReferenceStrength(move, actor);
            }
            foreach (CActiveBonus bonus in bonuses)
            {
                strength *= bonus.ReferenceStrengthScalar(move, actor);
            }
            return Math.Max(0, strength);
        }

        private static int PrintedStrength(CAbility ability, EEnhancement strengthEnhancement)
        {
            return EnhancedValue(ability, ability?.Strength ?? 0, strengthEnhancement);
        }

        private static int EnhancedValue(CAbility ability, int value, EEnhancement enhancementType)
        {
            if (ability == null)
            {
                return 0;
            }
            if (!ability.AppliedEnhancements && ability.AbilityEnhancements != null)
            {
                value += ability.AbilityEnhancements.Count(enhancement =>
                    enhancement.Enhancement == enhancementType);
            }
            return value;
        }

        private static float ScoreMoveEndpoint(CPlayerActor actor, CAbilityAttack attack,
            Point position, int range)
        {
            CTile sourceTile = ScenarioManager.Tiles[position.X, position.Y];
            if (sourceTile == null)
            {
                return float.MinValue;
            }

            List<CActor> hostiles = HostileActors(actor);
            CActor bestTarget = hostiles
                .Where(target => IsValidTarget(actor, target, attack) &&
                    ScenarioManager.GetTileDistance(position.X, position.Y,
                        target.ArrayIndex.X, target.ArrayIndex.Y) <= range &&
                    CActor.HaveLOS(sourceTile,
                        ScenarioManager.Tiles[target.ArrayIndex.X, target.ArrayIndex.Y]))
                .OrderByDescending(target => ScoreAttackTarget(actor, target, attack))
                .FirstOrDefault();
            if (bestTarget == null)
            {
                return float.MinValue;
            }

            int targetShield = Math.Max(0, bestTarget.CalculateShield(attack) - attack.Pierce);
            bool targetLikelyDies = attack.Strength - targetShield >= bestTarget.Health;
            float exposure = 0f;
            foreach (CActor hostile in hostiles)
            {
                if (hostile == bestTarget && targetLikelyDies)
                {
                    continue;
                }
                int distance = ScenarioManager.GetTileDistance(position.X, position.Y,
                    hostile.ArrayIndex.X, hostile.ArrayIndex.Y);
                exposure += distance == 1 ? 7f : distance == 2 ? 2.5f : distance == 3 ? 0.5f : 0f;
            }
            if (actor.Health * 2 <= actor.MaxHealth)
            {
                exposure *= 1.5f;
            }
            return ScoreAttackTarget(actor, bestTarget, attack) - exposure;
        }

        private static int EffectiveAttackRange(CActor actor, CAbilityAttack attack)
        {
            if (attack == null)
            {
                return 1;
            }
            int bonus = CActiveBonus.FindApplicableActiveBonuses(actor, CAbility.EAbilityType.AddRange)
                .Where(activeBonus => activeBonus.Ability.Augment == null)
                .Sum(activeBonus => activeBonus.ReferenceStrength(attack, null));
            return Math.Max(1, attack.Range + bonus);
        }

        private static bool IsValidTarget(CPlayerActor actor, CActor target, CAbility ability)
        {
            if (target == null || target.IsDead || target.Deactivated || target.PhasedOut || target.Untargetable ||
                CActor.AreActorsAllied(actor.Type, target.Type))
            {
                return false;
            }
            bool? canTargetInvisible = ability.MiscAbilityData?.CanTargetInvisible;
            return ability.AbilityFilter == null || ability.AbilityFilter.IsValidTarget(target, actor,
                ability.IsTargetedAbility, useTargetOriginalType: false, canTargetInvisible);
        }

        private static List<CActor> HostileActors(CActor actor)
        {
            CScenario scenario = ScenarioManager.Scenario;
            if (scenario == null)
            {
                return new List<CActor>();
            }
            return scenario.Enemies.Cast<CActor>()
                .Concat(scenario.Enemy2Monsters)
                .Concat(scenario.Objects)
                .Where(target => target != null && target != actor && !target.IsDead &&
                    !target.Deactivated && !target.PhasedOut && !target.Untargetable &&
                    !CActor.AreActorsAllied(actor.Type, target.Type)).ToList();
        }

        private static int NearestEnemyDistance(CActor actor)
        {
            List<CActor> hostiles = HostileActors(actor);
            return hostiles.Count == 0 ? 999 : hostiles.Min(target => Distance(actor, target));
        }

        private static int Distance(CActor first, CActor second)
        {
            return ScenarioManager.GetTileDistance(first.ArrayIndex.X, first.ArrayIndex.Y,
                second.ArrayIndex.X, second.ArrayIndex.Y);
        }

        private static bool HasLineOfSight(CActor first, CActor second)
        {
            CTile firstTile = ScenarioManager.Tiles[first.ArrayIndex.X, first.ArrayIndex.Y];
            CTile secondTile = ScenarioManager.Tiles[second.ArrayIndex.X, second.ArrayIndex.Y];
            return firstTile != null && secondTile != null && CActor.HaveLOS(firstTile, secondTile);
        }

        private static float ThreatValue(CActor actor)
        {
            return Math.Min(8f, actor.MaxHealth * 0.3f) + (actor.ActorActionHasHappened ? 0f : 5f);
        }

        private static int ComparePair(CAbilityCard first, CAbilityCard second,
            CAbilityCard currentFirst, CAbilityCard currentSecond)
        {
            if (currentFirst == null)
            {
                return -1;
            }
            int initiative = Math.Min(first.Initiative, second.Initiative)
                .CompareTo(Math.Min(currentFirst.Initiative, currentSecond.Initiative));
            if (initiative != 0)
            {
                return initiative;
            }
            int id = first.ID.CompareTo(currentFirst.ID);
            return id != 0 ? id : second.ID.CompareTo(currentSecond.ID);
        }
    }
}
