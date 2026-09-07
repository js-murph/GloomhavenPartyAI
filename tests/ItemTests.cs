using GloomhavenPartyAI;

internal static class ItemTests
{
    internal static void HealingRejectsInvalidAndWastefulUse()
    {
        foreach (var (health, maximum, healing) in new[] { (0, 10, 3), (-1, 10, 3), (5, 0, 3), (5, -1, 3), (5, 10, 0), (5, 10, -1), (5, 10, int.MaxValue) })
            Check.Near(0, ItemEvaluation.HealingScore(health, maximum, healing, true, true, false), $"invalid heal hp={health}, max={maximum}, healing={healing}");
        Check.Near(0, ItemEvaluation.HealingScore(10, 10, 3, false, false, false), "full HP needs no healing");
        Check.Near(0, ItemEvaluation.HealingScore(8, 10, 3, false, false, true), "save consumed heal with overheal");
        Check.Near(2, ItemEvaluation.HealingScore(8, 10, 3, false, false, false), "spent heal can restore partial amount");
        Check.Near(3, ItemEvaluation.HealingScore(7, 10, 3, false, false, true), "exact missing HP justifies consumed heal");
    }

    internal static void HealingCriticalHealthAndConditionThresholds()
    {
        Check.Near(5, ItemEvaluation.HealingScore(35, 100, 3, false, false, true), "35 percent receives urgency bonus");
        Check.Near(3, ItemEvaluation.HealingScore(36, 100, 3, false, false, true), "above 35 percent no urgency bonus");
        Check.Near(15, ItemEvaluation.HealingScore(7, 20, 14, false, false, true), "critical overheal allowed at threshold");
        Check.Near(0, ItemEvaluation.HealingScore(8, 20, 14, false, false, true), "noncritical overheal saved");
        Check.Near(3, ItemEvaluation.HealingScore(2, 10, 3, true, false, true), "poison removal replaces HP, no restoration urgency");
        Check.Near(7, ItemEvaluation.HealingScore(2, 10, 3, false, true, true), "wound removal accompanies HP and urgency");
        Check.Near(5, ItemEvaluation.HealingScore(2, 10, 3, true, true, true), "both conditions removed without HP");
        Check.Near(3, ItemEvaluation.HealingScore(10, 10, 3, true, false, true), "poison removal useful at full HP");
        Check.Near(2, ItemEvaluation.HealingScore(10, 10, 3, false, true, true), "wound removal useful at full HP");
    }

    internal static void MovementRequiresUsefulReachableRoute()
    {
        foreach (var (remaining, cost, extra) in new[] { (-1, 4, 2), (3, 0, 2), (3, 2, 2), (3, 3, 2), (3, 6, 2), (3, int.MaxValue, 2), (3, 4, 0), (3, 4, -1), (3, 4, int.MaxValue) })
            Check.Near(0, ItemEvaluation.MovementScore(remaining, cost, extra), $"unsupported route remaining={remaining}, cost={cost}, extra={extra}");
        Check.Near(2, ItemEvaluation.MovementScore(3, 5, 2), "exactly reachable route");
        Check.Near(1.5f, ItemEvaluation.MovementScore(3, 4, 2), "partially needed boost");
        Check.Near(2, ItemEvaluation.MovementScore(0, 2, 2), "boost can enable movement from zero");
    }

    internal static void MovementHandlesLargeCostsWithoutOverflow()
    {
        Check.Near(1.5f, ItemEvaluation.MovementScore(int.MaxValue - 2, int.MaxValue - 1, 2), "remaining plus boost may exceed int range");
        Check.Near(2, ItemEvaluation.MovementScore(0, int.MaxValue - 1, int.MaxValue - 1), "large finite route remains valid");
    }

    internal static void AttackBoostRespectsShieldAndKillBreakpoints()
    {
        Check.Near(0, ItemEvaluation.AttackScore(2, 5, 10, 2, false, false, false), "boost still absorbed by shield");
        Check.Near(1, ItemEvaluation.AttackScore(2, 3, 10, 2, false, false, false), "only post-shield gain counts");
        Check.Near(3, ItemEvaluation.AttackScore(3, 1, 3, 1, false, false, false), "one damage reaching kill earns breakpoint bonus");
        Check.Near(0, ItemEvaluation.AttackScore(5, 0, 3, 2, false, false, false), "already lethal attack needs no boost");
        Check.Near(3, ItemEvaluation.AttackScore(4, 0, 5, 20, false, false, false), "overkill capped at remaining target health");
        Check.Near(2, ItemEvaluation.AttackScore(3, -5, 10, 2, false, false, false), "negative shield treated as zero");
    }

    internal static void ConsumedAttackRequiresMeaningfulGain()
    {
        Check.Near(0, ItemEvaluation.AttackScore(3, 0, 10, 1, false, false, true), "save consumed boost for one nonlethal damage");
        Check.Near(2, ItemEvaluation.AttackScore(3, 0, 10, 2, false, false, true), "two damage meets consumed threshold");
        Check.Near(3, ItemEvaluation.AttackScore(3, 0, 4, 1, false, false, true), "one damage kill bypasses consumed threshold");
        Check.Near(1, ItemEvaluation.AttackScore(3, 0, 10, 1, false, false, false), "spent boost accepts one damage");
        Check.Near(0, ItemEvaluation.AttackScore(3, 0, 10, 1, true, false, true), "advantage does not bypass consumed gain threshold");
    }

    internal static void AdvantageRequiresUsefulNonredundantAttack()
    {
        Check.Near(0, ItemEvaluation.AttackScore(2, 0, 10, 0, true, false, false), "attack below three does not justify advantage");
        Check.Near(1.5f, ItemEvaluation.AttackScore(3, 0, 10, 0, true, false, false), "attack of three meets advantage threshold");
        Check.Near(0, ItemEvaluation.AttackScore(3, 0, 10, 0, true, true, false), "redundant advantage has no value");
        Check.Near(0, ItemEvaluation.AttackScore(3, 3, 10, 0, true, false, false), "fully shielded attack has no advantage value");
        Check.Near(0, ItemEvaluation.AttackScore(3, 0, 3, 0, true, false, false), "already lethal attack has no advantage value");
        Check.Near(0, ItemEvaluation.AttackScore(3, 0, 10, 0, true, false, true), "advantage alone does not spend consumed item");
        Check.Near(3.5f, ItemEvaluation.AttackScore(3, 0, 10, 2, true, false, true), "advantage combines with sufficient damage gain");
    }

    internal static void AttackRejectsInvalidInputsAndHandlesLargeValues()
    {
        foreach (var (damage, health, extra) in new[] { (0, 5, 2), (-1, 5, 2), (3, 0, 2), (3, -1, 2), (3, 5, -1), (3, 5, int.MaxValue), (int.MaxValue, 5, 2) })
            Check.Near(0, ItemEvaluation.AttackScore(damage, 0, health, extra, true, false, false), $"invalid attack damage={damage}, hp={health}, extra={extra}");
        Check.Near(12, ItemEvaluation.AttackScore(int.MaxValue - 1, int.MaxValue, 10, 20, false, false, true), "boost arithmetic does not overflow before shield");
    }

    internal static void AttackBoostIsMonotonicAndCappedByHealth()
    {
        foreach (bool consumed in new[] { false, true })
        for (int health = 1; health <= 20; health++)
        for (int damage = 1; damage <= 20; damage++)
        for (int shield = 0; shield <= 5; shield++)
        {
            float previous = 0;
            for (int extra = 0; extra <= 25; extra++)
            {
                float score = ItemEvaluation.AttackScore(damage, shield, health, extra, false, false, consumed);
                string context = $"damage={damage}, shield={shield}, hp={health}, boost={extra}, consumed={consumed}";
                Check.True(score >= previous, $"stronger boost lost value: {context}");
                Check.True(score <= Math.Max(0, health - Math.Max(0, damage - shield)) + 2, $"uncapped overkill: {context}");
                previous = score;
            }
        }
    }
}
