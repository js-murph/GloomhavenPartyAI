using System.Collections;
using System.Diagnostics;
using System.Reflection;
using AStar;
using GloomhavenPartyAI;
using ScenarioRuleLibrary;

internal static partial class ReferenceTests
{
    private static PlanningBudget FrozenBudget(int paths = 32, int los = 128, int samples = 512) =>
        new(paths, los, samples, timestamp: () => 0);

    private static object Planner(PlanningBudget budget, bool cheap = false) =>
        Activator.CreateInstance(typeof(TacticalPlanner), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [budget, cheap], null);

    private static object PlannerCall(object planner, string method, params object[] arguments) =>
        (typeof(TacticalPlanner).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new MissingMethodException(method)).Invoke(planner, arguments);

    private static void RequireCaps(PlanningBudget budget, int paths = 32, int los = 128, int samples = 512) =>
        Require(budget.PathQueries <= paths && budget.LosQueries <= los && budget.CandidateSamples <= samples,
            $"shared caps: path={budget.PathQueries}/{paths}, LOS={budget.LosQueries}/{los}, samples={budget.CandidateSamples}/{samples}");

    private static void PrepareHand(CPlayerActor actor, CAbilityCard recovery, CAbilityCard partner, int count)
    {
        TacticalPlanner.Reset();
        actor.CharacterClass.HandAbilityCards.Clear();
        actor.CharacterClass.RoundAbilityCards.Clear();
        actor.CharacterClass.HandAbilityCards.Add(recovery);
        for (int index = 1; index < count; index++)
            actor.CharacterClass.HandAbilityCards.Add(new CAbilityCard(50, -100 - index,
                partner.DefaultMoveAction.Copy(), partner.DefaultAttackAction.Copy(), partner.TopAction.Copy(),
                partner.BottomAction.Copy(), ether, ether.CharacterID, -100 - index));
    }

    private static void EightCardPreselectionUsesNoPathOrLos() => PreselectionUsesNoPathOrLos(8);
    private static void TwelveCardPreselectionUsesNoPathOrLos() => PreselectionUsesNoPathOrLos(12);

    private static void PreselectionUsesNoPathOrLos(int count)
    {
        WithBoard((actor, enemy, recovery, partner) =>
        {
            PrepareHand(actor, recovery, partner, count);
            var budget = FrozenBudget();
            List<CAbilityCard> selected = TacticalPlanner.ChooseRoundCards(actor, 2, budget);
            Require(selected.Count == 2 && selected.Distinct().Count() == 2 && selected.Contains(recovery) &&
                selected.All(actor.CharacterClass.HandAbilityCards.Contains), "complete legal pair retains recovery");
            Require(budget.PathQueries == 0 && budget.LosQueries == 0, "card-pair preselection never calls planner path/LOS");
            Require(budget.CandidateSamples == count * (count - 1) / 2, "every unordered pair receives one cheap sample");
            Require(!budget.TimeBudgetExceeded, "deterministic clock, not wall-time scheduling");
            RequireCaps(budget);
            Require(actor.CharacterClass.HandAbilityCards.Count == count && actor.CharacterClass.RoundAbilityCards.Count == 0,
                "preselection does not move cards");
            Console.WriteLine($"COUNTS cards={count} path={budget.PathQueries} los={budget.LosQueries} samples={budget.CandidateSamples}");
        }, rangedEnemy: true, blockMovement: false);
    }

    private static void ExpiredPreselectionPreservesRecoveryPairAndPartner()
    {
        WithBoard((actor, enemy, recovery, partner) =>
        {
            foreach (int count in new[] { 8, 12 })
            {
                PrepareHand(actor, recovery, partner, count);
                var budget = new PlanningBudget(maxMilliseconds: 0, timestamp: () => 0);
                var selected = TacticalPlanner.ChooseRoundCards(actor, 2, budget);
                Require(selected.Count == 2 && selected[0] == recovery && selected.Distinct().Count() == 2,
                    "expired preselection still returns a complete deterministic recovery pair");
                actor.CharacterClass.RoundAbilityCards.Add(partner);
                Require(TacticalPlanner.ChooseRoundCards(actor, 1, budget).Single() == recovery,
                    "expired partner selection preserves recovery");
                Require(budget.TimeBudgetExceeded, "deadline was observed");
                RequireCaps(budget, 0, 0, 0);
            }
        });
    }

    private static void FourRefinedPlansShareOperationBudgets()
    {
        WithBoard((actor, enemy, recovery, partner) =>
        {
            IList cheap = (IList)PlannerCall(Planner(FrozenBudget(), cheap: true), "EvaluatePlans", actor, recovery, partner, false);
            Require(cheap.Count > 4, "fixture offers more than four sequences before refinement");
            foreach (var limits in new[] { (32, 128, 512), (1, 1, 1), (0, 0, 0) })
            {
                var budget = FrozenBudget(limits.Item1, limits.Item2, limits.Item3);
                IList refined = (IList)PlannerCall(Planner(budget), "EvaluatePlans", actor, recovery, partner, false);
                Require(refined.Count == 4, "actual EvaluatePlans caps the refinement shortlist at four");
                RequireCaps(budget, limits.Item1, limits.Item2, limits.Item3);
                if (limits.Item1 < 32) Require(budget.BudgetExhausted, "small shared allowance is exhausted");
                Console.WriteLine($"COUNTS refined={refined.Count}/{cheap.Count} path={budget.PathQueries} los={budget.LosQueries} samples={budget.CandidateSamples}");
            }
        }, rangedEnemy: true, blockMovement: false);
    }

    private static void ZeroBudgetRecoveryFallbackPreservesLastChance()
    {
        WithBoard((actor, enemy, recovery, partner) =>
        {
            foreach (var budget in new[] { FrozenBudget(0, 0, 0), new PlanningBudget(maxMilliseconds: 0, timestamp: () => 0) })
            {
                Require(TacticalPlanner.ChooseNextAction(actor, budget)?.Action.Abilities.Single() is CAbilityRecoverLostCards,
                    "first-action fallback cannot hide recovery behind a default attack");
                RequireCaps(budget, 0, 0, 0);
            }
            actor.CharacterClass.HandAbilityCards.Clear();
            actor.CharacterClass.LostAbilityCards.RemoveAt(2);
            SetSecondAction(actor, partner);
            foreach (var budget in new[] { FrozenBudget(0, 0, 0), new PlanningBudget(maxMilliseconds: 0, timestamp: () => 0) })
            {
                var selected = TacticalPlanner.ChooseNextAction(actor, budget);
                Require(selected?.Card == recovery && selected.Action.Abilities.Single() is CAbilityRecoverLostCards &&
                    float.IsFinite(selected.Score), "h0 r1 d1 lost2 last-chance recovery survives zero budget/deadline");
                RequireCaps(budget, 0, 0, 0);
            }
            actor.CharacterClass.LostAbilityCards.Clear();
            var first = TacticalPlanner.ChooseNextAction(actor, new PlanningBudget(maxMilliseconds: 0, timestamp: () => 0));
            var second = TacticalPlanner.ChooseNextAction(actor, new PlanningBudget(maxMilliseconds: 0, timestamp: () => 0));
            Require(first?.ActionType == CBaseCard.ActionType.DefaultAttackAction && first.Action == second?.Action &&
                float.IsFinite(first.Score), "empty recovery pool uses stable finite default fallback");
            Require(actor.CharacterClass.RoundAbilityCards.Single() == recovery &&
                actor.CharacterClass.DiscardedAbilityCards.Single() == partner && enemy.Health == 6,
                "fallback does not submit actions or change piles");
        });
    }

    private static void DeadlineAfterOneEngineCallStopsMovement()
    {
        WithBoard((actor, enemy, recovery, partner) =>
        {
            foreach (bool path in new[] { true, false })
            {
                CAbilityMove move = PrepareFutureMelee(actor, partner);
                PlanningBudget budget = null;
                // Admission advances the clock, modeling an uninterruptible engine call finishing late.
                budget = new PlanningBudget(timestamp: () =>
                    (path ? budget?.PathQueries : budget?.LosQueries) > 0 ? Stopwatch.Frequency / 10 : 0);
                var chosen = TacticalPlanner.ChooseMoveDestination(actor, move, budget);
                Require(chosen?.m_ArrayIndex == actor.ArrayIndex && budget.TimeBudgetExceeded && budget.BudgetExhausted,
                    "late engine call returns a safe stationary fallback");
                Require((path ? budget.PathQueries : budget.LosQueries) == 1, "exactly one call of the delayed category");
                int before = budget.PathQueries + budget.LosQueries + budget.CandidateSamples;
                Require(!budget.TryPathQuery() && !budget.TryLosQuery() && !budget.TrySample() &&
                    before == budget.PathQueries + budget.LosQueries + budget.CandidateSamples, "all later work is refused");
                RequireCaps(budget);
            }
        }, rangedEnemy: true, blockMovement: false);
    }

    private static void PathCacheReusesQueriesButCannotBypassDeadline()
    {
        WithBoard((actor, enemy, recovery, partner) =>
        {
            CAbilityMove move = PrepareFutureMelee(actor, partner);
            long now = 0;
            var budget = new PlanningBudget(timestamp: () => now);
            object planner = Planner(budget);
            CTile near = ScenarioManager.Tiles[3, 2];
            object path = PlannerCall(planner, "FindPath", actor, move, near, true);
            Require(path != null && ReferenceEquals(path, PlannerCall(planner, "FindPath", actor, move, near, true)) &&
                budget.PathQueries == 1 && budget.CacheHits == 1, "identical raw path query hits invocation-local cache");
            PlannerCall(planner, "FindPath", actor, move, near, false);
            Require(budget.PathQueries == 2, "trap policy remains part of the cache key");
            now = Stopwatch.Frequency / 10;
            Require(ReferenceEquals(path, PlannerCall(planner, "FindPath", actor, move, near, true)) &&
                budget.CacheHits == 2 && budget.TimeBudgetExceeded, "expired cache hit records deadline without querying");
            Require((int)PlannerCall(planner, "MovePathCost", actor, move, near, (int?)2) == int.MaxValue &&
                budget.PathQueries == 2, "cached raw route cannot bypass validation deadline");
            var freshBudget = FrozenBudget();
            Require(PlannerCall(Planner(freshBudget), "FindPath", actor, move, near, true) != null &&
                freshBudget.PathQueries == 1 && freshBudget.CacheHits == 0, "new invocation does not inherit stale paths");
            for (int y = 0; y < ScenarioManager.Height; y++) ScenarioManager.PathFinder.Nodes[5, y].Walkable = false;
            CTile unreachable = ScenarioManager.Tiles[6, 2];
            CActor.FindCharacterPath(actor, actor.ArrayIndex, unreachable.m_ArrayIndex, false, false, out bool found, true);
            Require(!found, "physical barrier makes the engine report no complete path");
            var failedBudget = FrozenBudget();
            object failedPlanner = Planner(failedBudget);
            Require(PlannerCall(failedPlanner, "FindPath", actor, move, unreachable, true) == null &&
                PlannerCall(failedPlanner, "FindPath", actor, move, unreachable, true) == null &&
                failedBudget.PathQueries == 1 && failedBudget.CacheHits == 1,
                "failed engine routes are cached as failures, never as usable partial paths");
        }, rangedEnemy: true, blockMovement: false);
    }

    private static void SameMoveContinuesToFirstEndpointAndStopsOnArrival()
    {
        WithBoard((actor, enemy, recovery, partner) =>
        {
            CAbilityMove move = PrepareFutureMelee(actor, partner);
            Point origin = actor.ArrayIndex;
            CTile goal = TacticalPlanner.ChooseMoveDestination(actor, move, FrozenBudget());
            var path = CActor.FindCharacterPath(actor, origin, goal.m_ArrayIndex, false, false, out bool found, true);
            Require(found && path.Count == 2, "fixture has a real two-step engine route");
            SetField(typeof(CActor), actor, "m_ArrayIndex", path[0]);
            SetField(typeof(CAbilityMove), move, "m_TilesMoved", 1);
            SetField(typeof(CAbilityMove), move, "m_MoveCount", 1);
            var continuation = FrozenBudget();
            Require(TacticalPlanner.ChooseMoveDestination(actor, move, continuation) == goal && continuation.PathQueries == 1,
                "same ability continues toward first selected endpoint, revalidating exactly one route");
            SetField(typeof(CActor), actor, "m_ArrayIndex", goal.m_ArrayIndex);
            // Leave one move available so stopping proves arrival commitment, not an exhausted allowance.
            for (int index = 0; index < 3; index++)
            {
                var reached = FrozenBudget();
                Require(TacticalPlanner.ChooseMoveDestination(actor, move, reached) == goal,
                    "arrival never reoptimizes or steps backward with unused movement");
                RequireCaps(reached, 0, 0, 0);
            }
        }, rangedEnemy: true, blockMovement: false);
    }

    private static void BlockedCorridorCannotOscillateOrRevisitAndInvalidationResets()
    {
        WithBoard((actor, enemy, recovery, partner) =>
        {
            CAbilityMove move = PrepareFutureMelee(actor, partner);
            Point origin = new(4, 17);
            Point step = new(5, 17);
            CTile goal = TacticalPlanner.ChooseMoveDestination(actor, move, FrozenBudget());
            Require(actor.ArrayIndex == origin && goal?.m_ArrayIndex == new Point(6, 17),
                $"seven-hex corridor selects 6:17 from 4:17; actual={goal?.m_ArrayIndex}");
            var route = CActor.FindCharacterPath(actor, origin, goal.m_ArrayIndex, false, false, out bool found, true);
            Require(found && route.SequenceEqual(new[] { step, goal.m_ArrayIndex }), "real route begins 4:17 -> 5:17");
            SetField(typeof(CActor), actor, "m_ArrayIndex", step);
            SetField(typeof(CAbilityMove), move, "m_TilesMoved", 1);
            SetField(typeof(CAbilityMove), move, "m_MoveCount", 1);
            ScenarioManager.PathFinder.Nodes[6, 17].Blocked = true;
            SetField(typeof(CActor), enemy, "m_ArrayIndex", new Point(3, 17));
            for (int index = 0; index < 3; index++)
            {
                CTile replanned = TacticalPlanner.ChooseMoveDestination(actor, move, FrozenBudget());
                Require(replanned?.m_ArrayIndex == step, "blocked 6:17 cannot authorize 5:17 -> visited 4:17 oscillation");
            }

            // Exercise the actual route validator too: rejecting only visited endpoints would still
            // allow a longer route through 4:17 to a previously unseen tile behind it.
            var plans = (IDictionary)typeof(TacticalPlanner).GetField("MovePlans", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            object planner = Planner(FrozenBudget());
            SetField(typeof(TacticalPlanner), planner, "moving", plans[actor.ActorGuid]);
            ScenarioManager.Scenario.Enemies.Clear();
            Require(!(bool)PlannerCall(planner, "IsSafeMovePath", actor, move,
                new List<Point> { origin, new(3, 17) }, ScenarioManager.Tiles[3, 17]),
                "a route through a visited tile is rejected even when its endpoint is unvisited");
            ScenarioManager.Scenario.Enemies.Add(enemy);

            TacticalPlanner.InvalidateMovement(actor);
            Require(!plans.Contains(actor.ActorGuid), "explicit invalidation removes commitment and visited history");
            CTile fresh = TacticalPlanner.ChooseMoveDestination(actor, move, FrozenBudget());
            Require(fresh?.m_ArrayIndex == origin, "fresh planning may use 4:17 after invalidation; previous veto was not a physical barrier");
            TacticalPlanner.Reset();
            Require(plans.Count == 0, "scenario reset clears movement commitments");
            Console.WriteLine("EVIDENCE tiny corridor: 4:17 -> 5:17; blocked 6:17 stays 5:17; explicit invalidation permits 4:17");
        }, rangedEnemy: true, blockMovement: false, tinyCorridor: true);
    }

    private static void PlannerOperationMeasurements()
    {
        WithBoard((actor, enemy, recovery, partner) =>
        {
            foreach (int count in new[] { 8, 12 })
            {
                PrepareHand(actor, recovery, partner, count);
                MeasurePlanner("cards-" + count, budget => TacticalPlanner.ChooseRoundCards(actor, 2, budget));
            }
            actor.CharacterClass.RoundAbilityCards.Add(recovery);
            actor.CharacterClass.RoundAbilityCards.Add(partner);
            MeasurePlanner("action-four-refined", budget => TacticalPlanner.ChooseNextAction(actor, budget));
            CAbilityMove move = PrepareFutureMelee(actor, partner);
            MeasurePlanner("move", budget =>
            {
                TacticalPlanner.InvalidateMovement(actor);
                return TacticalPlanner.ChooseMoveDestination(actor, move, budget);
            });
        }, rangedEnemy: true, blockMovement: false);
    }

    private static void MeasurePlanner(string name, Func<PlanningBudget, object> choose)
    {
        for (int index = 0; index < 3; index++) choose(new PlanningBudget());
        double[] elapsed = new double[20];
        int paths = 0, los = 0, samples = 0, expired = 0;
        for (int index = 0; index < elapsed.Length; index++)
        {
            var budget = new PlanningBudget();
            var clock = Stopwatch.StartNew();
            Require(choose(budget) != null, "measurement returns a plan or safe fallback");
            elapsed[index] = clock.Elapsed.TotalMilliseconds;
            RequireCaps(budget);
            paths = Math.Max(paths, budget.PathQueries);
            los = Math.Max(los, budget.LosQueries);
            samples = Math.Max(samples, budget.CandidateSamples);
            if (budget.TimeBudgetExceeded) expired++;
        }
        Array.Sort(elapsed);
        Console.WriteLine(FormattableString.Invariant($"MEASURE {name} n={elapsed.Length} median_ms={elapsed[elapsed.Length / 2]:F3} max_ms={elapsed[^1]:F3} max_path={paths} max_los={los} max_samples={samples} expired={expired}; timing informational only"));
    }
}
