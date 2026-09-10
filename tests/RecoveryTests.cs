using GloomhavenPartyAI;

internal static class RecoveryTests
{
    internal static void R14RecoveryBeatsNonlethalAttackButNotAnImmediateKill()
    {
        float recovery = RecoveryEvaluation.RecoveryScore(3, 3, 2, 0);
        float attack = TacticalEvaluation.AttackValue(2, 6, false);
        Check.True(recovery > attack * 1.1f,
            "R14: lost=3 hand=3 round=2 discard=0 beats nonlethal Attack 2, even with attack-first credit");
        Check.True(recovery < TacticalEvaluation.AttackValue(2, 2, false),
            "endurance must not dominate an immediate kill before the enemy acts");
    }

    internal static void CardSelectionAndCommittedRoundHaveEquivalentEndurance()
    {
        Check.Near(RecoveryEvaluation.RecoveryScore(3, 3, 2, 0),
            RecoveryEvaluation.RecoveryScore(3, 5, 0, 0), "R14 preselection is the same future card pool");
        for (int lost = 0; lost <= 8; lost++)
        for (int hand = 2; hand <= 10; hand++)
        for (int discard = 0; discard <= 5; discard++)
        {
            float unselected = RecoveryEvaluation.RecoveryScore(lost, hand, 0, discard);
            Check.Near(unselected, RecoveryEvaluation.RecoveryScore(lost, hand - 1, 1, discard),
                $"one committed card: lost={lost} hand={hand} discard={discard}");
            Check.Near(unselected, RecoveryEvaluation.RecoveryScore(lost, hand - 2, 2, discard),
                $"both committed cards: lost={lost} hand={hand} discard={discard}");
        }
    }

    internal static void EmptyAndPrematureOneCardRecoveryDoNotSpendTheOption()
    {
        for (int hand = 0; hand <= 8; hand++)
        for (int discard = 0; discard <= 6; discard++)
            Check.True(RecoveryEvaluation.RecoveryScore(0, hand, 2, discard) < 0,
                $"empty Lost pile cannot repay permanent loss: hand={hand} discard={discard}");
        foreach (var (hand, round) in new[] { (5, 2), (7, 0), (3, 2), (5, 0) })
            Check.True(RecoveryEvaluation.RecoveryScore(1, hand, round, 0) < 0,
                $"one Lost card with a healthy cycling pool preserves the option: hand={hand} round={round}");
    }

    internal static void SecondActionPreservesTheActualCompanionPile()
    {
        float first = RecoveryEvaluation.RecoveryScore(3, 3, 2, 0, false);
        float second = RecoveryEvaluation.RecoveryScore(3, 3, 1, 1, true);
        Check.Near(18.666666f, second, "R14 after the companion is discarded");
        Check.Near(first, second, "the same end-of-turn pool does not reserve a second Hand card");
        Check.True(second > TacticalEvaluation.AttackValue(2, 6, false) * 1.1f,
            "recovery still beats a nonlethal attack after the companion acts");
        for (int lost = 0; lost <= 8; lost++)
        for (int hand = 0; hand <= 8; hand++)
        for (int discard = 0; discard <= 4; discard++)
            Check.Near(RecoveryEvaluation.RecoveryScore(lost, hand, 2, discard, false),
                RecoveryEvaluation.RecoveryScore(lost, hand, 1, discard + 1, true),
                $"discarding the companion preserves endurance: lost={lost} hand={hand} discard={discard}");
        Check.True(RecoveryEvaluation.RecoveryScore(3, 3, 1, 0, true) != second,
            "a lost rather than discarded companion must not invent a discard");
    }

    internal static void LastPairRescueStillWorksAfterTheCompanionActs()
    {
        float rescue = RecoveryEvaluation.RecoveryScore(2, 0, 1, 1, true);
        Check.Near(RecoveryEvaluation.RecoveryScore(2, 0, 2, 0, false), rescue,
            "last-pair rescue remains available with only Ether left in Round");
        Check.True(rescue > TacticalEvaluation.AttackValue(2, 6, false) * 1.1f,
            "recovering another hand pair beats spending the last top on nonlethal Attack 2");
        Check.Near(0, RecoveryEvaluation.RecoveryScore(2, 0, 1, 1, false),
            "the single remaining Round card is legal only in the second-action context");
        Check.True(RecoveryEvaluation.RecoveryScore(0, 0, 1, 1, true) < 0 &&
            RecoveryEvaluation.RecoveryScore(1, 0, 1, 1, true) < 0,
            "second-action context cannot turn empty or insufficient recovery into rescue");
        Check.Near(0, RecoveryEvaluation.RecoveryScore(2, 5, 0, 1, true),
            "a second action still requires a remaining Round card, not merely cards in Hand");
    }

    internal static void LastPairRecoveryRescuesAnotherTurn()
    {
        float insufficient = RecoveryEvaluation.RecoveryScore(1, 0, 2, 0);
        float rescue = RecoveryEvaluation.RecoveryScore(2, 0, 2, 0);
        Check.True(insufficient < 0 && rescue > 0,
            "last pair needs two recovered cards: one in hand plus one discard cannot survive a rest loss");
        Check.Near(rescue, RecoveryEvaluation.RecoveryScore(2, 2, 0, 0),
            "last-pair rescue is visible before round selection");
        Check.True(RecoveryEvaluation.RecoveryScore(3, 0, 2, 0) > rescue,
            "a third recovered card extends endurance beyond the rescued pair");
    }

    internal static void MoreRecoverableCardsNeverReduceValue()
    {
        for (int hand = 0; hand <= 8; hand++)
        for (int discard = 0; discard <= 8; discard++)
        {
            float previous = RecoveryEvaluation.RecoveryScore(0, hand, 2, discard);
            for (int lost = 1; lost <= 12; lost++)
            {
                float next = RecoveryEvaluation.RecoveryScore(lost, hand, 2, discard);
                Check.True(float.IsFinite(next) && next >= previous,
                    $"larger recovery pool regressed: lost={lost} hand={hand} discard={discard}");
                previous = next;
            }
        }
    }

    internal static void RetentionCountsOtherCyclingCardsWithoutCountingThePairTwice()
    {
        float pairOnly = RecoveryEvaluation.RetentionValue(0, 2);
        float ordinaryAttack = TacticalEvaluation.AttackValue(2, 6, false);
        Check.Near(pairOnly + 3 * ordinaryAttack, RecoveryEvaluation.RetentionValue(3, 2),
            "each of three Lost cards adds a future basic attack to the reserved option");
        Check.Near(RecoveryEvaluation.RetentionValue(3, 2), RecoveryEvaluation.RetentionValue(0, 5),
            "three OTHER cycling cards may become recoverable; Ether and its companion are not counted");
        Check.Near(RecoveryEvaluation.RetentionValue(2, 5), RecoveryEvaluation.RetentionValue(3, 4),
            "moving an ordinary cycling card into Lost preserves recovery potential");
        Check.Near(RecoveryEvaluation.RetentionValue(6, 2), RecoveryEvaluation.RetentionValue(20, 20),
            "distant potential is capped, not unbounded");
    }

    internal static void RetentionProtectsTheOptionButAllowsLastResortLoss()
    {
        float ordinary = TacticalEvaluation.FutureCardValue(8, 4, 50);
        float ether = ordinary + RecoveryEvaluation.RetentionValue(3, 5);
        var candidates = new[] { (Name: "ordinary", Value: ordinary), (Name: "Ether", Value: ether) };
        Check.Equal("ordinary", candidates.OrderBy(card => card.Value).First().Name,
            "retention protects recovery when an ordinary card can pay the loss");
        Check.True(float.IsFinite(ether), "retention is a finite premium, not a loss veto");
        Check.Equal("Ether", candidates.Where(card => card.Name == "Ether")
            .OrderBy(card => card.Value).First().Name, "the sole eligible card remains a last-resort loss");
        Check.True(ether < ordinary + 100, "a sufficiently valuable alternative can outweigh retention");
    }

    internal static void InvalidAndExtremeCountsRemainFinite()
    {
        foreach (var (lost, hand, round, discard) in new[]
            { (-1, 3, 2, 0), (3, -1, 2, 0), (3, 3, -1, 0), (3, 3, 2, -1), (3, 1, 0, 0) })
            Check.Near(0, RecoveryEvaluation.RecoveryScore(lost, hand, round, discard),
                "invalid counts or no playable pair cannot justify recovery");
        foreach (int hand in new[] { 0, int.MaxValue })
        {
            float score = RecoveryEvaluation.RecoveryScore(int.MaxValue, hand, 2, int.MaxValue);
            Check.True(float.IsFinite(score) && score >= -60 && score <= 52,
                "extreme counts cannot overflow into an unbounded tactical score");
        }
        Check.Near(0, RecoveryEvaluation.RetentionValue(-1, 5), "invalid lost count");
        Check.Near(0, RecoveryEvaluation.RetentionValue(3, 0), "no cycling option to retain");
        Check.Near(RecoveryEvaluation.RetentionValue(6, 2),
            RecoveryEvaluation.RetentionValue(int.MaxValue, int.MaxValue), "retention arithmetic does not wrap");
    }
}
