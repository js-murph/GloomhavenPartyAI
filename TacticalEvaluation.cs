using System;

namespace GloomhavenPartyAI
{
    // Kept independent of game assemblies so scoring invariants can be tested in isolation.
    internal static class TacticalEvaluation
    {
        internal static bool MatchesCommittedAction(Guid plannedId, Guid? selectedId,
            int plannedType, int selectedType)
        {
            return plannedId != Guid.Empty && selectedId == plannedId && plannedType == selectedType;
        }

        internal static int MovementRulesKey(bool jump, bool fly, bool ignoreDifficultTerrain,
            bool ignoreHazardousTerrain, bool ignoreBlockedTileMoveCost, bool carryOtherActors)
        {
            return (jump ? 1 : 0) | (fly ? 2 : 0) | (ignoreDifficultTerrain ? 4 : 0) |
                (ignoreHazardousTerrain ? 8 : 0) | (ignoreBlockedTileMoveCost ? 16 : 0) |
                (carryOtherActors ? 32 : 0);
        }

        internal static float EstimatedDamage(int damage, bool rangedAdjacent)
        {
            // A conservative proxy, not an attack-modifier deck simulation.
            return Math.Max(0, damage) * (rangedAdjacent ? 0.75f : 1f);
        }

        internal static bool IsProjectedKill(int damage, int health, bool rangedAdjacent)
        {
            return health > 0 && EstimatedDamage(damage, rangedAdjacent) >= health;
        }

        internal static float AttackValue(int damage, int health, bool targetHasActed,
            bool rangedAdjacent = false)
        {
            if (health <= 0)
            {
                return 0f;
            }
            float score = Math.Min(health, EstimatedDamage(damage, rangedAdjacent)) * 4f;
            if (IsProjectedKill(damage, health, rangedAdjacent))
            {
                score += targetHasActed ? 14f : 26f;
            }
            return score;
        }

        internal static float ControlConditionValue(float value, bool targetHasActed)
        {
            // Applied after a turn, control still denies the target's next turn.
            return Math.Max(0f, value) * (targetHasActed ? 0.85f : 1f);
        }

        internal static float ConditionValue(float value, bool projectedKill, bool persistsAfterDeath)
        {
            return projectedKill && !persistsAfterDeath ? 0f : Math.Max(0f, value);
        }

        internal static float LossPenalty(int cyclingCards)
        {
            return Math.Max(1, (cyclingCards - 1) / 2) * 5f;
        }

        internal static float FutureCardValue(float top, float bottom, int initiative)
        {
            return Math.Max(top, bottom) + Math.Min(top, bottom) * 0.5f +
                (initiative <= 25 || initiative >= 75 ? 1.5f : 0f);
        }

        internal static float RoleScarcityValue(int otherProviders)
        {
            return 3f / (1 + Math.Max(0, otherProviders));
        }

        internal static float InitiativeValue(int initiative, float plannedAttackValue,
            float plannedHealValue, bool underPressure)
        {
            float early = (100 - Math.Max(1, Math.Min(99, initiative))) / 100f;
            float urgency = Math.Max(0f, plannedAttackValue) * 0.1f +
                Math.Max(0f, plannedHealValue) * 0.15f + (underPressure ? 2f : 0f);
            return urgency > 0f ? early * urgency : (1f - early) * 0.25f;
        }
    }
}
