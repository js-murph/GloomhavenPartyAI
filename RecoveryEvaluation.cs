using System;

namespace GloomhavenPartyAI
{
    // Pure endurance estimates, independent of game state and attack-modifier RNG.
    internal static class RecoveryEvaluation
    {
        internal static float RecoveryScore(int recoverableCards, int handCards, int roundCards,
            int discardedCards, bool companionAlreadyPlayed = false)
        {
            if (recoverableCards < 0 || handCards < 0 || roundCards < 0 || discardedCards < 0 ||
                (companionAlreadyPlayed ? roundCards < 1 : (long)handCards + roundCards < 2))
            {
                return 0f;
            }

            // Normalize card selection and action selection to the same end-of-turn snapshot.
            // After the first action its card has already left Round; use its actual destination
            // pile rather than reserving another Hand card or inventing another discard.
            // Round cards are not another future hand. One of them pays for recovery permanently.
            long committed = companionAlreadyPlayed ? 0 : Math.Max(0, 2 - (long)roundCards);
            long hand = handCards - committed;
            long discard = (long)discardedCards + roundCards + committed;
            double before = PlayableTurns(hand, discard);
            double after = PlayableTurns(hand + recoverableCards, discard - 1);
            double gained = after - before;

            // A future two-action turn is discounted to 4 points (one immediate damage point).
            // Cap credit at 12 extra turns: distant endurance must not dominate immediate tactics.
            // Reserve three future turns (12 points) for keeping the one-shot option. This avoids
            // burning it for a one-card hand-parity gain. Up to two basic Attack 2 actions (16)
            // reward avoiding exhaustion, including pairs still available through rests. Range: -60..52.
            float urgency = gained > 0 && recoverableCards > 0 ? 16f / (1f + (float)before) : 0f;
            return 4f * (float)Math.Max(-12d, Math.Min(12d, gained)) + urgency - 12f;
        }

        internal static float RetentionValue(int recoverableCards, int cyclingCards)
        {
            if (recoverableCards < 0 || cyclingCards <= 0)
            {
                return 0f;
            }
            // Keep the recovery card and a companion. Other cycling cards may become ordinary
            // losses later, so an unused recovery remains valuable even with an empty Lost pile.
            // Three basic Attack 2 actions reserve the option (24); each potential recovered card
            // adds one such action (8), capped at six cards. Finite 24..72, never a loss veto.
            long potential = (long)recoverableCards + Math.Max(0L, (long)cyclingCards - 2);
            return 24f + 8f * Math.Min(6L, potential);
        }

        private static double PlayableTurns(long hand, long discard)
        {
            // Play all pairs, then lose one card per rest. Sum floor(k/2), k=1..N-1.
            // This assumes no further voluntary losses and does not count long-rest waiting turns.
            double remaining = Math.Max(0L, hand + discard - 1);
            return hand / 2 + Math.Floor(remaining * remaining / 4d);
        }
    }
}
