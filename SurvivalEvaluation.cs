using System;

namespace GloomhavenPartyAI
{
    // Numeric heuristics only; callers supply information from the revealed board.
    internal static class SurvivalEvaluation
    {
        internal static float EnemyDamage(int distance, bool hasLineOfSight, int movement,
            int range, int attack, bool disabled, bool immobilized, bool hasActed)
        {
            if (disabled || attack <= 0 || distance < 0)
            {
                return 0f;
            }
            range = Math.Max(1, range);
            movement = immobilized ? 0 : Math.Max(0, movement);
            float reach;
            if (distance <= range && hasLineOfSight)
            {
                reach = 1f;
            }
            else if (movement > 0 && distance <= (long)range + movement)
            {
                // LOS from the current hex is not proof of LOS after moving around a corner.
                reach = hasLineOfSight ? 0.8f : 0.35f;
            }
            else
            {
                return 0f;
            }
            return attack * reach * (hasActed ? 0.65f : 1f);
        }

        internal static float ExposurePenalty(float futureDamage, int health, int maxHealth,
            int cyclingCards)
        {
            if (futureDamage <= 0f)
            {
                return 0f;
            }
            float fragility = maxHealth > 0 && health * 2L <= maxHealth ? 1.5f : 1f;
            float fatigue = cyclingCards <= 4 ? 1.4f : 1f;
            float lethal = Math.Max(0f, futureDamage - Math.Max(1, health) * 0.6f) * 5f;
            return (futureDamage * 3f + lethal) * fragility * fatigue;
        }

        internal static float PositionValue(float exposure, float support, int distanceMoved)
        {
            return support - exposure - Math.Max(0, distanceMoved) * 0.05f;
        }

        internal static float ApproachValue(int currentPathCost, int remainingPathCost,
            float futureDamage, int health, int maxHealth, int cyclingCards)
        {
            if (currentPathCost <= 0 || currentPathCost == int.MaxValue || remainingPathCost < 0 ||
                remainingPathCost >= currentPathCost || health <= 0 || maxHealth <= 0 ||
                health * 2L <= maxHealth || cyclingCards < 2 || float.IsNaN(futureDamage) || futureDamage < 0f)
            {
                return 0f;
            }
            // Progress buys only a bounded exposure budget, never a lethal exchange. Fatigued
            // actors reserve more HP because absorbing damage with cards is especially expensive.
            float riskBudget = health * (cyclingCards <= 4 ? 0.1f : 0.25f);
            if (futureDamage > riskBudget)
            {
                return 0f;
            }
            // Add to PositionValue, which still pays the complete endpoint exposure penalty.
            return Math.Min(3, currentPathCost - remainingPathCost) * 7f;
        }

        internal static bool ShouldOpenDoor(bool nearbyEnemies, bool partyNeedsRest,
            bool partyScattered, bool usefulRemainingAction, bool openerDisabled)
        {
            return !nearbyEnemies && !partyNeedsRest && !partyScattered &&
                usefulRemainingAction && !openerDisabled;
        }

        internal static bool IsUnderThreat(float futureDamage, float exposure)
        {
            return futureDamage >= 1f || exposure >= 3f;
        }
    }
}
