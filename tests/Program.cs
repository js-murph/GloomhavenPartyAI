internal static class Program
{
    private static int Main()
    {
        Action[] tests =
        [
            TacticalTests.ZeroDamageAndDeadTargetsHaveNoAttackValue,
            TacticalTests.EffectiveDamageAndKillTiming,
            TacticalTests.AttackStrengthIsMonotonicAndOverkillIsHarmless,
            TacticalTests.AdjacentRangedDisadvantageIsConservative,
            TacticalTests.ControlStillMattersAfterTargetActs,
            TacticalTests.ConditionsRespectProjectedDeath,
            TacticalTests.LossPenaltyTracksRemainingCycles,
            TacticalTests.FutureCardValueIncludesBothHalvesAndInitiative,
            TacticalTests.RoleScarcityDecreasesWithOtherProviders,
            TacticalTests.InitiativeRespondsToPlannedActionsAndPressure,
            ItemTests.HealingRejectsInvalidAndWastefulUse,
            ItemTests.HealingCriticalHealthAndConditionThresholds,
            ItemTests.MovementRequiresUsefulReachableRoute,
            ItemTests.MovementHandlesLargeCostsWithoutOverflow,
            ItemTests.AttackBoostRespectsShieldAndKillBreakpoints,
            ItemTests.ConsumedAttackRequiresMeaningfulGain,
            ItemTests.AdvantageRequiresUsefulNonredundantAttack,
            ItemTests.AttackRejectsInvalidInputsAndHandlesLargeValues,
            ItemTests.AttackBoostIsMonotonicAndCappedByHealth,
            DiagnosticJsonTests.EmptyNestedAndNullValuesAreValidJson,
            DiagnosticJsonTests.KeysAndValuesEscapeQuotesAndBackslashes,
            DiagnosticJsonTests.AllControlCharactersAndNonAsciiRoundTrip,
            DiagnosticJsonTests.NumbersIgnoreCurrentCulture,
            DiagnosticJsonTests.MalformedSurrogatesAreReplacedAndValidPairsPreserved,
            DiagnosticJsonTests.ToStringDoesNotCloseOrMutateTheBuilder
        ];

        int failures = 0;
        foreach (Action test in tests)
        {
            string name = $"{test.Method.DeclaringType!.Name}.{test.Method.Name}";
            try
            {
                test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception}");
            }
        }

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} test cases passed; {failures} failed.");
        return failures == 0 ? 0 : 1;
    }
}

internal static class Check
{
    internal static void True(bool value, string context)
    {
        if (!value) throw new InvalidOperationException(context);
    }

    internal static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected <{expected}>, actual <{actual}>");
    }

    internal static void Near(float expected, float actual, string context)
    {
        if (!float.IsFinite(actual) || Math.Abs(expected - actual) > 0.0001f)
            throw new InvalidOperationException($"{context}: expected {expected}, actual {actual}");
    }
}
