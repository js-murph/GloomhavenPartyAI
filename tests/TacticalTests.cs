using GloomhavenPartyAI;

internal static class TacticalTests
{
    internal static void CopiedActionIdentityPreservesOnlyTheCommittedHalf()
    {
        Guid original = Guid.NewGuid();
        Guid copiedId = new Guid(original.ToByteArray());
        Check.True(TacticalEvaluation.MatchesCommittedAction(original, copiedId, 2, 2),
            "the game copies actions while preserving their ID");
        Check.True(!TacticalEvaluation.MatchesCommittedAction(original, copiedId, 2, 3),
            "a different selected half must not retain the plan");
        Check.True(!TacticalEvaluation.MatchesCommittedAction(original, Guid.NewGuid(), 2, 2),
            "a different action must not retain the plan");
        Check.True(!TacticalEvaluation.MatchesCommittedAction(original, null, 2, 2),
            "uncommitted action has no followup");
        Check.True(!TacticalEvaluation.MatchesCommittedAction(Guid.Empty, Guid.Empty, 2, 2),
            "missing IDs do not establish action identity");
    }

    internal static void MovementCacheKeysDistinguishEveryRuleCombination()
    {
        var keys = new HashSet<int>();
        for (int flags = 0; flags < 64; flags++)
        {
            int key = TacticalEvaluation.MovementRulesKey((flags & 1) != 0, (flags & 2) != 0,
                (flags & 4) != 0, (flags & 8) != 0, (flags & 16) != 0, (flags & 32) != 0);
            Check.True(keys.Add(key), $"movement rules collide for flags={flags}");
            Check.Equal(key, TacticalEvaluation.MovementRulesKey((flags & 1) != 0, (flags & 2) != 0,
                (flags & 4) != 0, (flags & 8) != 0, (flags & 16) != 0, (flags & 32) != 0),
                "equivalent movement rules reuse the cache key");
        }
    }

    internal static void ZeroDamageAndDeadTargetsHaveNoAttackValue()
    {
        foreach (bool acted in new[] { false, true })
        foreach (bool adjacent in new[] { false, true })
        {
            foreach (int damage in new[] { int.MinValue, -1, 0 })
            {
                Check.Near(0, TacticalEvaluation.AttackValue(damage, 5, acted, adjacent), $"damage={damage}");
                Check.True(!TacticalEvaluation.IsProjectedKill(damage, 5, adjacent), "zero effective damage cannot kill");
            }
            foreach (int health in new[] { -1, 0 })
            {
                Check.Near(0, TacticalEvaluation.AttackValue(10, health, acted, adjacent), $"health={health}");
                Check.True(!TacticalEvaluation.IsProjectedKill(10, health, adjacent), "dead target is not a new kill");
            }
        }
    }

    internal static void EffectiveDamageAndKillTiming()
    {
        Check.Near(12, TacticalEvaluation.AttackValue(3, 5, false), "effective damage earns four points per HP");
        Check.Near(46, TacticalEvaluation.AttackValue(5, 5, false), "kill before turn includes denial bonus");
        Check.Near(34, TacticalEvaluation.AttackValue(5, 5, true), "kill after turn retains smaller bonus");
        Check.Near(12, TacticalEvaluation.AttackValue(3, 5, true), "turn timing does not alter nonlethal damage");
    }

    internal static void AttackStrengthIsMonotonicAndOverkillIsHarmless()
    {
        foreach (bool acted in new[] { false, true })
        foreach (bool adjacent in new[] { false, true })
        for (int health = 1; health <= 40; health++)
        {
            float previous = 0;
            for (int damage = 0; damage <= 100; damage++)
            {
                float score = TacticalEvaluation.AttackValue(damage, health, acted, adjacent);
                Check.True(score >= previous, $"stronger attack regressed: hp={health}, damage={damage}, acted={acted}, adjacent={adjacent}");
                previous = score;
            }
            float cap = health * 4 + (acted ? 14 : 26);
            Check.Near(cap, previous, "overkill reaches, but does not exceed, capped kill value");
            Check.Near(cap, TacticalEvaluation.AttackValue(int.MaxValue, health, acted, adjacent), "extreme overkill is not penalized");
        }
    }

    internal static void AdjacentRangedDisadvantageIsConservative()
    {
        Check.Near(3, TacticalEvaluation.EstimatedDamage(4, true), "adjacent ranged damage uses 0.75 heuristic");
        Check.Near(4, TacticalEvaluation.EstimatedDamage(4, false), "normal damage unchanged");
        Check.True(!TacticalEvaluation.IsProjectedKill(4, 4, true), "nominal lethal hit is not a projected disadvantaged kill");
        Check.True(TacticalEvaluation.IsProjectedKill(4, 3, true), "effective lethal threshold is inclusive");
        for (int health = 1; health <= 30; health++)
        for (int damage = 0; damage <= 50; damage++)
        foreach (bool acted in new[] { false, true })
            Check.True(TacticalEvaluation.AttackValue(damage, health, acted, true) <=
                TacticalEvaluation.AttackValue(damage, health, acted, false), $"disadvantage improved value: hp={health}, damage={damage}");
    }

    internal static void ControlStillMattersAfterTargetActs()
    {
        Check.Near(20, TacticalEvaluation.ControlConditionValue(20, false), "control before turn");
        Check.Near(17, TacticalEvaluation.ControlConditionValue(20, true), "control after turn retains next-turn denial");
        foreach (bool acted in new[] { false, true })
            Check.Near(0, TacticalEvaluation.ControlConditionValue(-10, acted), "negative control is clamped");
    }

    internal static void ConditionsRespectProjectedDeath()
    {
        Check.Near(0, TacticalEvaluation.ConditionValue(8, true, false), "no condition value on projected corpse");
        Check.Near(8, TacticalEvaluation.ConditionValue(8, true, true), "persistent effect survives projected death");
        Check.Near(8, TacticalEvaluation.ConditionValue(8, false, false), "living target retains condition value");
        Check.Near(0, TacticalEvaluation.ConditionValue(-8, false, true), "negative condition value clamped");
    }

    internal static void LossPenaltyTracksRemainingCycles()
    {
        foreach (var (cards, expected) in new[] { (0, 5f), (1, 5f), (4, 5f), (5, 10f), (6, 10f), (7, 15f), (11, 25f) })
            Check.Near(expected, TacticalEvaluation.LossPenalty(cards), $"cycling cards={cards}");
        for (int cards = 1; cards < 30; cards++)
            Check.True(TacticalEvaluation.LossPenalty(cards + 1) >= TacticalEvaluation.LossPenalty(cards), "more cycles cannot make a loss cheaper");
    }

    internal static void FutureCardValueIncludesBothHalvesAndInitiative()
    {
        Check.Near(10, TacticalEvaluation.FutureCardValue(8, 4, 50), "stronger half plus half of weaker half");
        Check.Near(10, TacticalEvaluation.FutureCardValue(4, 8, 50), "half order does not matter");
        foreach (int initiative in new[] { 1, 25, 75, 99 })
            Check.Near(11.5f, TacticalEvaluation.FutureCardValue(8, 4, initiative), $"extreme initiative={initiative}");
        foreach (int initiative in new[] { 26, 74 })
            Check.Near(10, TacticalEvaluation.FutureCardValue(8, 4, initiative), $"ordinary initiative={initiative}");
    }

    internal static void RoleScarcityDecreasesWithOtherProviders()
    {
        Check.Near(3, TacticalEvaluation.RoleScarcityValue(0), "unique role premium");
        Check.Near(3, TacticalEvaluation.RoleScarcityValue(-1), "negative provider count clamped");
        Check.Near(1.5f, TacticalEvaluation.RoleScarcityValue(1), "one alternative halves premium");
        for (int providers = 0; providers < 30; providers++)
        {
            float next = TacticalEvaluation.RoleScarcityValue(providers + 1);
            Check.True(next > 0 && next < TacticalEvaluation.RoleScarcityValue(providers), $"scarcity providers={providers}");
        }
    }

    internal static void InitiativeRespondsToPlannedActionsAndPressure()
    {
        Check.Near(2.7f, TacticalEvaluation.InitiativeValue(10, 10, 0, true), "pressure and attack urgency combine");
        foreach (var (attack, heal, pressure) in new[] { (10f, 0f, false), (0f, 10f, false), (0f, 0f, true) })
            Check.True(TacticalEvaluation.InitiativeValue(10, attack, heal, pressure) >
                TacticalEvaluation.InitiativeValue(90, attack, heal, pressure), "urgent action prefers early initiative");
        Check.True(TacticalEvaluation.InitiativeValue(90, 0, 0, false) >
            TacticalEvaluation.InitiativeValue(10, 0, 0, false), "no urgent action prefers late initiative");
        Check.Near(TacticalEvaluation.InitiativeValue(50, 0, 0, false),
            TacticalEvaluation.InitiativeValue(50, -10, -10, false), "negative plans do not create urgency");
        Check.Near(TacticalEvaluation.InitiativeValue(1, 10, 10, true),
            TacticalEvaluation.InitiativeValue(int.MinValue, 10, 10, true), "initiative lower clamp");
        Check.Near(TacticalEvaluation.InitiativeValue(99, 10, 10, true),
            TacticalEvaluation.InitiativeValue(int.MaxValue, 10, 10, true), "initiative upper clamp");
    }
}
