using System;

namespace GloomhavenPartyAI
{
    internal static class ItemEvaluation
    {
        internal static float HealingScore(int health, int maxHealth, int healing,
            bool poisoned, bool wounded, bool consumed)
        {
            if (health <= 0 || maxHealth <= 0 || healing <= 0 || healing == int.MaxValue)
            {
                return 0f;
            }

            int missing = Math.Max(0, maxHealth - health);
            int restored = poisoned ? 0 : Math.Min(missing, healing);
            // Poison removal replaces HP restoration; wound removal does not.
            if (consumed && !poisoned && !wounded && missing < healing &&
                (double)health / maxHealth > 0.35)
            {
                return 0f;
            }
            return restored + (poisoned ? 3f : 0f) + (wounded ? 2f : 0f) +
                (restored > 0 && (double)health / maxHealth <= 0.35 ? 2f : 0f);
        }

        internal static float MovementScore(int remainingMoves, int usefulPathCost, int extraMoves)
        {
            // The caller supplies a safe, useful route's game-calculated cost, not hex distance.
            if (remainingMoves < 0 || usefulPathCost <= remainingMoves ||
                usefulPathCost == int.MaxValue || extraMoves <= 0 || extraMoves == int.MaxValue ||
                (long)remainingMoves + extraMoves < usefulPathCost)
            {
                return 0f;
            }
            return 1f + (float)(usefulPathCost - remainingMoves) / extraMoves;
        }

        internal static float AttackScore(int damageBeforeShield, int shield, int targetHealth,
            int extraAttack, bool advantage, bool alreadyHasAdvantage, bool consumed)
        {
            if (damageBeforeShield <= 0 || targetHealth <= 0 || extraAttack < 0 ||
                extraAttack == int.MaxValue || damageBeforeShield == int.MaxValue)
            {
                return 0f;
            }
            long damage = Math.Max(0L, (long)damageBeforeShield - Math.Max(0, shield));
            long boostedDamage = Math.Max(0L,
                (long)damageBeforeShield + extraAttack - Math.Max(0, shield));
            long gain = Math.Min(targetHealth, boostedDamage) - Math.Min(targetHealth, damage);
            bool killBreakpoint = damage < targetHealth && boostedDamage >= targetHealth;
            float advantageValue = advantage && !alreadyHasAdvantage && damageBeforeShield >= 3 &&
                damage > 0 && damage < targetHealth ? 1.5f : 0f;
            // Save one-shot power boosts unless they add at least two damage or reach a kill breakpoint.
            if (consumed && gain < 2 && !killBreakpoint)
            {
                return 0f;
            }
            return gain + (killBreakpoint ? 2f : 0f) + advantageValue;
        }
    }
}
