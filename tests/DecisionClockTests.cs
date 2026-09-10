using GloomhavenPartyAI;

internal static class DecisionClockTests
{
    internal static void OneActorsThirtySevenSecondPlanDoesNotExpireAnotherActorsUiDeadline()
    {
        double realSeconds = 100;
        var clock = new DecisionClock(() => realSeconds);
        double actorAQueueDeadline = clock.Now + 15;
        double actorBUiDeadline = clock.Now + 3;
        realSeconds += 0.75;

        // Actor B cannot poll while actor A owns the shared thread. Exclude the measured
        // work on return, not the nominal 50ms planner allowance and not B's prior UI wait.
        double plannerStarted = realSeconds;
        realSeconds += 37;
        clock.ExcludePlanning(realSeconds - plannerStarted);
        Check.True(realSeconds >= actorBUiDeadline, "raw time reproduces B's starvation timeout");
        Check.Equal(100.75, clock.Now, "all 37 seconds of actual A work excluded; prior wait retained");
        Check.True(clock.Now < actorBUiDeadline && clock.Now < actorAQueueDeadline,
            "neither actor's outstanding deadline charges synchronous planning");

        realSeconds += 2;
        Check.True(clock.Now < actorBUiDeadline, "B still has a quarter second of its own UI allowance");
        realSeconds += 0.25;
        Check.Equal(actorBUiDeadline, clock.Now, "B expires after exactly three seconds of non-planner waiting");
        Check.True(clock.Now < actorAQueueDeadline, "A's longer queue allowance is independent of B's deadline");
    }

    internal static void UiAndQueueWaitingStillAdvanceBetweenPlanningCalls()
    {
        double realSeconds = 0;
        var clock = new DecisionClock(() => realSeconds);
        double uiDeadline = clock.Now + 3;
        double queueDeadline = clock.Now + 15;
        realSeconds += 1.5;
        Check.Equal(1.5, clock.Now, "ordinary UI waiting advances without exclusions");
        realSeconds += 4;
        clock.ExcludePlanning(4);
        realSeconds += 1;
        realSeconds += 8;
        clock.ExcludePlanning(8);
        Check.Equal(2.5, clock.Now, "multiple actors' planning excludes only their work intervals");
        realSeconds += 0.5;
        Check.Equal(uiDeadline, clock.Now, "UI deadline still expires");
        realSeconds += 12;
        Check.Equal(queueDeadline, clock.Now, "rule/message queue waiting is not excluded");
        Check.Equal(27.0, realSeconds, "fifteen waiting seconds plus twelve planning seconds");
    }

    internal static void ResetDiscardsPriorScenarioExclusions()
    {
        double realSeconds = 200;
        var clock = new DecisionClock(() => realSeconds);
        clock.ExcludePlanning(37);
        Check.Equal(163.0, clock.Now, "fixture has prior scenario work");
        clock.Reset();
        clock.Reset();
        Check.Equal(200.0, clock.Now, "reset is idempotent and preserves the injected wall-clock origin");
        double deadline = clock.Now + 3;
        realSeconds += 1;
        clock.ExcludePlanning(1);
        Check.Equal(200.0, clock.Now, "new scenario accumulates its own exclusions");
        realSeconds += 3;
        Check.Equal(deadline, clock.Now, "old exclusions cannot extend a new scenario deadline");
    }

    internal static void InvalidAndOverflowingDurationsDoNotCorruptTheClock()
    {
        var clock = new DecisionClock(() => 100);
        clock.ExcludePlanning(2.5);
        foreach (double invalid in new[] { 0.0, -1, -double.MaxValue, double.NaN, double.NegativeInfinity, double.PositiveInfinity })
        {
            clock.ExcludePlanning(invalid);
            Check.Equal(97.5, clock.Now, "invalid duration is ignored without erasing prior work: " + invalid);
        }
        clock.ExcludePlanning(0.5);
        Check.Equal(97.0, clock.Now, "valid fractional duration still works after invalid inputs");

        var extreme = new DecisionClock(() => double.MaxValue);
        extreme.ExcludePlanning(double.MaxValue);
        Check.Equal(0.0, extreme.Now, "largest finite duration is accepted");
        extreme.ExcludePlanning(double.MaxValue);
        Check.Equal(0.0, extreme.Now, "overflowing accumulated duration is ignored, not converted to infinity");
        extreme.Reset();
        Check.Equal(double.MaxValue, extreme.Now, "reset recovers the original finite clock");
    }
}
