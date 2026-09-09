using System;
using System.Collections.Generic;
using System.Globalization;
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
            internal PlannedAction First;
            internal PlannedAction Followup;
            internal float Score;
            internal float AttackValue;
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

        internal static void InvalidatePlan(CActor actor)
        {
            if (actor == null)
            {
                return;
            }
            lock (PlanLock)
            {
                TurnPlans.Remove(actor.ActorGuid);
            }
        }

        internal static List<CAbilityCard> ChooseRoundCards(CPlayerActor actor, int cardsNeeded)
        {
            List<CAbilityCard> hand = actor.CharacterClass.HandAbilityCards.ToList();
            List<CAbilityCard> selected = actor.CharacterClass.RoundAbilityCards.ToList();
            var reachable = new Dictionary<Tuple<CPlayerActor, int, int>, List<CTile>>();
            if (cardsNeeded <= 0 || hand.Count < cardsNeeded)
            {
                return new List<CAbilityCard>();
            }

            if (selected.Count == 1 && cardsNeeded == 1)
            {
                CAbilityCard partner = hand
                    .OrderByDescending(card => ScorePair(actor, selected[0], card, reachable))
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
                    float score = ScorePair(actor, first, second, reachable);
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

            TurnPlan plan = cards.Count >= 2
                ? EvaluatePlans(actor, cards[0], cards[1]).OrderByDescending(value => value.Score)
                    .ThenByDescending(value => value.First.Score).FirstOrDefault()
                : null;
            float attackValue = plan?.AttackValue ?? 0f;
            float healValue = plan == null ? 0f : new[] { plan.First, plan.Followup }
                .SelectMany(action => action.Action.Abilities.OfType<CAbilityHeal>())
                .Sum(heal => ScoreHeal(actor, heal));
            bool preparingDoor = plan != null && HasRevealedClosedDoor() &&
                new[] { plan.First, plan.Followup }.SelectMany(action => action.Action.Abilities)
                    .SkipWhile(ability => !(ability is CAbilityMove)).Skip(1)
                    .Any(ability => ability is CAbilityAttack attack && IsSupportedAttack(attack));
            bool underPressure = NearestEnemyDistance(actor) <= 3 || actor.Health * 2 <= actor.MaxHealth || preparingDoor;
            return cards.OrderByDescending(card => TacticalEvaluation.InitiativeValue(card.Initiative,
                    attackValue, healValue, underPressure))
                .ThenBy(card => card.ID).ThenBy(card => card.CardInstanceID).First();
        }

        internal static PlannedAction ChooseNextAction(CPlayerActor actor)
        {
            List<CAbilityCard> cards = (actor.IsTakingExtraTurn
                    ? actor.CharacterClass.ExtraTurnCards
                    : actor.CharacterClass.RoundAbilityCards)
                .ToList();
            if (cards.Count == 0)
            {
                InvalidatePlan(actor);
                return null;
            }

            if (GameState.CurrentActionSelectionSequence == GameState.ActionSelectionSequenceType.SecondAction)
            {
                InvalidatePlan(actor);
                CAbilityCard usedCard = GameState.RoundAbilityCardselected;
                CAbilityCard remaining = cards.FirstOrDefault(card => card != usedCard);
                if (remaining == null)
                {
                    return null;
                }
                bool needTop = GameState.HasPlayedBottomAction;
                bool firstActionWasHeal = usedCard?.LastSelectedAction?.Abilities?
                    .OfType<CAbilityHeal>().Any() == true;
                var reachable = new Dictionary<Tuple<CPlayerActor, int, int>, List<CTile>>();
                return HalfOptions(actor, remaining, needTop)
                    .Select(option =>
                    {
                        if (firstActionWasHeal)
                        {
                            PenalizeRoutineHeal(actor, option, null, false);
                        }
                        return option;
                    })
                    .OrderByDescending(option =>
                    {
                        float score = ScoreActionOrder(actor, option, null, reachable, out _);
                        RecordActionCandidate(actor, option, null, score);
                        return score;
                    })
                    .ThenByDescending(option => option.Action.Abilities.OfType<CAbilityAttack>()
                        .Sum(attack => attack.Strength))
                    .FirstOrDefault();
            }

            if (cards.Count < 2)
            {
                InvalidatePlan(actor);
                return null;
            }

            TurnPlan plan = EvaluatePlans(actor, cards[0], cards[1], recordCandidates: true)
                .OrderByDescending(sequence => sequence.Score)
                .ThenByDescending(sequence => sequence.First.Score)
                // Equal capped utility should not prefer Attack 2 over a stronger non-loss attack.
                .ThenByDescending(sequence => sequence.First.Action.Abilities
                    .Concat(sequence.Followup.Action.Abilities).OfType<CAbilityAttack>().Sum(attack => attack.Strength))
                .FirstOrDefault();
            if (plan == null)
            {
                InvalidatePlan(actor);
                return null;
            }

            lock (PlanLock)
            {
                TurnPlans[actor.ActorGuid] = plan;
            }
            return plan.First;
        }

        internal static CAbilityAttack GetFollowupAttack(CActor actor)
        {
            return GetFollowupAttacks(actor).FirstOrDefault();
        }

        internal static List<CAbilityAttack> GetFollowupAttacks(CActor actor)
        {
            return AttackSegment(GetFollowupAbilities(actor));
        }

        private static List<CAbility> GetFollowupAbilities(CActor actor)
        {
            if (actor == null || GameState.InternalCurrentActor != actor)
            {
                return new List<CAbility>();
            }
            List<CAbility> abilities = new List<CAbility>();
            if (PhaseManager.Phase is CPhaseAction actionPhase)
            {
                abilities.AddRange(actionPhase.RemainingPhaseAbilities.Select(phaseAbility => phaseAbility.m_Ability));
            }
            lock (PlanLock)
            {
                if (TurnPlans.TryGetValue(actor.ActorGuid, out TurnPlan plan) &&
                    GameState.RoundAbilityCardselected == plan.First.Card &&
                    TacticalEvaluation.MatchesCommittedAction(plan.First.Action.ID,
                        plan.First.Card.LastSelectedAction?.ID, (int)plan.First.ActionType,
                        (int)GameState.RoundAbilityCardActionType) &&
                    actor is CPlayerActor player &&
                    (player.IsTakingExtraTurn ? player.CharacterClass.ExtraTurnCards :
                        player.CharacterClass.RoundAbilityCards).Contains(plan.Followup.Card))
                {
                    abilities.AddRange(plan.Followup.Action.Abilities);
                }
            }
            return abilities;
        }

        private static List<CAbilityAttack> AttackSegment(IEnumerable<CAbility> abilities)
        {
            // Recovery changes card availability; replan rather than looking through it.
            return abilities.TakeWhile(ability => !(ability is CAbilityMove) &&
                    !(ability is CAbilityRecoverLostCards) &&
                    !(ability is CAbilityAttack attack && !IsSupportedAttack(attack)))
                .OfType<CAbilityAttack>().ToList();
        }

        // Returns a demonstrated total route cost, not a movement bonus. Zero means do not spend.
        internal static int UsefulBootsPathCost(CPlayerActor actor, CAbilityMove move, int maxBonus)
        {
            if (actor == null || !IsSupportedMove(move) || maxBonus <= 0 || move.RemainingMoves < 0 ||
                move.RemainingMoves > int.MaxValue - maxBonus || move.HasMoved ||
                move.IsItemAbility || move.IsMergedAbility || actor.IsDead ||
                move.IgnoreBlockedTileMoveCost || GameState.InternalCurrentActor != actor ||
                !(PhaseManager.Phase is CPhaseAction phase) || phase.CurrentPhaseAbility?.m_Ability != move ||
                move.State != CAbilityMove.EMoveState.ActorIsSelectingMoveTile ||
                actor.Tokens.HasKey(CCondition.ENegativeCondition.Immobilize) ||
                actor.Tokens.HasKey(CCondition.ENegativeCondition.Stun) ||
                actor.Tokens.HasKey(CCondition.ENegativeCondition.Sleep))
            {
                return 0;
            }
            List<CAbilityAttack> attacks = GetFollowupAttacks(actor);
            if (attacks.Count == 0 || attacks.Any(attack => BestCurrentAttackScore(actor, attack) > 0f) ||
                !HasPotentialAttackTarget(actor, attacks, move.RemainingMoves + maxBonus))
            {
                return 0;
            }

            int current = move.RemainingMoves;
            int budget = current + maxBonus;
            int bestCost = int.MaxValue;
            foreach (CTile tile in MoveDestinationCandidates(actor, budget))
            {
                // Filter firing positions before doing any path searches. Never spend on a blank attack.
                float endpointScore = ScoreMoveEndpoint(actor, attacks, tile.m_ArrayIndex, out float attackValue);
                if (attackValue <= 0f)
                {
                    continue;
                }
                int currentCost = MovePathCost(actor, move, tile, budget: current);
                if (currentCost <= current)
                {
                    return 0;
                }
                int boostedCost = MovePathCost(actor, move, tile, budget: budget);
                if (boostedCost == int.MaxValue || boostedCost > budget)
                {
                    continue;
                }
                // A larger item bonus can switch the engine back to its preferred route.
                // That route must also be safe, not just the shorter fallback at a tight budget.
                int cost = Math.Min(currentCost, boostedCost);
                if (cost > current && cost <= budget && cost < bestCost &&
                    endpointScore > 0.2f)
                {
                    bestCost = cost;
                }
            }
            // No speculative door spending: opening an unknown room does not prove useful boots.
            return bestCost == int.MaxValue ? 0 : bestCost;
        }

        internal static CTile ChooseMoveDestination(CPlayerActor actor, CAbilityMove move)
        {
            if (actor == null || !IsSupportedMove(move) || TileAt(actor.ArrayIndex) == null ||
                ScenarioManager.Scenario == null || ScenarioManager.PathFinder?.Nodes == null ||
                actor.Tokens.HasKey(CCondition.ENegativeCondition.Immobilize) ||
                actor.Tokens.HasKey(CCondition.ENegativeCondition.Stun) ||
                actor.Tokens.HasKey(CCondition.ENegativeCondition.Sleep))
            {
                return null;
            }

            List<CAbilityAttack> attacks = GetFollowupAttacks(actor);
            List<CTile> legalDestinations = null;
            List<CAbilityHeal> heals = GetFollowupAbilities(actor).OfType<CAbilityHeal>()
                .Where(IsSupportedHeal).ToList();
            CTile currentTile = ScenarioManager.Tiles[actor.ArrayIndex.X, actor.ArrayIndex.Y];
            if (HasPotentialAttackTarget(actor, attacks, move.RemainingMoves))
            {
                legalDestinations = ReachableMoveDestinations(actor, move, move.RemainingMoves);
                if (new[] { currentTile }.Concat(legalDestinations).Any(tile => tile != null &&
                    attacks.Any(attack => HasAttackTargetFrom(actor, attack, tile.m_ArrayIndex,
                        EffectiveAttackRange(actor, attack)))))
                {
                    return ChooseAttackEndpoint(actor, attacks, legalDestinations, true, out _, out _);
                }
            }

            legalDestinations = legalDestinations ?? ReachableMoveDestinations(actor, move, move.RemainingMoves);
            CTile result = ChooseApproachOrRetreat(actor, move, attacks, legalDestinations, heals,
                true, out float value);
            CTile doorDestination = ChooseClosedDoorDestination(actor, move, legalDestinations);
            // Door readiness is separate from demonstrated progress against a known enemy.
            if (doorDestination != null &&
                ThreatAt(actor, doorDestination.m_ArrayIndex) <= ThreatAt(actor, actor.ArrayIndex) &&
                PassivePositionValue(actor, doorDestination.m_ArrayIndex, heals) >= value - 0.5f)
            {
                result = doorDestination;
                value = PassivePositionValue(actor, doorDestination.m_ArrayIndex, heals);
            }
            RecordSurvivalCandidate("move_candidate", actor, result,
                result == doorDestination ? "door" : result == currentTile ? "no_action" : "best_score", value);
            return result;
        }

        // Destinations must be validated by ReachableMoveDestinations under this move's budget.
        // No committed turn plan is required: callers supply the remaining attacks/heals explicitly.
        internal static CTile ChooseApproachOrRetreat(CPlayerActor actor, CAbilityMove move,
            List<CAbilityAttack> attacks, List<CTile> destinations, List<CAbilityHeal> heals,
            bool recordCandidates, out float score)
        {
            CTile best = ChooseSurvivalEndpoint(actor, destinations, heals, recordCandidates, out score);
            List<CActor> hostiles = HostileActors(actor);
            int cyclingCards = actor.CharacterClass.HandAbilityCards.Count +
                actor.CharacterClass.DiscardedAbilityCards.Count + actor.CharacterClass.RoundAbilityCards.Count;
            if (hostiles.Count == 0 || actor.Health * 2L <= actor.MaxHealth || cyclingCards < 2 ||
                actor.Tokens.HasKey(CCondition.ENegativeCondition.Disarm))
            {
                return best;
            }
            if (attacks.Count == 0)
            {
                // Reposition for an actual reusable attack next round, not an invented range-1 goal.
                List<CAbilityCard> futureCards = actor.CharacterClass.HandAbilityCards.Concat(actor.CharacterClass.DiscardedAbilityCards)
                    .Concat(actor.CharacterClass.RoundAbilityCards).Distinct()
                    .Where(card => card != null && !(actor.CharacterClass.RoundAbilityCards.Contains(card) &&
                        (card.SelectedAction?.CardPile == CBaseCard.ECardPile.Lost ||
                         card.SelectedAction?.CardPile == CBaseCard.ECardPile.PermanentlyLost))).ToList();
                if (futureCards.Count < 2)
                {
                    return best;
                }
                attacks = futureCards
                    .SelectMany(card => new[] { card.TopAction, card.BottomAction, card.DefaultAttackAction })
                    .Where(action => IsSupportedAction(action) && action.CardPile != CBaseCard.ECardPile.Lost &&
                        action.CardPile != CBaseCard.ECardPile.PermanentlyLost)
                    .SelectMany(action => action.Abilities.OfType<CAbilityAttack>()).Where(IsSupportedAttack)
                    .Distinct().ToList();
            }
            if (attacks.Count == 0 || attacks.Any(attack => BestCurrentAttackScore(actor, attack) > 0f))
            {
                return best;
            }

            int desiredRange = attacks.Max(attack => EffectiveAttackRange(actor, attack));
            var reachable = new HashSet<CTile>(destinations);
            var remainingCosts = new Dictionary<CTile, int>();
            int currentCost = int.MaxValue;
            foreach (CActor target in hostiles)
            {
                foreach (CTile goal in MoveDestinationCandidates(actor, desiredRange, target.ArrayIndex))
                {
                    if (!attacks.Any(attack => AttackTargetsFrom(actor, attack, goal.m_ArrayIndex,
                        EffectiveAttackRange(actor, attack)).Contains(target) &&
                        ScoreAttackTargetFrom(actor, target, attack, goal.m_ArrayIndex) > 0f))
                    {
                        continue;
                    }
                    bool foundPath;
                    List<Point> path = CActor.FindCharacterPath(actor, actor.ArrayIndex,
                        goal.m_ArrayIndex, move.Jump || move.Fly, ignoreMoveCost: false,
                        out foundPath, avoidTraps: true, move.IgnoreDifficultTerrain,
                        move.IgnoreHazardousTerrain, move.CarryOtherActorsOnHex);
                    if (!foundPath || !IsSafeMovePath(actor, move, path, goal))
                    {
                        continue;
                    }
                    int cost = CAbilityMove.CalculateMoveCost(path, !move.Fly, !move.Jump,
                        ignoreMoveCost: false, move.IgnoreDifficultTerrain, move.IgnoreBlockedTileMoveCost);
                    currentCost = Math.Min(currentCost, cost);
                    for (int index = 0; index < path.Count; index++)
                    {
                        CTile destination = TileAt(path[index]);
                        if (!reachable.Contains(destination) || !IsValidMoveEndpoint(actor, destination))
                        {
                            continue;
                        }
                        // Engine paths omit the origin. This suffix starts after the candidate,
                        // so the reward measures remaining legal route cost, not hex distance.
                        int remaining = CAbilityMove.CalculateMoveCost(path.Skip(index + 1).ToList(),
                            !move.Fly, !move.Jump, ignoreMoveCost: false, move.IgnoreDifficultTerrain,
                            move.IgnoreBlockedTileMoveCost);
                        if (!remainingCosts.TryGetValue(destination, out int previous) || remaining < previous)
                        {
                            remainingCosts[destination] = remaining;
                        }
                    }
                }
            }
            int bestRemainingCost = int.MaxValue;
            foreach (var candidate in remainingCosts)
            {
                CTile destination = candidate.Key;
                ThreatAt(actor, destination.m_ArrayIndex, null, out float damage);
                // Compare with the cheapest demonstrated attack route, not a longer side route
                // that could award "progress" for circling a ranged enemy indefinitely.
                float progress = SurvivalEvaluation.ApproachValue(currentCost, candidate.Value, damage,
                    actor.Health, actor.MaxHealth, cyclingCards);
                if (progress <= 0f)
                {
                    continue;
                }
                float value = PassivePositionValue(actor, destination.m_ArrayIndex, heals) + progress;
                if (recordCandidates)
                {
                    RecordSurvivalCandidate("move_candidate", actor, destination, "best_score", value);
                }
                if (value > score || value == score && bestRemainingCost != int.MaxValue && candidate.Value < bestRemainingCost)
                {
                    best = destination;
                    score = value;
                    bestRemainingCost = candidate.Value;
                }
            }
            return best;
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
                (attack.MiscAbilityData?.IgnorePreviousAbilityTargets?.Count ?? 0) == 0 &&
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
            if (attacker == null || target == null || attack == null)
            {
                return 0f;
            }
            float score = ScoreAttackTargetFrom(attacker, target, attack, attacker.ArrayIndex);
            if (DeveloperDiagnostics.Enabled)
            {
                DeveloperDiagnostics.Record("target_candidate", attacker, string.Format(CultureInfo.InvariantCulture,
                    "action=attack;score={0};x={1};y={2};targets=1", score, target.ArrayIndex.X, target.ArrayIndex.Y));
            }
            return score;
        }

        private static float ScoreAttackTargetFrom(CActor attacker, CActor target, CAbilityAttack attack,
            Point position)
        {
            int shield = Math.Max(0, target.CalculateShield(attack) - attack.Pierce);
            int damage = Math.Max(0, attack.Strength - shield);
            bool rangedAdjacent = EffectiveAttackRange(attacker, attack) > 1 &&
                ScenarioManager.GetTileDistance(position.X, position.Y,
                    target.ArrayIndex.X, target.ArrayIndex.Y) == 1;
            return TacticalEvaluation.AttackValue(damage, target.Health, target.ActorActionHasHappened,
                    rangedAdjacent) + ScoreConditions(attack, target,
                    TacticalEvaluation.IsProjectedKill(damage, target.Health, rangedAdjacent));
        }

        internal static float ScoreHealTarget(CActor target, CAbilityHeal heal)
        {
            return ScoreHealTarget(target, heal?.ModifiedStrength() ?? 0);
        }

        private static float ScorePair(CPlayerActor actor, CAbilityCard first, CAbilityCard second,
            Dictionary<Tuple<CPlayerActor, int, int>, List<CTile>> reachable)
        {
            List<TurnPlan> sequences = EvaluatePlans(actor, first, second, reachable);
            if (sequences.Count == 0)
            {
                return float.MinValue;
            }
            float orientationOne = sequences
                .Where(plan => (plan.First.IsTop ? plan.First.Card : plan.Followup.Card) == first)
                .Select(plan => plan.Score).DefaultIfEmpty(0f).Max();
            float orientationTwo = sequences
                .Where(plan => (plan.First.IsTop ? plan.First.Card : plan.Followup.Card) == second)
                .Select(plan => plan.Score).DefaultIfEmpty(0f).Max();
            float flexibility = Math.Max(0f, Math.Min(orientationOne, orientationTwo)) * 0.18f;
            float initiativeCoverage = Math.Abs(first.Initiative - second.Initiative) >= 25 ? 0.75f : 0f;
            float score = sequences.Max(sequence => sequence.Score) + flexibility + initiativeCoverage;
            if (DeveloperDiagnostics.Enabled)
            {
                DeveloperDiagnostics.Record("pair_candidate", actor, string.Format(CultureInfo.InvariantCulture,
                    "action=cards;score={0};first_card_id={1};second_card_id={2}", score, first.ID, second.ID));
            }
            return score;
        }

        private static List<TurnPlan> EvaluatePlans(CPlayerActor actor, CAbilityCard first,
            CAbilityCard second, Dictionary<Tuple<CPlayerActor, int, int>, List<CTile>> reachable = null,
            bool recordCandidates = false)
        {
            List<TurnPlan> plans = new List<TurnPlan>();
            reachable = reachable ?? new Dictionary<Tuple<CPlayerActor, int, int>, List<CTile>>();
            foreach (bool firstIsTop in new[] { true, false })
            {
                List<PlannedAction> firstOptions = HalfOptions(actor, first, firstIsTop).ToList();
                List<PlannedAction> secondOptions = HalfOptions(actor, second, !firstIsTop).ToList();
                foreach (PlannedAction firstOption in firstOptions)
                {
                    foreach (PlannedAction secondOption in secondOptions)
                    {
                        foreach (bool firstActsFirst in new[] { true, false })
                        {
                            PlannedAction[] sequence = BuildOrderedSequence(actor,
                                firstActsFirst ? firstOption : secondOption,
                                firstActsFirst ? secondOption : firstOption);
                            float score = ScoreActionOrder(actor, sequence[0], sequence[1], reachable,
                                out float attackValue);
                            if (score == float.MinValue)
                            {
                                continue;
                            }
                            if (recordCandidates)
                            {
                                RecordActionCandidate(actor, sequence[0], sequence[1], score);
                            }
                            plans.Add(new TurnPlan
                            {
                                First = sequence[0],
                                Followup = sequence[1],
                                Score = score,
                                AttackValue = attackValue
                            });
                        }
                    }
                }
            }
            return plans;
        }

        private static void RecordActionCandidate(CPlayerActor actor, PlannedAction first,
            PlannedAction second, float score)
        {
            if (!DeveloperDiagnostics.Enabled)
            {
                return;
            }
            string action = first.Action.Abilities[0] is CAbilityAttack ? "attack" :
                first.Action.Abilities[0] is CAbilityHeal ? "heal" :
                first.Action.Abilities[0] is CAbilityRecoverLostCards ? "recover" : "move";
            DeveloperDiagnostics.Record("action_candidate", actor, string.Format(CultureInfo.InvariantCulture,
                "action={0};score={1};card_id={2};first_card_id={2};second_card_id={3};top_card_id={4};" +
                "bottom_card_id={5};default_action={6};initiative={7}", action, score, first.Card.ID,
                second?.Card.ID ?? -1, first.IsTop ? first.Card.ID : second?.Card.ID ?? -1,
                first.IsTop ? second?.Card.ID ?? -1 : first.Card.ID,
                first.ActionType == CBaseCard.ActionType.DefaultAttackAction ||
                    first.ActionType == CBaseCard.ActionType.DefaultMoveAction ? 1 : 0, first.Card.Initiative));
        }

        private static float ScoreActionOrder(CPlayerActor actor, PlannedAction first,
            PlannedAction second, Dictionary<Tuple<CPlayerActor, int, int>, List<CTile>> reachable,
            out float attackValue)
        {
            float score = first.Score + (second?.Score ?? 0f);
            attackValue = 0f;
            List<CAbility> abilities = new[] { first, second }.Where(value => value != null)
                .SelectMany(action => action.Action.Abilities).ToList();
            bool movedBeforeRecovery = false;
            int recoveries = 0;
            int moves = 0;
            foreach (CAbility ability in abilities)
            {
                movedBeforeRecovery |= ability is CAbilityMove;
                if (ability is CAbilityMove && ++moves > 1)
                {
                    // No composed route forecast exists for successive moves.
                    return float.MinValue;
                }
                if (ability is CAbilityRecoverLostCards recovery &&
                    (++recoveries > 1 || movedBeforeRecovery &&
                        !recovery.AbilityFilter.HasTargetTypeFlag(CAbilityFilter.EFilterTargetType.Self, exclusive: true)))
                {
                    // Do not double-count the same lost pool or forecast ally recovery from a stale position.
                    return float.MinValue;
                }
            }
            bool afterMove = false;
            for (int index = 0; index < abilities.Count; index++)
            {
                if (abilities[index] is CAbilityMove move)
                {
                    afterMove = true;
                    List<CAbilityAttack> attacks = AttackSegment(abilities.Skip(index + 1));
                    if (attacks.Count == 0)
                    {
                        List<CAbilityHeal> heals = abilities.Skip(index + 1).OfType<CAbilityHeal>()
                            .Where(IsSupportedHeal).ToList();
                        if (HostileActors(actor).Count > 0 || heals.Count > 0)
                        {
                            List<CTile> retreats = ReachableMoveDestinations(actor, move,
                                PlannedMoveStrength(actor, move), reachable);
                            ChooseApproachOrRetreat(actor, move, attacks, retreats, heals, false, out float retreatValue);
                            score += retreatValue - PassivePositionValue(actor, actor.ArrayIndex, heals);
                        }
                        continue;
                    }
                    int budget = PlannedMoveStrength(actor, move);
                    if (attacks.Any(attack => IsSupportedAttack(attack) && attack.Strength > 0) &&
                        HostileActors(actor).Count == 0 && HasRevealedClosedDoor())
                    {
                        // Reserve an attack after the opener's move even before new targets are known.
                        // Otherwise trivial printed utility can win forever over a blank default attack.
                        score += 4f;
                    }
                    // A target beyond this move can still justify demonstrated multi-turn progress.
                    List<CTile> destinations = HostileActors(actor).Count > 0
                        ? ReachableMoveDestinations(actor, move, budget, reachable) : new List<CTile>();
                    float immediate = attacks.Sum(attack => BestCurrentAttackScore(actor, attack));
                    // Use the same endpoint objective and tie-breaking as actual movement.
                    float endpointScore;
                    float endpointAttackValue = 0f;
                    if (new[] { TileAt(actor.ArrayIndex) }.Concat(destinations).Any(tile => tile != null &&
                        attacks.Any(attack => HasAttackTargetFrom(actor, attack, tile.m_ArrayIndex,
                            EffectiveAttackRange(actor, attack)))))
                    {
                        ChooseAttackEndpoint(actor, attacks, destinations, false, out endpointScore, out endpointAttackValue);
                    }
                    else
                    {
                        List<CAbilityHeal> heals = abilities.Skip(index + 1).OfType<CAbilityHeal>()
                            .Where(IsSupportedHeal).ToList();
                        ChooseApproachOrRetreat(actor, move, attacks, destinations, heals, false, out endpointScore);
                    }
                    score += endpointScore - immediate;
                    attackValue += endpointAttackValue;
                }
                else if (!afterMove && abilities[index] is CAbilityAttack attack)
                {
                    float immediate = BestCurrentAttackScore(actor, attack);
                    score += immediate * 0.1f;
                    attackValue += immediate;
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
                followup = new PlannedAction
                {
                    Card = second.Card,
                    ActionType = second.ActionType,
                    Action = second.Action,
                    Score = second.Score
                };
                PenalizeRoutineHeal(actor, followup, canForecast ? first.FirstHeal : null, !canForecast);
            }
            return new[] { first, followup };
        }

        private static IEnumerable<PlannedAction> HalfOptions(CPlayerActor actor, CAbilityCard card, bool top)
        {
            CBaseCard.ActionType printedType = top
                ? CBaseCard.ActionType.TopAction
                : CBaseCard.ActionType.BottomAction;
            CBaseCard.ActionType defaultType = top
                ? CBaseCard.ActionType.DefaultAttackAction
                : CBaseCard.ActionType.DefaultMoveAction;
            PlannedAction printed = BuildOption(actor, card, printedType);
            PlannedAction fallback = BuildOption(actor, card, defaultType);
            if (fallback != null)
            {
                yield return fallback;
            }
            if (printed != null)
            {
                yield return printed;
            }
        }

        private static PlannedAction BuildOption(CPlayerActor actor, CAbilityCard card,
            CBaseCard.ActionType actionType)
        {
            CAction action = card?.GetActionForType(actionType);
            if (!IsSupportedAction(action) || action.Abilities.OfType<CAbilityRecoverLostCards>()
                .Any(recovery => RecoveryPlanner.RecoverableCount(actor, recovery) <= 0))
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
                action.Augmentations != null && action.Augmentations.Count > 0)
            {
                return false;
            }
            if (action.CardPile == CBaseCard.ECardPile.PermanentlyLost)
            {
                return action.Abilities.Count == 1 &&
                    (action.Infusions == null || action.Infusions.All(element =>
                        element != ElementInfusionBoardManager.EElement.Any &&
                        Enum.IsDefined(typeof(ElementInfusionBoardManager.EElement), element))) &&
                    action.Abilities[0] is CAbilityRecoverLostCards recovery && RecoveryPlanner.IsSupported(recovery);
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
                    score += BestCurrentAttackScore(actor, attack);
                }
                else if (ability is CAbilityHeal heal)
                {
                    score += ScoreHeal(actor, heal);
                }
                else if (ability is CAbilityRecoverLostCards recovery)
                {
                    score += RecoveryPlanner.Score(actor, recovery);
                }
            }

            if (action.CardPile == CBaseCard.ECardPile.Lost)
            {
                int cyclingCards = actor.CharacterClass.HandAbilityCards.Count +
                    actor.CharacterClass.DiscardedAbilityCards.Count +
                    actor.CharacterClass.RoundAbilityCards.Count;
                score -= TacticalEvaluation.LossPenalty(cyclingCards);
            }
            return score;
        }

        private static float BestCurrentAttackScore(CPlayerActor actor, CAbilityAttack attack)
        {
            if (attack == null)
            {
                return 0f;
            }
            return BestAttackScoreFrom(actor, attack, actor.ArrayIndex);
        }

        private static float BestAttackScoreFrom(CPlayerActor actor, CAbilityAttack attack, Point position)
        {
            return AttackTargetsFrom(actor, attack, position, EffectiveAttackRange(actor, attack))
                .Select(target => ScoreAttackTargetFrom(actor, target, attack, position))
                .DefaultIfEmpty(0f).Max();
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

        private static float ScoreConditions(CAbilityAttack attack, CActor target, bool projectedKill)
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
                float value;
                switch (condition)
                {
                    case CCondition.ENegativeCondition.Stun:
                        value = TacticalEvaluation.ControlConditionValue(12f, target.ActorActionHasHappened);
                        break;
                    case CCondition.ENegativeCondition.Disarm:
                        value = TacticalEvaluation.ControlConditionValue(9f, target.ActorActionHasHappened);
                        break;
                    case CCondition.ENegativeCondition.Sleep:
                        value = TacticalEvaluation.ControlConditionValue(10f, target.ActorActionHasHappened);
                        break;
                    case CCondition.ENegativeCondition.Immobilize:
                        value = TacticalEvaluation.ControlConditionValue(5f, target.ActorActionHasHappened);
                        break;
                    case CCondition.ENegativeCondition.Poison:
                    case CCondition.ENegativeCondition.Wound:
                        value = 4f;
                        break;
                    case CCondition.ENegativeCondition.Muddle:
                    case CCondition.ENegativeCondition.Curse:
                        value = 3f;
                        break;
                    default:
                        value = 1f;
                        break;
                }
                // Curse affects the shared modifier deck even if this target dies.
                score += TacticalEvaluation.ConditionValue(value, projectedKill,
                    condition == CCondition.ENegativeCondition.Curse);
            }
            return score;
        }

        private static float ScoreFutureCard(CPlayerActor actor, CAbilityCard card)
        {
            float score = TacticalEvaluation.FutureCardValue(ScoreIntrinsicAction(card.TopAction),
                ScoreIntrinsicAction(card.BottomAction), card.Initiative) +
                RecoveryPlanner.RetentionValue(actor, card);
            List<CAbilityCard> others = actor.CharacterClass.HandAbilityCards
                .Concat(actor.CharacterClass.DiscardedAbilityCards)
                .Concat(actor.CharacterClass.RoundAbilityCards)
                .Concat(actor.CharacterClass.ExtraTurnCards)
                .Where(other => other != card).Distinct().ToList();
            foreach (CAbility.EAbilityType role in new[] { CAbility.EAbilityType.Attack,
                CAbility.EAbilityType.Move, CAbility.EAbilityType.Heal })
            {
                if (HasReusableRole(card, role))
                {
                    score += TacticalEvaluation.RoleScarcityValue(
                        others.Count(other => HasReusableRole(other, role)));
                }
            }
            return score;
        }

        private static bool HasReusableRole(CAbilityCard card, CAbility.EAbilityType role)
        {
            return new[] { card.TopAction, card.BottomAction }.Any(action => action?.Abilities != null &&
                action.CardPile != CBaseCard.ECardPile.Lost &&
                action.CardPile != CBaseCard.ECardPile.PermanentlyLost && action.Abilities.Any(ability =>
                    role == CAbility.EAbilityType.Move
                        ? ability is CAbilityMove move && PrintedStrength(move, EEnhancement.PlusMove) >= 3
                        : role == CAbility.EAbilityType.Attack
                            ? ability is CAbilityAttack attack && PrintedStrength(attack, EEnhancement.PlusAttack) >= 3
                            : ability is CAbilityHeal heal && PrintedStrength(heal, EEnhancement.PlusHeal) > 0));
        }

        private static float ScoreIntrinsicAction(CAction action)
        {
            if (action?.Abilities == null)
            {
                return 0f;
            }
            float score = 0f;
            foreach (CAbility ability in action.Abilities)
            {
                if (ability is CAbilityAttack attack)
                {
                    score += Math.Max(0, PrintedStrength(attack, EEnhancement.PlusAttack)) * 3f *
                        Math.Max(1, Math.Min(3, attack.NumberTargets));
                    score += Math.Max(0, attack.Range - 1) * 0.5f + Math.Max(0, attack.Pierce) * 0.5f;
                    score += (attack.NegativeConditions?.Count ?? 0) * 2f;
                }
                else if (ability is CAbilityMove move)
                {
                    score += Math.Max(0, PrintedStrength(move, EEnhancement.PlusMove)) * 1.6f +
                        (move.Jump || move.Fly ? 1.5f : 0f);
                }
                else if (ability is CAbilityHeal heal)
                {
                    score += Math.Max(0, PrintedStrength(heal, EEnhancement.PlusHeal)) * 1.25f *
                        Math.Max(1, Math.Min(3, heal.NumberTargets)) + 2f;
                }
                else
                {
                    // Unsupported printed utility is not worthless just because we cannot automate it.
                    score += 3f;
                }
            }
            return score * (action.CardPile == CBaseCard.ECardPile.Lost ||
                action.CardPile == CBaseCard.ECardPile.PermanentlyLost ? 0.5f : 1f);
        }

        private static bool HasAttackTargetFrom(CPlayerActor actor, CAbilityAttack attack,
            Point position, int range)
        {
            return AttackTargetsFrom(actor, attack, position, range).Any();
        }

        private static IEnumerable<CActor> AttackTargetsFrom(CPlayerActor actor, CAbilityAttack attack,
            Point position, int range)
        {
            CTile sourceTile = TileAt(position);
            if (actor == null || !IsSupportedAttack(attack) || !IsValidMoveEndpoint(actor, sourceTile) ||
                actor.Tokens.HasKey(CCondition.ENegativeCondition.Disarm) ||
                actor.Tokens.HasKey(CCondition.ENegativeCondition.Stun) ||
                actor.Tokens.HasKey(CCondition.ENegativeCondition.Sleep) ||
                // Non-target filters can depend on the attacker's real position; do not fake that state.
                position != actor.ArrayIndex && attack.AbilityFilter.HasNonTargetTypeFilters())
            {
                yield break;
            }
            foreach (CActor target in HostileActors(actor))
            {
                if (!IsValidTarget(actor, target, attack) || attack.ActorsToIgnore?.Contains(target) == true ||
                    ScenarioManager.GetTileDistance(position.X, position.Y,
                        target.ArrayIndex.X, target.ArrayIndex.Y) > range)
                {
                    continue;
                }
                CTile targetTile = TileAt(target.ArrayIndex);
                if (targetTile != null &&
                    (targetTile.m_HexMap?.Revealed == true || targetTile.m_Hex2Map?.Revealed == true) &&
                    CActor.HaveLOS(sourceTile, targetTile))
                {
                    yield return target;
                }
            }
        }

        private static CTile TileAt(Point position)
        {
            CTile[,] tiles = ScenarioManager.Tiles;
            return tiles != null && position.X >= 0 && position.Y >= 0 &&
                position.X < tiles.GetLength(0) && position.Y < tiles.GetLength(1)
                ? tiles[position.X, position.Y] : null;
        }

        private static bool IsValidMoveEndpoint(CPlayerActor actor, CTile tile, bool allowClosedDoor = false)
        {
            if (actor == null || tile == null || ScenarioManager.Scenario == null ||
                ScenarioManager.PathFinder?.Nodes == null ||
                tile.m_HexMap?.Revealed != true && tile.m_Hex2Map?.Revealed != true)
            {
                return false;
            }
            Point position = tile.m_ArrayIndex;
            if (TileAt(position) != tile || position.X >= ScenarioManager.PathFinder.Nodes.GetLength(0) ||
                position.Y >= ScenarioManager.PathFinder.Nodes.GetLength(1))
            {
                return false;
            }
            var node = ScenarioManager.PathFinder.Nodes[position.X, position.Y];
            if (node == null || !node.Walkable || node.Blocked ||
                ScenarioManager.Scenario.FindActorsAt(position).Any(other => other != actor))
            {
                return false;
            }
            CObjectDoor door = tile.FindProp(ScenarioManager.ObjectImportType.Door) as CObjectDoor;
            return door == null || door.DoorIsOpen || allowClosedDoor && !door.DoorIsLocked &&
                !door.IsDungeonEntrance && !door.IsDungeonExit &&
                !(door.PropHealthDetails != null && door.PropHealthDetails.HasHealth &&
                    door.PropHealthDetails.CurrentHealth > 0);
        }

        private static IEnumerable<CTile> MoveDestinationCandidates(CPlayerActor actor, int budget,
            Point? center = null)
        {
            if (actor == null || budget <= 0 || TileAt(actor.ArrayIndex) == null ||
                ScenarioManager.Scenario == null || ScenarioManager.PathFinder?.Nodes == null)
            {
                return Enumerable.Empty<CTile>();
            }
            Point origin = center ?? actor.ArrayIndex;
            if (TileAt(origin) == null)
            {
                return Enumerable.Empty<CTile>();
            }
            // This overload can skip the game's redundant path/LOS prefilter. Endpoint and actual
            // actor-specific path validation happen below, including the real hex-radius bound.
            int radius = Math.Min(budget, Math.Max(ScenarioManager.Width, ScenarioManager.Height));
            return GameState.GetTilesInRange(origin, radius, CAbility.EAbilityTargeting.Range,
                    emptyTilesOnly: false, ignorePathLength: true, ignoreLOS: true,
                    allowClosedDoorTiles: true, skipPathingCheck: true)
                .Where(tile => IsValidMoveEndpoint(actor, tile, allowClosedDoor: true) &&
                    tile.m_ArrayIndex != actor.ArrayIndex &&
                    ScenarioManager.GetTileDistance(origin.X, origin.Y,
                        tile.m_ArrayIndex.X, tile.m_ArrayIndex.Y) <= budget);
        }

        private static bool HasPotentialAttackTarget(CPlayerActor actor, List<CAbilityAttack> attacks, int movement)
        {
            if (actor == null || attacks.Count == 0 ||
                actor.Tokens.HasKey(CCondition.ENegativeCondition.Disarm) ||
                actor.Tokens.HasKey(CCondition.ENegativeCondition.Stun) ||
                actor.Tokens.HasKey(CCondition.ENegativeCondition.Sleep))
            {
                return false;
            }
            return HostileActors(actor).Any(target =>
            {
                CTile tile = TileAt(target.ArrayIndex);
                return tile != null && (tile.m_HexMap?.Revealed == true || tile.m_Hex2Map?.Revealed == true) &&
                    attacks.Any(attack => IsSupportedAttack(attack) && IsValidTarget(actor, target, attack) &&
                        attack.ActorsToIgnore?.Contains(target) != true &&
                        Distance(actor, target) <= (long)Math.Max(0, movement) + EffectiveAttackRange(actor, attack));
            });
        }

        private static List<CTile> ReachableMoveDestinations(CPlayerActor actor, CAbilityMove move, int budget,
            Dictionary<Tuple<CPlayerActor, int, int>, List<CTile>> cache = null)
        {
            if (!IsSupportedMove(move) || actor == null ||
                actor.Tokens.HasKey(CCondition.ENegativeCondition.Immobilize))
            {
                return new List<CTile>();
            }
            // Actor state and origin are fixed for this invocation. Share equivalent printed/default
            // moves only after resolving their budgets, including ability-specific active bonuses.
            var key = Tuple.Create(actor, budget, TacticalEvaluation.MovementRulesKey(move.Jump, move.Fly,
                move.IgnoreDifficultTerrain, move.IgnoreHazardousTerrain, move.IgnoreBlockedTileMoveCost,
                move.CarryOtherActorsOnHex));
            if (cache != null && cache.TryGetValue(key, out List<CTile> cached))
            {
                return cached;
            }
            List<CTile> destinations = MoveDestinationCandidates(actor, budget)
                .Where(tile => MovePathCost(actor, move, tile, budget) <= budget).ToList();
            if (cache != null)
            {
                cache[key] = destinations;
            }
            return destinations;
        }

        private static int MovePathCost(CPlayerActor actor, CAbilityMove move, CTile destination,
            int? budget = null)
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
            if (foundPath && cost <= (budget ?? move.RemainingMoves))
            {
                return IsSafeMovePath(actor, move, path, destination)
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
            return foundPath && IsSafeMovePath(actor, move, path, destination)
                ? cost
                : int.MaxValue;
        }

        private static bool IsSafeMovePath(CPlayerActor actor, CAbilityMove move, List<Point> path,
            CTile destination)
        {
            if (!IsValidMoveEndpoint(actor, destination, allowClosedDoor: true) || move == null ||
                path == null || path.Count == 0 || path[path.Count - 1] != destination.m_ArrayIndex)
            {
                return false;
            }
            for (int index = 0; index < path.Count; index++)
            {
                CTile tile = TileAt(path[index]);
                if (tile == null || tile.m_HexMap?.Revealed != true && tile.m_Hex2Map?.Revealed != true)
                {
                    return false;
                }
                CObjectDoor door = tile.FindProp(ScenarioManager.ObjectImportType.Door) as CObjectDoor;
                if (index < path.Count - 1 && door != null && !door.DoorIsOpen)
                {
                    return false;
                }
                bool landing = index == path.Count - 1;
                foreach (CObjectProp prop in tile.m_Props)
                {
                    if (prop == null)
                    {
                        continue;
                    }
                    // Portals change the endpoint; pressure-plate consequences are not modeled.
                    if (landing && (prop.ObjectType == ScenarioManager.ObjectImportType.Portal ||
                        prop.ObjectType == ScenarioManager.ObjectImportType.PressurePlate))
                    {
                        return false;
                    }
                    if (prop.Activated)
                    {
                        continue;
                    }
                    bool activates = prop is CObjectDoor || !move.Fly && (!move.Jump || landing);
                    if (activates && !(move.IgnoreHazardousTerrain && prop is CObjectHazardousTerrain) &&
                        (prop.WillActivationDamageActor(actor) ||
                            prop is CObjectTrap trap && trap.Conditions.Count > 0))
                    {
                        return false;
                    }
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
                CTile doorTile = TileAt(new Point(door.ArrayIndex.X, door.ArrayIndex.Y));
                if (!IsRevealed(doorTile) || door.DoorIsOpen || door.DoorIsLocked || door.IsDungeonEntrance || door.IsDungeonExit ||
                    door.PropHealthDetails != null && door.PropHealthDetails.HasHealth &&
                    door.PropHealthDetails.CurrentHealth > 0)
                {
                    continue;
                }
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
                bool ready = IsPartyReadyForDoor(actor, doorTile);
                CTile destination = ready && doorCost <= move.RemainingMoves
                    ? doorTile
                    : ReachablePathEndpoint(actor, move, ready ? doorPath :
                        doorPath.Take(doorPath.Count - 1).ToList(), legalDestinations);
                RecordSurvivalCandidate("door_decision", actor, doorTile,
                    ready ? "door_ready" : "party_not_ready", 0f);
                if (destination != null)
                {
                    bestDoorCost = doorCost;
                    bestDestination = destination;
                }
            }
            return bestDestination;
        }

        private static bool IsPartyReadyForDoor(CPlayerActor actor, CTile door)
        {
            bool nearbyEnemies = HostileActors(actor).Any(enemy => Distance(actor, enemy) <= 6 ||
                ScenarioManager.GetTileDistance(door.m_ArrayIndex.X, door.m_ArrayIndex.Y,
                    enemy.ArrayIndex.X, enemy.ArrayIndex.Y) <= 6);
            List<CPlayerActor> party = KnownParty(actor).ToList();
            bool needsRest = party.Any(ally => ally.CharacterClass.LongRest || ally != actor &&
                ally.CharacterClass.HandAbilityCards.Count + ally.CharacterClass.RoundAbilityCards.Count < 2);
            bool scattered = party.Any(ally => ScenarioManager.GetTileDistance(door.m_ArrayIndex.X,
                door.m_ArrayIndex.Y, ally.ArrayIndex.X, ally.ArrayIndex.Y) > 6 ||
                ally != actor && Distance(actor, ally) > 3 && !CActor.HaveLOS(TileAt(actor.ArrayIndex), TileAt(ally.ArrayIndex)));
            // An unrevealed room cannot provide a concrete target yet. A committed ordinary attack
            // after this move is sufficient readiness, unlike an end-of-turn exploratory move.
            bool usefulAction = GetFollowupAttacks(actor).Any(attack =>
                IsSupportedAttack(attack) && attack.Strength > 0);
            bool disabled = actor.Tokens.HasKey(CCondition.ENegativeCondition.Stun) ||
                actor.Tokens.HasKey(CCondition.ENegativeCondition.Sleep) ||
                actor.Tokens.HasKey(CCondition.ENegativeCondition.Disarm);
            return SurvivalEvaluation.ShouldOpenDoor(nearbyEnemies, needsRest, scattered, usefulAction, disabled);
        }

        private static bool HasRevealedClosedDoor()
        {
            return ScenarioManager.CurrentScenarioState?.DoorProps?.OfType<CObjectDoor>().Any(door =>
                IsRevealed(TileAt(new Point(door.ArrayIndex.X, door.ArrayIndex.Y))) &&
                !door.DoorIsOpen && !door.DoorIsLocked && !door.IsDungeonEntrance && !door.IsDungeonExit &&
                !(door.PropHealthDetails != null && door.PropHealthDetails.HasHealth &&
                    door.PropHealthDetails.CurrentHealth > 0)) == true;
        }

        private static bool IsRevealed(CTile tile)
        {
            return tile != null && (tile.m_HexMap?.Revealed == true || tile.m_Hex2Map?.Revealed == true);
        }

        private static IEnumerable<CPlayerActor> KnownParty(CPlayerActor actor)
        {
            return ScenarioManager.Scenario.PlayerActors.Where(ally => ally != null && !ally.IsDead &&
                !ally.Deactivated && !ally.PhasedOut && CActor.AreActorsAllied(actor.Type, ally.Type) &&
                IsRevealed(TileAt(ally.ArrayIndex)));
        }

        internal static bool IsUnderThreat(CPlayerActor actor)
        {
            if (actor == null || actor.IsDead || !IsRevealed(TileAt(actor.ArrayIndex)))
            {
                return false;
            }
            float exposure = ThreatAt(actor, actor.ArrayIndex, null, out float damage);
            return SurvivalEvaluation.IsUnderThreat(damage, exposure);
        }

        internal static float ThreatAt(CPlayerActor actor, Point position)
        {
            return ThreatAt(actor, position, null, out _);
        }

        private static float ThreatAt(CPlayerActor actor, Point position, HashSet<CActor> projectedKills,
            out float futureDamage)
        {
            futureDamage = 0f;
            CTile tile = TileAt(position);
            if (actor == null || !IsRevealed(tile))
            {
                return 0f;
            }
            foreach (CEnemyActor enemy in HostileActors(actor).OfType<CEnemyActor>())
            {
                CMonsterClass monster = enemy.MonsterClass;
                if (monster == null)
                {
                    continue;
                }
                int distance = ScenarioManager.GetTileDistance(position.X, position.Y,
                    enemy.ArrayIndex.X, enemy.ArrayIndex.Y);
                bool los = CActor.HaveLOS(tile, TileAt(enemy.ArrayIndex));
                bool disabled = enemy.Tokens.HasKey(CCondition.ENegativeCondition.Stun) ||
                    enemy.Tokens.HasKey(CCondition.ENegativeCondition.Sleep) ||
                    enemy.Tokens.HasKey(CCondition.ENegativeCondition.Disarm);
                bool immobilized = enemy.Tokens.HasKey(CCondition.ENegativeCondition.Immobilize);
                float damage;
                CPhase.PhaseType phase = PhaseManager.PhaseType;
                bool revealedIntent = !enemy.ActorActionHasHappened &&
                    (phase == CPhase.PhaseType.StartTurn || phase == CPhase.PhaseType.ActionSelection ||
                     phase == CPhase.PhaseType.Action || phase == CPhase.PhaseType.EndTurn);
                CAction intent = revealedIntent ? monster.RoundAbilityCard?.Action : null;
                // Never clone/start an enemy ability: those helpers can recalculate board-dependent stats.
                bool plainIntent = intent?.Abilities != null &&
                    (intent.Augmentations == null || intent.Augmentations.Count == 0) &&
                    (monster.StatIsBasedOnXEntries == null || monster.StatIsBasedOnXEntries.Count == 0) &&
                    intent.Abilities.All(ability => IsSimpleAbility(ability) && !ability.OnDeath &&
                        !ability.UseSpecialBaseStat && !ability.AddAttackBaseStat &&
                        ability.Strength != int.MaxValue && ability.Range != int.MaxValue &&
                        ability.AreaEffect == null && !ability.AllTargetsOnMovePath &&
                        !ability.AllTargetsOnAttackPath &&
                        (ability.StartAbilityRequirements == null ||
                         ability.StartAbilityRequirements.StartAbilityRequirementType ==
                            CAbilityRequirements.EStartAbilityRequirementType.None) &&
                        (ability is CAbilityAttack || ability is CAbilityMove || ability is CAbilityHeal));
                if (plainIntent)
                {
                    int movement = 0;
                    damage = 0f;
                    foreach (CAbility ability in intent.Abilities)
                    {
                        if (ability is CAbilityMove)
                        {
                            movement += Math.Max(0, ability.Strength + (ability.StrengthIsBase ? 0 : monster.Move));
                        }
                        else if (ability is CAbilityAttack)
                        {
                            int range = ability.RangeIsBase ? ability.Range : monster.Range + ability.Range;
                            int strength = Math.Max(0, ability.Strength + (ability.StrengthIsBase ? 0 : monster.Attack));
                            damage += SurvivalEvaluation.EnemyDamage(distance, los, movement, range,
                                strength, disabled, immobilized, false);
                        }
                    }
                }
                else
                {
                    // An already-used card says nothing about next round. Use visible base stats instead.
                    damage = SurvivalEvaluation.EnemyDamage(distance, los, monster.Move, monster.Range,
                        monster.Attack, disabled, immobilized, enemy.ActorActionHasHappened);
                }
                // Projected kills still carry modifier-deck uncertainty.
                futureDamage += damage * (projectedKills?.Contains(enemy) == true ? 0.25f : 1f);
            }
            int cards = actor.CharacterClass.HandAbilityCards.Count + actor.CharacterClass.DiscardedAbilityCards.Count +
                actor.CharacterClass.RoundAbilityCards.Count;
            return SurvivalEvaluation.ExposurePenalty(futureDamage, actor.Health, actor.MaxHealth, cards);
        }

        private static float PassivePositionValue(CPlayerActor actor, Point position, List<CAbilityHeal> heals)
        {
            CTile tile = TileAt(position);
            float support = 0f;
            List<CPlayerActor> allies = KnownParty(actor).Where(ally => ally != actor).ToList();
            if (allies.Count > 0)
            {
                int nearest = allies.Min(ally => ScenarioManager.GetTileDistance(position.X, position.Y,
                    ally.ArrayIndex.X, ally.ArrayIndex.Y));
                support -= Math.Min(12, Math.Max(0, nearest - 3)) * 0.5f;
            }
            foreach (CAbilityHeal heal in heals)
            {
                int range = EnhancedValue(heal, heal.Range, EEnhancement.PlusRange);
                support += allies.Where(ally => heal.AbilityFilter.IsValidTarget(ally, actor, heal.IsTargetedAbility,
                        useTargetOriginalType: false, heal.MiscAbilityData?.CanTargetInvisible) &&
                        ScenarioManager.GetTileDistance(position.X, position.Y, ally.ArrayIndex.X, ally.ArrayIndex.Y) <= range &&
                        CActor.HaveLOS(tile, TileAt(ally.ArrayIndex)))
                    .Select(ally => ScoreHealTarget(ally, EffectiveHealStrength(actor, heal)))
                    .OrderByDescending(value => value).Take(Math.Max(1, heal.NumberTargets)).Sum();
            }
            int distance = ScenarioManager.GetTileDistance(actor.ArrayIndex.X, actor.ArrayIndex.Y, position.X, position.Y);
            return SurvivalEvaluation.PositionValue(ThreatAt(actor, position), support, distance);
        }

        private static CTile ChooseSurvivalEndpoint(CPlayerActor actor, List<CTile> destinations,
            List<CAbilityHeal> heals, bool recordCandidates, out float score)
        {
            CTile best = TileAt(actor.ArrayIndex);
            score = PassivePositionValue(actor, actor.ArrayIndex, heals);
            float currentThreat = ThreatAt(actor, actor.ArrayIndex);
            foreach (CTile tile in destinations)
            {
                if (!IsValidMoveEndpoint(actor, tile))
                {
                    continue;
                }
                float value = PassivePositionValue(actor, tile.m_ArrayIndex, heals);
                if (recordCandidates)
                {
                    RecordSurvivalCandidate("retreat_candidate", actor, tile, "retreat", value);
                }
                // Healing/support can justify some exposure; empty movement cannot.
                if (value > score && (heals.Count > 0 || ThreatAt(actor, tile.m_ArrayIndex) <= currentThreat))
                {
                    score = value;
                    best = tile;
                }
            }
            if (recordCandidates)
            {
                RecordSurvivalCandidate("move_candidate", actor, best,
                    best.m_ArrayIndex == actor.ArrayIndex ? "no_action" : "retreat", score);
            }
            return best;
        }

        private static void RecordSurvivalCandidate(string kind, CPlayerActor actor, CTile tile,
            string reason, float score)
        {
            if (!DeveloperDiagnostics.Enabled || tile == null)
            {
                return;
            }
            float threat = ThreatAt(actor, tile.m_ArrayIndex, null, out float damage);
            DeveloperDiagnostics.Record(kind, actor, string.Format(CultureInfo.InvariantCulture,
                "action=move;reason={0};x={1};y={2};score={3};threats={4};future_damage={5}",
                reason, tile.m_ArrayIndex.X, tile.m_ArrayIndex.Y, score, threat, damage));
        }

        private static CTile ReachablePathEndpoint(CPlayerActor actor, CAbilityMove move, List<Point> path,
            List<CTile> legalDestinations)
        {
            // Door/approach objectives do not need a full-board reachable-tile enumeration.
            return path.AsEnumerable().Reverse().Select(TileAt).FirstOrDefault(tile =>
                IsValidMoveEndpoint(actor, tile, allowClosedDoor: true) &&
                ScenarioManager.GetTileDistance(actor.ArrayIndex.X, actor.ArrayIndex.Y,
                    tile.m_ArrayIndex.X, tile.m_ArrayIndex.Y) <= move.RemainingMoves &&
                (legalDestinations != null ? legalDestinations.Contains(tile) :
                    MovePathCost(actor, move, tile) <= move.RemainingMoves));
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
            if (!foundPath || !IsSafeMovePath(actor, move, path, doorTile))
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

        private static CTile ChooseAttackEndpoint(CPlayerActor actor, List<CAbilityAttack> attacks,
            List<CTile> destinations, bool recordCandidates, out float score, out float attackValue)
        {
            CTile best = null;
            score = float.MinValue;
            attackValue = 0f;
            int bestDistance = int.MaxValue;
            foreach (CTile tile in new[] { TileAt(actor.ArrayIndex) }.Concat(destinations))
            {
                if (!IsValidMoveEndpoint(actor, tile))
                {
                    continue;
                }
                float value = ScoreMoveEndpoint(actor, attacks, tile.m_ArrayIndex, out float attacksValue);
                if (recordCandidates && DeveloperDiagnostics.Enabled)
                {
                    float threat = ThreatAt(actor, tile.m_ArrayIndex, null, out float futureDamage);
                    DeveloperDiagnostics.Record("move_candidate", actor, string.Format(CultureInfo.InvariantCulture,
                        "action=move;score={0};x={1};y={2};reason=exposure;threats={3};future_damage={4}",
                        value, tile.m_ArrayIndex.X, tile.m_ArrayIndex.Y, threat, futureDamage));
                }
                int distance = ScenarioManager.GetTileDistance(actor.ArrayIndex.X, actor.ArrayIndex.Y,
                    tile.m_ArrayIndex.X, tile.m_ArrayIndex.Y);
                if (value > score || value == score && distance < bestDistance)
                {
                    best = tile;
                    score = value;
                    attackValue = attacksValue;
                    bestDistance = distance;
                }
            }
            if (best == null)
            {
                score = 0f;
            }
            return best;
        }

        private static float ScoreMoveEndpoint(CPlayerActor actor, List<CAbilityAttack> attacks,
            Point position, out float attackValue)
        {
            attackValue = 0f;
            if (!IsValidMoveEndpoint(actor, TileAt(position)))
            {
                return float.MinValue;
            }

            HashSet<CActor> projectedKills = new HashSet<CActor>();
            foreach (CAbilityAttack attack in attacks)
            {
                int range = EffectiveAttackRange(actor, attack);
                CActor bestTarget = AttackTargetsFrom(actor, attack, position, range)
                    .Where(target => !projectedKills.Contains(target))
                    .OrderByDescending(target => ScoreAttackTargetFrom(actor, target, attack, position))
                    .FirstOrDefault();
                if (bestTarget == null)
                {
                    continue;
                }
                attackValue += ScoreAttackTargetFrom(actor, bestTarget, attack, position);
                int shield = Math.Max(0, bestTarget.CalculateShield(attack) - attack.Pierce);
                if (TacticalEvaluation.IsProjectedKill(attack.Strength - shield, bestTarget.Health,
                    range > 1 && ScenarioManager.GetTileDistance(position.X, position.Y,
                        bestTarget.ArrayIndex.X, bestTarget.ArrayIndex.Y) == 1))
                {
                    projectedKills.Add(bestTarget);
                }
            }
            float exposure = ThreatAt(actor, position, projectedKills, out _);
            return attackValue - exposure;
        }

        private static int EffectiveAttackRange(CActor actor, CAbilityAttack attack)
        {
            if (actor == null || attack == null)
            {
                return 1;
            }
            int bonus = CActiveBonus.FindApplicableActiveBonuses(actor, CAbility.EAbilityType.AddRange)
                .Where(activeBonus => activeBonus != null && activeBonus.Ability != null &&
                    activeBonus.Ability.Augment == null)
                .Sum(activeBonus => activeBonus.ReferenceStrength(attack, null));
            return Math.Max(1, EnhancedValue(attack, attack.Range, EEnhancement.PlusRange) + bonus);
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
                .Where(target => target != null && target != actor && IsRevealed(TileAt(target.ArrayIndex)) && !target.IsDead &&
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
