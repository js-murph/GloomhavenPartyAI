using GloomhavenPartyAI;

internal static class SurvivalTests
{
    internal static void FourHealthBetweenTwoAttackersPrefersSafeRetreat()
    {
        float adjacent = 2 * SurvivalEvaluation.EnemyDamage(1, true, 2, 1, 3, false, false, false);
        float safe = 2 * SurvivalEvaluation.EnemyDamage(4, true, 2, 1, 3, false, false, false);
        float stayExposure = SurvivalEvaluation.ExposurePenalty(adjacent, 4, 10, 4);
        float retreatExposure = SurvivalEvaluation.ExposurePenalty(safe, 4, 10, 4);
        float stay = SurvivalEvaluation.PositionValue(stayExposure, 0, 0);
        float retreat = SurvivalEvaluation.PositionValue(retreatExposure, -1, 3);
        Check.True(adjacent > 4 && safe == 0, "two adjacent Attack 3 enemies threaten lethal damage; retreat is out of reach");
        Check.True(retreat > stay + TacticalEvaluation.AttackValue(2, 6, false),
            "at 4 HP, safe retreat beats staying for nonlethal Attack 2 despite distance and lost support");
        Check.True(SurvivalEvaluation.IsUnderThreat(adjacent, stayExposure) &&
            !SurvivalEvaluation.IsUnderThreat(safe, retreatExposure), "retreat also removes short-rest threat");
    }

    internal static void LineOfSightAndMovementBoundThreatReach()
    {
        float direct = SurvivalEvaluation.EnemyDamage(1, true, 2, 1, 3, false, false, false);
        float approach = SurvivalEvaluation.EnemyDamage(3, true, 2, 1, 3, false, false, false);
        float corner = SurvivalEvaluation.EnemyDamage(3, false, 2, 1, 3, false, false, false);
        Check.True(direct > approach && approach > corner && corner > 0,
            "current LOS is certain, movement less certain, and rounding a corner speculative but possible");
        Check.Near(0, SurvivalEvaluation.EnemyDamage(4, true, 2, 1, 3, false, false, false),
            "one hex beyond move plus range is safe");
        Check.Near(0, SurvivalEvaluation.EnemyDamage(1, false, 0, 1, 3, false, false, false),
            "in range without LOS or movement cannot attack");
    }

    internal static void RangeAndImmobilizeDoNotDisableAnInRangeAttack()
    {
        float ranged = SurvivalEvaluation.EnemyDamage(3, true, 2, 3, 3, false, false, false);
        Check.Near(ranged, SurvivalEvaluation.EnemyDamage(3, true, 2, 3, 3, false, true, false),
            "immobilize does not prevent an in-range ranged attack");
        Check.True(SurvivalEvaluation.EnemyDamage(3, true, 2, 1, 3, false, false, false) > 0,
            "mobile melee enemy can approach");
        Check.Near(0, SurvivalEvaluation.EnemyDamage(3, true, 2, 1, 3, false, true, false),
            "immobilized melee enemy cannot close the gap");
        Check.Near(SurvivalEvaluation.EnemyDamage(1, true, 0, 1, 3, false, true, false),
            SurvivalEvaluation.EnemyDamage(1, true, -2, 0, 3, false, true, false),
            "zero-range base stats mean melee, and negative movement grants no extra reach");
        Check.Near(0, SurvivalEvaluation.EnemyDamage(3, false, 2, 3, 3, false, true, false),
            "immobilized ranged enemy still needs LOS");
    }

    internal static void StunAndActedFlagsHaveDifferentConsequences()
    {
        foreach (bool immobilized in new[] { false, true })
        {
            float waiting = SurvivalEvaluation.EnemyDamage(1, true, 2, 1, 3, false, immobilized, false);
            float acted = SurvivalEvaluation.EnemyDamage(1, true, 2, 1, 3, false, immobilized, true);
            Check.True(acted > 0 && acted < waiting, "already-acted enemy retains discounted next-round threat");
            foreach (bool hasActed in new[] { false, true })
                Check.Near(0, SurvivalEvaluation.EnemyDamage(1, true, 2, 1, 3, true, immobilized, hasActed),
                    "disabled flag (stun/sleep/disarm) prevents attacks regardless of turn or immobilize");
        }
    }

    internal static void LowHealthAndFewCardsIndependentlyIncreaseExposure()
    {
        float healthy = SurvivalEvaluation.ExposurePenalty(2, 10, 10, 8);
        float hurt = SurvivalEvaluation.ExposurePenalty(2, 5, 10, 8);
        float fatigued = SurvivalEvaluation.ExposurePenalty(2, 10, 10, 4);
        float both = SurvivalEvaluation.ExposurePenalty(2, 5, 10, 4);
        Check.True(hurt > healthy && fatigued > healthy && both > hurt && both > fatigued,
            "half health and four remaining cards each increase exposure independently");
        Check.Near(healthy, SurvivalEvaluation.ExposurePenalty(2, 6, 10, 5),
            "just above both thresholds has no fragility/fatigue surcharge");
        Check.True(SurvivalEvaluation.ExposurePenalty(6, 4, 10, 4) > both * 3,
            "damage threatening lethal harm adds more than the linear damage cost");
        Check.Near(0, SurvivalEvaluation.ExposurePenalty(0, 1, 10, 2), "fragility alone is not exposure");
        float previous = 0;
        for (int damage = 0; damage <= 20; damage++)
        {
            float next = SurvivalEvaluation.ExposurePenalty(damage, 4, 10, 4);
            Check.True(next >= previous, "more incoming damage cannot improve a position");
            previous = next;
        }
    }

    internal static void EachDoorGateIndependentlyBlocksOpening()
    {
        // Every bit is a blocker; bit 3 means the useful followup is absent.
        for (int blockers = 0; blockers < 32; blockers++)
            Check.Equal(blockers == 0, SurvivalEvaluation.ShouldOpenDoor(
                (blockers & 1) != 0, (blockers & 2) != 0, (blockers & 4) != 0,
                (blockers & 8) == 0, (blockers & 16) != 0), $"door blocker combination={blockers}");
    }

    internal static void RestCompletionAllowsReadyPartyToOpenDoor()
    {
        Check.True(!SurvivalEvaluation.ShouldOpenDoor(false, true, false, true, false),
            "grouped party with a followup attack waits while an ally needs rest");
        Check.True(SurvivalEvaluation.ShouldOpenDoor(false, false, false, true, false),
            "after rest restores readiness the same door can open");
        Check.True(!SurvivalEvaluation.ShouldOpenDoor(false, false, false, false, false),
            "rest completion alone does not justify an end-of-turn exploratory move");
    }

    internal static void EqualPositionsPreferStayingThenShorterMovement()
    {
        float exposure = SurvivalEvaluation.ExposurePenalty(1, 8, 10, 8);
        float stay = SurvivalEvaluation.PositionValue(exposure, 2, 0);
        float near = SurvivalEvaluation.PositionValue(exposure, 2, 1);
        float far = SurvivalEvaluation.PositionValue(exposure, 2, 3);
        Check.True(stay > near && near > far, "equal exposure/support prefers staying, then shorter movement");
        Check.Near(stay - near, (stay - far) / 3, "short-distance penalty scales with distance");
        Check.True(SurvivalEvaluation.PositionValue(exposure, 2.5f, 1) > stay,
            "a small real support improvement can overcome the tie-breaking distance penalty");
        Check.Near(stay, SurvivalEvaluation.PositionValue(exposure, 2, -1), "negative distance cannot reward movement");
    }

    internal static void ThreatThresholdsAndExtremeReachAreSafe()
    {
        Check.True(!SurvivalEvaluation.IsUnderThreat(0.99f, 2.99f), "below both threat thresholds");
        Check.True(SurvivalEvaluation.IsUnderThreat(1, 0), "damage alone can prevent a rest");
        Check.True(SurvivalEvaluation.IsUnderThreat(0, 3), "exposure alone can prevent a rest");
        Check.Near(0, SurvivalEvaluation.EnemyDamage(-1, true, 2, 1, 3, false, false, false), "invalid distance");
        Check.Near(0, SurvivalEvaluation.EnemyDamage(1, true, 2, 1, 0, false, false, false), "no attack");
        Check.True(SurvivalEvaluation.EnemyDamage(int.MaxValue, true, int.MaxValue, 1, 3,
            false, false, false) > 0, "move plus range does not overflow and hide a reachable threat");
    }

    internal static void VerifiedApproachOvercomesLimitedExposureWithoutMakingRiskFree()
    {
        float incoming = SurvivalEvaluation.EnemyDamage(3, true, 0, 3, 2, false, false, false);
        float safeStay = SurvivalEvaluation.PositionValue(0, 0, 0);
        float exposedEndpoint = SurvivalEvaluation.PositionValue(
            SurvivalEvaluation.ExposurePenalty(incoming, 10, 10, 6), 0, 2);
        float progress = SurvivalEvaluation.ApproachValue(4, 2, incoming, 10, 10, 6);
        Check.True(exposedEndpoint < safeStay && exposedEndpoint + progress > safeStay,
            "healthy Move 2 closes a verified melee route from five hexes away despite entering ranged Attack 2 reach");
        float safeProgress = SurvivalEvaluation.PositionValue(0, 0, 2) +
            SurvivalEvaluation.ApproachValue(4, 2, 0, 10, 10, 6);
        Check.True(safeProgress > exposedEndpoint + progress,
            "equal progress still prefers a safe route because the complete exposure cost is paid");
        float hurtEndpoint = SurvivalEvaluation.PositionValue(
            SurvivalEvaluation.ExposurePenalty(incoming, 2, 10, 6), 0, 2);
        Check.True(hurtEndpoint + SurvivalEvaluation.ApproachValue(4, 2, incoming, 2, 10, 6) < safeStay,
            "low HP does not buy the same charge with speculative future attack value");
    }

    internal static void ApproachCreditRequiresProgressAndIsBounded()
    {
        float oneStep = SurvivalEvaluation.ApproachValue(5, 4, 0, 10, 10, 6);
        float twoSteps = SurvivalEvaluation.ApproachValue(5, 3, 0, 10, 10, 6);
        float cap = SurvivalEvaluation.ApproachValue(5, 2, 0, 10, 10, 6);
        Check.True(oneStep > 0 && twoSteps > oneStep && cap > twoSteps,
            "more verified progress initially earns more approach value");
        Check.Near(cap, SurvivalEvaluation.ApproachValue(100, 0, 0, 10, 10, 6),
            "a long route cannot create unbounded credit for future attacks");
        Check.Near(cap, SurvivalEvaluation.ApproachValue(int.MaxValue - 1, 0, 0, 10, 10, 6),
            "large finite route costs remain capped");
        Check.Near(oneStep, SurvivalEvaluation.ApproachValue(100, 99, 0, 10, 10, 6),
            "credit depends on progress, not the absolute length of a detour");
        foreach (int remaining in new[] { 5, 6, int.MaxValue })
            Check.Near(0, SurvivalEvaluation.ApproachValue(5, remaining, 0, 10, 10, 6),
                "staying or circling without reducing remaining route cost earns nothing");
    }

    internal static void ApproachRiskBudgetRespondsToHealthAndCards()
    {
        float healthy = SurvivalEvaluation.ApproachValue(4, 2, 2, 10, 10, 6);
        Check.True(healthy > 0, "healthy cycling pool tolerates limited ranged damage");
        Check.Near(0, SurvivalEvaluation.ApproachValue(4, 2, 2, 10, 10, 4),
            "four cycling cards reserve a smaller HP budget");
        Check.True(SurvivalEvaluation.ApproachValue(4, 2, 1, 10, 10, 4) > 0,
            "fatigue still allows demonstrated progress at sufficiently low risk");
        Check.True(SurvivalEvaluation.ApproachValue(4, 2, 2.5f, 10, 10, 5) > 0,
            "healthy quarter-HP risk boundary is inclusive");
        Check.Near(0, SurvivalEvaluation.ApproachValue(4, 2, 2.51f, 10, 10, 5),
            "just over the risk budget rejects a charge");
        foreach (int health in new[] { 1, 2, 5 })
            Check.Near(0, SurvivalEvaluation.ApproachValue(4, 2, 0, health, 10, 6),
                "at or below half health prefers survival even if this endpoint looks safe");
        Check.Near(0, SurvivalEvaluation.ApproachValue(4, 2, 10, 10, 10, 6),
            "lethal exposure never buys progress");
        Check.Near(0, SurvivalEvaluation.ApproachValue(4, 2, 0, 10, 10, 1),
            "no future playable pair means no approach reward");
    }

    internal static void InvalidApproachInputsCannotBuyProgress()
    {
        foreach (var (current, remaining) in new[] { (0, 0), (-1, 0), (int.MaxValue, 2), (4, -1) })
            Check.Near(0, SurvivalEvaluation.ApproachValue(current, remaining, 0, 10, 10, 6),
                "invalid or unreachable route has no verified progress");
        foreach (float damage in new[] { -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Check.Near(0, SurvivalEvaluation.ApproachValue(4, 2, damage, 10, 10, 6),
                "invalid or unbounded exposure cannot justify approach");
        Check.Near(0, SurvivalEvaluation.ApproachValue(4, 2, 0, 0, 10, 6), "dead actor");
        Check.Near(0, SurvivalEvaluation.ApproachValue(4, 2, 0, 10, 0, 6), "invalid maximum health");
    }
}
