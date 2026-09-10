using System.Reflection;
using AStar;
using GloomhavenPartyAI;
using ScenarioRuleLibrary;
using ScenarioRuleLibrary.YML;

internal static partial class ReferenceTests
{
    // Observe real engine calls without replacing their results or planner policy.
    private sealed class RankingTarget : CEnemyActor
    {
        internal int ShieldReads, InitiativeReads;
        public override int BaseShield() { ShieldReads++; return base.BaseShield(); }
        public override int Initiative() { InitiativeReads++; return base.Initiative(); }
    }

    private static RankingTarget RankingActor(CEnemyActor template, int id, Point position, bool ally = false, int health = 6)
    {
        if (template.MonsterClass.MonsterYML == null)
            SetField(typeof(CMonsterClass), template.MonsterClass, "m_MonstersYML", new MonsterYMLData("ranking-fixture"));
        var target = new RankingTarget
        {
            StandeeID = id, Type = ally ? CActor.EType.Ally : CActor.EType.Enemy,
            OriginalType = ally ? CActor.EType.Ally : CActor.EType.Enemy,
            Health = health, MaxHealth = 10, CauseOfDeath = CActor.ECauseOfDeath.StillAlive
        };
        typeof(CActor).GetProperty("ActorGuid").SetValue(target, "ranking-" + id);
        SetField(typeof(CActor), target, "m_Class", template.MonsterClass);
        SetField(typeof(CActor), target, "m_ArrayIndex", position);
        SetField(typeof(CActor), target, "m_Tokens", new CTokens(target));
        return target;
    }

    private static CAbilityHeal RankingHeal()
    {
        var heal = new CAbilityHeal(null)
        {
            Strength = 3, Range = 6, NumberTargets = 1, Targeting = CAbility.EAbilityTargeting.Range,
            AbilityFilter = new CAbilityFilterContainer(CAbilityFilter.EFilterTargetType.Self | CAbilityFilter.EFilterTargetType.Ally)
        };
        // Ranking receives an in-progress ability. Seed its resolved strength without invoking Start.
        SetField(typeof(CAbility), heal, "m_ModifiedStrength", 3);
        return heal;
    }

    private static void AttackRankingScoresOnlyUniqueSuppliedTargetsAndBreaksTiesStably()
    {
        WithBoard((actor, enemy, recovery, partner) =>
        {
            var attack = (CAbilityAttack)CAbilityAttack.CreateDefaultAttack(2, 6, 1, false);
            var nearLowId = RankingActor(enemy, 2, new Point(0, 2));
            var nearHighId = RankingActor(enemy, 9, new Point(4, 2));
            var farFocus = RankingActor(enemy, 20, new Point(6, 2));
            var kill = RankingActor(enemy, 30, new Point(5, 2), health: 2);
            var ignored = RankingActor(enemy, 40, new Point(3, 2), health: 1);
            var dead = RankingActor(enemy, 41, new Point(3, 2), health: 0);
            dead.CauseOfDeath = CActor.ECauseOfDeath.Damage;
            attack.ActorsToIgnore = [ignored];
            actor.AIMoveFocusActors = [farFocus];
            CActor[] candidates = [nearHighId, farFocus, null, actor, nearLowId, ignored, dead, kill, nearHighId];
            CActor[] expected = [kill, farFocus, nearLowId, nearHighId];
            Require(ScenarioManager.GetTileDistance(2, 2, 0, 2) == ScenarioManager.GetTileDistance(2, 2, 4, 2),
                "equal-score/equal-distance/equal-initiative targets exercise ID ties");

            // Rank supplied targets even when pathfinding is unavailable. This also detects an
            // unbudgeted engine path query hidden inside a distance tie-breaker.
            typeof(ScenarioManager).GetField("s_PathFinder", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, null);
            for (int pass = 0; pass < 2; pass++)
            {
                var budget = FrozenBudget();
                var ranked = TacticalPlanner.RankAttackTargets(actor, attack, pass == 0 ? candidates : candidates.Reverse(), budget);
                Require(ranked.SequenceEqual(expected), "score, focus, geometric distance and ID give input-order-independent ranking");
                Require(ranked.All(candidates.Contains) && !ranked.Contains(enemy), "ranking cannot discover a scenario target outside the supplied subset");
                Require(expected.Cast<RankingTarget>().All(target => target.ShieldReads == pass + 1 && target.InitiativeReads == pass + 1),
                    "each unique eligible candidate is scored exactly once per invocation");
                Require(ignored.ShieldReads == 0 && dead.ShieldReads == 0, "ignored/dead candidates never reach engine scoring");
                Require(budget.CandidateSamples == candidates.Length, "admission counts include duplicate and invalid supplied entries");
                RequireCaps(budget, 0, 0, candidates.Length);
            }
            actor.AIMoveFocusActors.Clear();
            Require(TacticalPlanner.RankAttackTargets(actor, attack, [farFocus, nearHighId, nearLowId], FrozenBudget())
                .SequenceEqual(new CActor[] { nearLowId, nearHighId, farFocus }), "without focus, equal scores use geometric distance then stable ID");
            Require(kill.Health == 2 && attack.ActorsToIgnore.Single() == ignored &&
                (attack.ActorsTargeted?.Count ?? 0) == 0, "ranking does not deal damage or mutate target selection");
        }, blockMovement: false);
    }

    private static void HealRankingScoresOnlyUsefulSuppliedAlliesAndBreaksTiesStably()
    {
        WithBoard((actor, enemy, recovery, partner) =>
        {
            CAbilityHeal heal = RankingHeal();
            Require(TacticalPlanner.IsSupportedHeal(heal), "real supported heal fixture");
            var nearLowId = RankingActor(enemy, 2, new Point(0, 2), ally: true);
            var nearHighId = RankingActor(enemy, 9, new Point(4, 2), ally: true);
            var far = RankingActor(enemy, 20, new Point(6, 2), ally: true);
            var critical = RankingActor(enemy, 30, new Point(5, 2), ally: true, health: 2);
            var full = RankingActor(enemy, 40, new Point(3, 2), ally: true, health: 10);
            var ignored = RankingActor(enemy, 41, new Point(3, 2), ally: true, health: 1);
            heal.ActorsToIgnore = [ignored];
            CActor[] candidates = [far, null, nearHighId, enemy, full, nearLowId, ignored, critical, nearHighId];
            CActor[] expected = [critical, nearLowId, nearHighId, far];
            typeof(ScenarioManager).GetField("s_PathFinder", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, null);
            for (int pass = 0; pass < 2; pass++)
            {
                var budget = FrozenBudget();
                var ranked = TacticalPlanner.RankHealTargets(actor, heal, pass == 0 ? candidates : candidates.Reverse(), budget);
                Require(ranked.SequenceEqual(expected) && ranked.All(candidates.Contains) && !ranked.Contains(actor),
                    "only useful supplied allies are ranked by score, geometric distance and stable ID");
                Require(expected.Cast<RankingTarget>().All(target => target.InitiativeReads == pass + 1),
                    "duplicate supplied allies produce one scored ranking entry each");
                Require(full.InitiativeReads == 0 && ignored.InitiativeReads == 0, "zero-value and ignored heals never become ranked entries");
                Require(budget.CandidateSamples == candidates.Length, "heal admission counts every supplied entry");
                RequireCaps(budget, 0, 0, candidates.Length);
            }
            Require(critical.Health == 2 && heal.ActorsToIgnore.Single() == ignored &&
                (heal.ActorsTargeted?.Count ?? 0) == 0, "ranking does not heal or mutate target selection");
        }, blockMovement: false);
    }

    private static void TargetRankingsRespectHardAndSharedCandidateCaps()
    {
        WithBoard((actor, enemy, recovery, partner) =>
        {
            var attack = (CAbilityAttack)CAbilityAttack.CreateDefaultAttack(2, 6, 1, false);
            CAbilityHeal heal = RankingHeal();
            foreach (bool healing in new[] { false, true })
            {
                // Independent ranking snapshots, not 520 actors placed onto a playable board.
                var targets = Enumerable.Range(0, 520).Select(index =>
                    RankingActor(enemy, index + 100, new Point(4, 2), ally: healing)).ToArray();
                var budget = new PlanningBudget(int.MaxValue, int.MaxValue, int.MaxValue, timestamp: () => 0);
                var ranked = healing ? TacticalPlanner.RankHealTargets(actor, heal, targets, budget) :
                    TacticalPlanner.RankAttackTargets(actor, attack, targets, budget);
                Require(ranked.SequenceEqual(targets.Take(512)) && budget.CandidateSamples == 512 && budget.BudgetExhausted,
                    "ranking never raises the 512-entry cap even when requested limits are larger");
                Require(targets.Take(512).All(target => target.InitiativeReads == 1) &&
                    targets.Skip(512).All(target => target.InitiativeReads == 0 && target.ShieldReads == 0),
                    "only the admitted subset reaches scoring/ranking");
                RequireCaps(budget, 0, 0, 512);

                var repeated = RankingActor(enemy, 999, new Point(4, 2), ally: healing);
                var duplicates = Enumerable.Repeat<CActor>(repeated, 520);
                budget = FrozenBudget();
                ranked = healing ? TacticalPlanner.RankHealTargets(actor, heal, duplicates, budget) :
                    TacticalPlanner.RankAttackTargets(actor, attack, duplicates, budget);
                Require(ranked.Single() == repeated && repeated.InitiativeReads == 1 &&
                    (healing || repeated.ShieldReads == 1) && budget.CandidateSamples == 512 && budget.BudgetExhausted,
                    "duplicate floods still consume the hard admission cap but score the target only once");
                RequireCaps(budget, 0, 0, 512);
            }
            var shared = FrozenBudget(samples: 3);
            var first = RankingActor(enemy, 1, new Point(4, 2));
            var ally = RankingActor(enemy, 2, new Point(4, 2), ally: true);
            Require(TacticalPlanner.RankAttackTargets(actor, attack, [first, first], shared).Single() == first,
                "attack consumes two of three shared samples while scoring one unique target");
            Require(TacticalPlanner.RankHealTargets(actor, heal, [ally, ally], shared).Single() == ally && shared.CandidateSamples == 3,
                "heal receives only the remainder, not a fresh per-helper budget");
            Require(TacticalPlanner.RankAttackTargets(actor, attack, [first], shared).Count == 0 && shared.BudgetExhausted,
                "exhausted shared allowance cannot restart ranking");
            RequireCaps(shared, 0, 0, 3);
        }, blockMovement: false);
    }

    private static void ColdExpiredRankingsDoNotEnumerateOrQueryTheEngine()
    {
        WithBoard((actor, enemy, recovery, partner) =>
        {
            var attack = (CAbilityAttack)CAbilityAttack.CreateDefaultAttack(2, 6, 1, false);
            CAbilityHeal heal = RankingHeal();
            IEnumerable<CActor> MustNotEnumerate() => Enumerable.Range(0, 1)
                .Select<int, CActor>(_ => throw new InvalidOperationException("expired ranking enumerated its candidates"));
            foreach (bool healing in new[] { false, true })
            {
                var budget = new PlanningBudget(maxMilliseconds: 0, timestamp: () => 0);
                var ranked = healing ? TacticalPlanner.RankHealTargets(actor, heal, MustNotEnumerate(), budget) :
                    TacticalPlanner.RankAttackTargets(actor, attack, MustNotEnumerate(), budget);
                Require(ranked.Count == 0 && budget.TimeBudgetExceeded, "cold expired ranking returns an empty safe subset");
                RequireCaps(budget, 0, 0, 0);
                var zero = FrozenBudget(0, 0, 0);
                Require((healing ? TacticalPlanner.RankHealTargets(actor, heal, [actor], zero) :
                    TacticalPlanner.RankAttackTargets(actor, attack, [enemy], zero)).Count == 0 && zero.BudgetExhausted,
                    "zero operation budget does not invent an unscored target");
                RequireCaps(zero, 0, 0, 0);
            }
        });
    }

    private static void ThreatQueriesShareLosBudgetAndExpiredThreatIsConservative()
    {
        WithBoard((actor, enemy, recovery, partner) =>
        {
            var budget = FrozenBudget(paths: 0, los: 1);
            Require(!TacticalPlanner.IsUnderThreat(actor, budget), "stationary ranged enemy cannot reach actor five hexes away");
            Require(budget.LosQueries == 1, "threat helper charges the caller's LOS budget");
            SetField(typeof(CActor), enemy, "m_ArrayIndex", new Point(3, 2));
            Require(TacticalPlanner.IsUnderThreat(actor, budget) && budget.BudgetExhausted && budget.LosQueries == 1,
                "next call shares the exhausted budget and conservatively treats unknown LOS as threat");
            RequireCaps(budget, 0, 1, 512);
            var expired = new PlanningBudget(maxMilliseconds: 0, timestamp: () => 0);
            Require(TacticalPlanner.IsUnderThreat(actor, expired) && expired.TimeBudgetExceeded,
                "expired threat computation cannot declare an exposed actor safe");
            RequireCaps(expired, 0, 0, 0);
            Require(!TacticalPlanner.IsUnderThreat(null, FrozenBudget()), "missing actor is not a threat target");
        }, rangedEnemy: true, blockMovement: false);
    }
}
