using System;

namespace GloomhavenPartyAI
{
    // Tracks UI/queue waiting separately from synchronous planner work on the shared game thread.
    internal sealed class DecisionClock
    {
        private readonly Func<double> seconds;
        private double planningSeconds;

        internal DecisionClock(Func<double> seconds) { this.seconds = seconds; }
        internal double Now => seconds() - planningSeconds;
        internal void Reset() { planningSeconds = 0; }

        internal void ExcludePlanning(double elapsed)
        {
            if (elapsed > 0 && !double.IsNaN(elapsed) && !double.IsInfinity(planningSeconds + elapsed))
                planningSeconds += elapsed;
        }
    }
}
