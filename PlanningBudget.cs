using System;
using System.Diagnostics;

namespace GloomhavenPartyAI
{
    // One synchronous invocation, never a timer or a background game-state reader.
    internal sealed class PlanningBudget
    {
        internal const int MaxPathQueries = 32;
        internal const int MaxLosQueries = 128;
        internal const int MaxCandidateSamples = 512;
        internal const int MaxCards = 16;
        internal const int MaxDestinations = 12;
        internal const int MaxSampleRadius = 6;
        internal const int MaxApproachGoals = 8;
        internal const int MaxRefinedPlans = 4;
        internal const int MaxMilliseconds = 50;

        private readonly int pathLimit;
        private readonly int losLimit;
        private readonly int sampleLimit;
        private readonly Func<long> timestamp;
        private readonly long started;
        private readonly long duration;
        internal int PathQueries { get; private set; }
        internal int LosQueries { get; private set; }
        // Admitted pairs, coordinates, refinements and endpoints, not arithmetic comparisons.
        internal int CandidateSamples { get; private set; }
        internal int CacheHits { get; private set; }
        internal bool BudgetExhausted { get; private set; }
        internal bool TimeBudgetExceeded { get; private set; }

        internal PlanningBudget(int pathQueries = MaxPathQueries, int losQueries = MaxLosQueries,
            int candidateSamples = MaxCandidateSamples, int maxMilliseconds = MaxMilliseconds,
            Func<long> timestamp = null)
        {
            pathLimit = Math.Max(0, Math.Min(MaxPathQueries, pathQueries));
            losLimit = Math.Max(0, Math.Min(MaxLosQueries, losQueries));
            sampleLimit = Math.Max(0, Math.Min(MaxCandidateSamples, candidateSamples));
            // Tests may supply a monotonic clock in Stopwatch ticks. Production always uses the
            // real clock; neither time nor operation limits can be raised through this constructor.
            this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
            duration = (long)Math.Max(0, Math.Min(MaxMilliseconds, maxMilliseconds)) * Stopwatch.Frequency / 1000;
            started = this.timestamp();
        }

        internal bool CheckTime()
        {
            if (TimeBudgetExceeded) return false;
            if (timestamp() - started < duration) return true;
            TimeBudgetExceeded = true;
            BudgetExhausted = true;
            return false;
        }

        internal bool TryPathQuery()
        {
            if (!CheckTime()) return false;
            if (PathQueries >= pathLimit) { BudgetExhausted = true; return false; }
            PathQueries++;
            return true;
        }

        internal bool TryLosQuery()
        {
            if (!CheckTime()) return false;
            if (LosQueries >= losLimit) { BudgetExhausted = true; return false; }
            LosQueries++;
            return true;
        }

        internal bool TrySample()
        {
            if (!CheckTime()) return false;
            if (CandidateSamples >= sampleLimit) { BudgetExhausted = true; return false; }
            CandidateSamples++;
            return true;
        }

        internal void HitCache() { CacheHits++; CheckTime(); }
    }
}
