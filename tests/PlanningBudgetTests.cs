using System.Diagnostics;
using GloomhavenPartyAI;

internal static class PlanningBudgetTests
{
    internal static void RequestedLimitsCannotRaiseHardCaps()
    {
        var budget = new PlanningBudget(int.MaxValue, int.MaxValue, int.MaxValue, timestamp: () => 0);
        for (int index = 0; index < 1024; index++)
        {
            Check.Equal(index < 32, budget.TryPathQuery(), "path admission");
            Check.Equal(index < 128, budget.TryLosQuery(), "LOS admission");
            Check.Equal(index < 512, budget.TrySample(), "sample admission");
            Check.True(budget.PathQueries <= 32 && budget.LosQueries <= 128 && budget.CandidateSamples <= 512,
                "hard caps hold after every operation, not just at return");
        }
        Check.Equal(32, budget.PathQueries, "hard path cap");
        Check.Equal(128, budget.LosQueries, "hard LOS cap");
        Check.Equal(512, budget.CandidateSamples, "hard sample cap");
        Check.True(budget.BudgetExhausted && !budget.TimeBudgetExceeded, "operation exhaustion is not time expiry");
    }

    internal static void LowerAndNegativeLimitsAreIndependent()
    {
        foreach (int limit in new[] { int.MinValue, -1, 0, 1, 3 })
        {
            var budget = new PlanningBudget(limit, limit, limit, timestamp: () => 0);
            for (int index = 0; index < 5; index++)
            {
                Check.Equal(index < Math.Max(0, limit), budget.TryPathQuery(), "lower path limit");
                Check.Equal(index < Math.Max(0, limit), budget.TryLosQuery(), "lower LOS limit");
                Check.Equal(index < Math.Max(0, limit), budget.TrySample(), "lower sample limit");
            }
            Check.Equal(Math.Max(0, limit), budget.PathQueries, "path count");
            Check.Equal(Math.Max(0, limit), budget.LosQueries, "LOS count");
            Check.Equal(Math.Max(0, limit), budget.CandidateSamples, "sample count");
        }
        var independent = new PlanningBudget(0, 1, 1, timestamp: () => 0);
        Check.True(!independent.TryPathQuery() && independent.TryLosQuery() && independent.TrySample(),
            "exhausting paths does not spend LOS or candidate allowance");
    }

    internal static void InjectedClockEnforcesFiftyMillisecondStickyDeadline()
    {
        long now = 7 * Stopwatch.Frequency;
        long started = now;
        var budget = new PlanningBudget(maxMilliseconds: int.MaxValue, timestamp: () => now);
        Check.True(budget.TryPathQuery(), "first query is admitted");
        now = started + 50 * Stopwatch.Frequency / 1000 - 1;
        Check.True(budget.CheckTime(), "one tick before the clamped 50ms deadline");
        now++;
        Check.True(!budget.TryPathQuery() && !budget.TryLosQuery() && !budget.TrySample(),
            "exactly 50ms rejects all query categories");
        Check.True(budget.TimeBudgetExceeded && budget.BudgetExhausted, "deadline flags");
        now = started;
        Check.True(!budget.CheckTime(), "expiry cannot be cleared by a clock moving backward");
        Check.Equal(1, budget.PathQueries, "no path after expiry");
        Check.Equal(0, budget.LosQueries, "no LOS after expiry");
        Check.Equal(0, budget.CandidateSamples, "no sample after expiry");
    }

    internal static void DeadlineAfterOneCallStopsEveryCategory()
    {
        foreach (int category in new[] { 0, 1, 2 })
        {
            long now = 0;
            var budget = new PlanningBudget(timestamp: () => now);
            Check.True(category == 0 ? budget.TryPathQuery() : category == 1 ? budget.TryLosQuery() : budget.TrySample(),
                "one admitted operation");
            now = Stopwatch.Frequency / 10;
            Check.True(!budget.CheckTime(), "simulated 100ms synchronous call consumes the deadline");
            for (int index = 0; index < 10; index++)
                Check.True(!budget.TryPathQuery() && !budget.TryLosQuery() && !budget.TrySample(), "no subsequent work");
            Check.Equal(1, budget.PathQueries + budget.LosQueries + budget.CandidateSamples, "only the first call counted");
        }
    }

    internal static void ImmediateDeadlinesAndCacheHitsDoNotAdmitQueries()
    {
        foreach (int milliseconds in new[] { int.MinValue, -1, 0 })
        {
            var budget = new PlanningBudget(maxMilliseconds: milliseconds, timestamp: () => 0);
            budget.HitCache();
            Check.True(budget.TimeBudgetExceeded && budget.BudgetExhausted, "cache lookup checks immediate deadline");
            Check.Equal(1, budget.CacheHits, "cache hit recorded");
            Check.True(!budget.TryPathQuery() && !budget.TryLosQuery() && !budget.TrySample(), "immediate deadline");
            Check.Equal(0, budget.PathQueries + budget.LosQueries + budget.CandidateSamples, "no query charged");
        }
    }
}
