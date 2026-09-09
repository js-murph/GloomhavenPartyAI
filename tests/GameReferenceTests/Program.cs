using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using AStar;
using GloomhavenPartyAI;
using ScenarioRuleLibrary;
using ScenarioRuleLibrary.YML;
using SharedLibrary.Client;

internal static class Program
{
    private static int Main()
    {
        string gameRoot = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "GameRoot").Value;
        // Resolve installed managed libraries (including the parser's YAML dependencies) read-only.
        // Do not copy game DLLs or extract rule data into this project or its output.
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            string path = Path.Combine(gameRoot, "GH_Data", "Managed", new AssemblyName(args.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        return ReferenceTests.Run(gameRoot);
    }
}

internal static class ReferenceTests
{
    private static string gameRoot;
    private static AbilityCardYMLData ether;
    private static readonly MethodInfo admission = typeof(TacticalPlanner).GetMethod("IsSupportedAction",
        BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException("IsSupportedAction");
    private static readonly MethodInfo buildOption = typeof(TacticalPlanner).GetMethod("BuildOption",
        BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException("BuildOption");

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run(string root)
    {
        gameRoot = root;
        Action[] tests =
        [
            ParseInstalledEther,
            ParsedEtherPassesBothAdmissionPolicies,
            EngineCopiesPreserveRecoveryAndDark,
            InertStartFlagsAndMiscDefaultsRemainSupported,
            FixedInfusionsAreAllowedButChoiceAndUnknownInfusionsAreNot,
            OnlySinglePermanentLossRecoveryActionsAreAdmitted,
            UnsupportedRecoverySemanticsAreRejected,
            R14BuildOptionUsesRealPilesAndNetRecoveryScore,
            RecoverableCountExcludesPermanentAndNonLostCards,
            RetentionAppliesOnlyToAnUnusedCyclingOption,
            ActualLossRankingRetainsEtherButAllowsLastResort,
            R14ChooseNextActionPrefersRecoveryOverLegalAttackTwo,
            SecondActionRecoveryUsesCurrentActorAndPhaseOnly,
            LastPairChooseNextActionRecoversAfterCompanionDiscard,
            MoveDestinationAdvancesAtLimitedRiskButProtectsLowHealth,
            MoveDestinationRequiresAnAttackAndAnUnblockedRoute
        ];
        int failures = 0;
        int passed = 0;
        foreach (Action test in tests)
        {
            try
            {
                test();
                passed++;
                Console.WriteLine("PASS " + test.Method.Name);
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine("FAIL " + test.Method.Name + ": " + exception);
                if (test == (Action)ParseInstalledEther) break;
            }
        }
        Console.WriteLine($"{passed}/{tests.Length} reference cases passed; {failures} failed; {tests.Length - passed - failures} not run.");
        Console.WriteLine("Isolation: installed YAML parser, engine copies, linked production admission/scoring, " +
            "loss ranking, ChooseNextAction and ChooseMoveDestination on synthetic boards; " +
            "no full parser initialization, ability Start, Unity, game loop, or command submission.");
        return failures == 0 ? 0 : 1;
    }

    private static void ParseInstalledEther()
    {
        SharedClient.InitValidationRecord();
        // CSRLYML's normal constructor creates Unity-dependent lazy loaders. Seed only the character
        // lookup used by AbilityCardYML, not a substitute rule parser or a persisted scenario fixture.
        var rules = (CSRLYML)RuntimeHelpers.GetUninitializedObject(typeof(CSRLYML));
        var global = (CSRLYMLModeData)RuntimeHelpers.GetUninitializedObject(typeof(CSRLYMLModeData));
        typeof(CSRLYMLModeData).GetProperty("Characters").SetValue(global, new CharacterYML());
        typeof(CSRLYML).GetProperty("GlobalData").SetValue(rules, global);
        rules.YMLMode = CSRLYML.EYMLMode.Global;
        rules.Characters.Add(new CharacterYMLData("reference-test") { ID = "SpellweaverID" });
        typeof(ScenarioRuleClient).GetField("s_SRLYML", BindingFlags.NonPublic | BindingFlags.Static)
            .SetValue(null, rules);
        ether = LoadEther();
        Console.WriteLine("SOURCE Global.ruleset:RevivingEther; card=" + ether.ID);
    }

    private static AbilityCardYMLData LoadEther()
    {
        using var archive = ZipFile.OpenRead(Path.Combine(gameRoot, "Rulebase", "Global.ruleset"));
        var entry = archive.Entries.Single(value => value.FullName.Contains("RevivingEther", StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(entry.Open());
        var parser = new AbilityCardYML();
        Require(parser.ProcessFile(reader, entry.FullName), "installed Ether YAML did not parse");
        Require(!SharedClient.ValidationRecord.RecordedFailures.Any(), string.Join("; ",
            SharedClient.ValidationRecord.RecordedFailures.Select(failure => failure.Message)));
        return parser.LoadedYML.Single();
    }

    private static CAbilityCard Card(CAction top = null, int? instance = null) =>
        new CAbilityCard(ether.Initiative, ether.ID, null, null, top ?? ether.TopActionCardData,
            ether.BottomActionCardData, ether, ether.CharacterID, cardInstanceID: instance ?? ether.ID);

    private static bool Admitted(CAction action) => (bool)admission.Invoke(null, [action]);

    private static void Require(bool value, string context)
    {
        if (!value) throw new InvalidOperationException(context);
    }

    private static void BothPolicies(CAction action, bool expected, string context)
    {
        Require(RecoveryPlanner.HasRecoveryAction(Card(action)) == expected, "HasRecoveryAction: " + context);
        Require(Admitted(action) == expected, "actual TacticalPlanner.IsSupportedAction: " + context);
    }

    private static void ParsedEtherPassesBothAdmissionPolicies()
    {
        CAction top = ether.TopActionCardData;
        Require(top.CardPile == CBaseCard.ECardPile.PermanentlyLost, "actual Ether pays permanent loss");
        Require(top.Infusions.SequenceEqual([ElementInfusionBoardManager.EElement.Dark]), "actual Ether infuses fixed Dark");
        Require(RecoveryPlanner.IsSupported((CAbilityRecoverLostCards)top.Abilities.Single()), "actual parsed recovery ability");
        BothPolicies(top, true, "unmodified installed Ether, including its fixed Dark action infusion");
    }

    private static void EngineCopiesPreserveRecoveryAndDark()
    {
        CAction copy = ether.TopActionCardData.Copy();
        Require(!ReferenceEquals(copy, ether.TopActionCardData), "engine produced a distinct action");
        Require(!ReferenceEquals(copy.Abilities.Single(), ether.TopActionCardData.Abilities.Single()), "engine copied the ability");
        Require(copy.Infusions.SequenceEqual(ether.TopActionCardData.Infusions), "copy preserves Dark");
        BothPolicies(copy, true, "engine action copy");
        var full = (CAbilityRecoverLostCards)CAbility.CopyAbility(copy.Abilities.Single(), generateNewID: false, fullCopy: true);
        Require(RecoveryPlanner.IsSupported(full), "engine full ability copy retains supported defaults");
        copy.Abilities[0] = full;
        BothPolicies(copy, true, "full ability copy inside the action");
    }

    private static void InertStartFlagsAndMiscDefaultsRemainSupported()
    {
        CAction copy = ether.TopActionCardData.Copy();
        var recovery = (CAbilityRecoverLostCards)copy.Abilities.Single();
        Require(recovery.MiscAbilityData != null && recovery.StartAbilityRequirements != null,
            "test factory-created defaults, not hand-crafted null requirements/misc");
        var actor = (CPlayerActor)RuntimeHelpers.GetUninitializedObject(typeof(CPlayerActor));
        recovery.TargetingActor = actor;
        recovery.OriginalTargetingActor = actor;
        recovery.FilterActor = actor;
        recovery.ValidActorsInRange = [actor];
        recovery.AppliedEnhancements = true;
        recovery.AbilityStartListenersInvoked = true;
        recovery.OriginalTargetCount = recovery.NumberTargets;
        SetField(typeof(CAbility), recovery, "m_ModifiedStrength", recovery.Strength);
        SetField(typeof(CAbility), recovery, "m_AbilityStartComplete", true);
        SetField(typeof(CAbility), recovery, "m_ActorsToTarget", new List<CActor> { actor });
        SetField(typeof(CAbility), recovery, "m_NumberTargetsRemaining", 1);
        typeof(CAbility).GetProperty("ActorsTargeted").SetValue(recovery, new List<CActor>());
        SetField(typeof(CAbilityTargeting), recovery, "m_State", CAbilityTargeting.TargetingState.ActorIsSelectingTargetingFocus);
        BothPolicies(copy, true, "inert fields normally populated by Start; Start itself is NOT invoked");
        Require(recovery.ActorsToTarget.Single() == actor && recovery.AppliedEnhancements &&
            recovery.AbilityStartListenersInvoked && !recovery.AbilityHasHappened &&
            copy.Infusions.SequenceEqual([ElementInfusionBoardManager.EElement.Dark]),
            "policy is read-only: self preselection, lifecycle flags and Dark remain intact");
    }

    private static void FixedInfusionsAreAllowedButChoiceAndUnknownInfusionsAreNot()
    {
        foreach (var element in Enum.GetValues<ElementInfusionBoardManager.EElement>())
        {
            CAction copy = ether.TopActionCardData.Copy();
            copy.Infusions.Add(element);
            BothPolicies(copy, element != ElementInfusionBoardManager.EElement.Any, "additional action infusion " + element);
        }
        CAction unknown = ether.TopActionCardData.Copy();
        unknown.Infusions.Add((ElementInfusionBoardManager.EElement)999);
        BothPolicies(unknown, false, "unknown action infusion");
        CAction deferred = ether.TopActionCardData.Copy();
        SetField(typeof(CAbility), deferred.Abilities.Single(), "m_InfuseElements",
            new List<ElementInfusionBoardManager.EElement> { ElementInfusionBoardManager.EElement.Dark });
        BothPolicies(deferred, false, "ability-level deferred infusion is not the supported fixed action infusion");
    }

    private static void OnlySinglePermanentLossRecoveryActionsAreAdmitted()
    {
        CAction mixed = ether.TopActionCardData.Copy();
        mixed.Abilities.Add(CAbilityMove.CreateDefaultMove(2, false));
        BothPolicies(mixed, false, "mixed recovery and move");
        CAction duplicate = ether.TopActionCardData.Copy();
        duplicate.Abilities.Add(ether.TopActionCardData.Copy().Abilities.Single());
        BothPolicies(duplicate, false, "two recoveries cannot receive double credit");
        foreach (var pile in new[] { CBaseCard.ECardPile.Discarded, CBaseCard.ECardPile.Lost })
            BothPolicies(new CAction(null, null, ether.TopActionCardData.Copy().Abilities.Single(), 0, pile),
                false, "recovery without permanent loss: " + pile);
        BothPolicies(new CAction(null, null, CAbilityMove.CreateDefaultMove(2, false), 0,
            CBaseCard.ECardPile.PermanentlyLost), false, "unrelated permanently-lost move");
        Require(!Admitted(null), "missing action is rejected");
    }

    private static void UnsupportedRecoverySemanticsAreRejected()
    {
        Action<CAbilityRecoverLostCards>[] mutations =
        [
            value => value.Strength = 1,
            value => value.RecoverCardsWithAbilityOfTypeFilter.Add(CAbility.EAbilityType.Attack),
            value => value.RecoverCardsWithAbilityOfTypeFilter = null,
            value => value.AbilityFilter.AbilityFilters[0].FilterTargetType |= CAbilityFilter.EFilterTargetType.Ally,
            value => value.NumberTargets = 2,
            value => value.Range = int.MaxValue,
            value => value.IsItemAbility = true,
            value => value.MiscAbilityData.TargetOneEnemyWithAllAttacks = true
        ];
        foreach (var mutate in mutations)
        {
            // Engine copies share some nested metadata. Reparse before destructive mutations so
            // a changed filter or Misc field cannot contaminate the next case or the source card.
            CAction copy = LoadEther().TopActionCardData.Copy();
            var recovery = (CAbilityRecoverLostCards)copy.Abilities.Single();
            mutate(recovery);
            Require(!RecoveryPlanner.IsSupported(recovery), "unsupported semantics: " + mutate.Method.Name);
            BothPolicies(copy, false, "unsupported semantics: " + mutate.Method.Name);
        }
        BothPolicies(ether.TopActionCardData, true, "mutation fixtures have not changed the parsed source action");
    }

    private static void SetField(Type type, object target, string name, object value) =>
        (type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new MissingFieldException(type.FullName, name)).SetValue(target, value);

    private static CPlayerActor Snapshot(int lost, int hand, int round, int discarded)
    {
        // Real engine objects with only inert pile fields populated. No class/actor initialization,
        // rule execution, installed save loading, or callbacks that could mutate a live scenario.
        var actor = (CPlayerActor)RuntimeHelpers.GetUninitializedObject(typeof(CPlayerActor));
        var cards = (CCharacterClass)RuntimeHelpers.GetUninitializedObject(typeof(CCharacterClass));
        SetField(typeof(CActor), actor, "m_Class", cards);
        foreach (var (field, count) in new[] { ("m_LostAbilityCards", lost), ("m_HandAbilityCards", hand),
            ("m_RoundAbilityCards", round), ("m_DiscardedAbilityCards", discarded),
            ("m_PermanentlyLostAbilityCards", 0), ("m_ExtraTurnCards", 0) })
            SetField(typeof(CCharacterClass), cards, field,
                Enumerable.Range(0, count).Select(index => Card(instance: ether.ID + index + 1)).ToList());
        return actor;
    }

    private static void R14BuildOptionUsesRealPilesAndNetRecoveryScore()
    {
        CPlayerActor actor = Snapshot(3, 3, 2, 0);
        CAbilityCard card = Card();
        actor.CharacterClass.RoundAbilityCards[0] = card;
        var option = (TacticalPlanner.PlannedAction)buildOption.Invoke(null, [actor, card, CBaseCard.ActionType.TopAction]);
        Require(option != null && option.Card == card && option.Action == card.TopAction,
            "actual BuildOption admits R14 Ether, not a default Attack 2");
        float expected = RecoveryEvaluation.RecoveryScore(3, 3, 2, 0) +
            card.TopAction.ActionXP * 0.25f + card.TopAction.Infusions.Count * 0.35f;
        Require(Math.Abs(option.Score - expected) < 0.0001f,
            "actual ScoreAction includes recovery once, with no second permanent-loss penalty");
        Require(option.Score > TacticalEvaluation.AttackValue(2, 6, false) * 1.1f,
            "actual admitted R14 option beats a nonlethal Attack 2 with attack-first credit");
        Require(Math.Abs(RecoveryPlanner.Score(Snapshot(3, 5, 0, 0),
            (CAbilityRecoverLostCards)card.TopAction.Abilities.Single()) - RecoveryPlanner.Score(actor,
            (CAbilityRecoverLostCards)card.TopAction.Abilities.Single())) < 0.0001f,
            "actual planner piles agree before and after selection");
        actor.CharacterClass.LostAbilityCards.Clear();
        Require(buildOption.Invoke(null, [actor, card, CBaseCard.ActionType.TopAction]) == null,
            "actual BuildOption excludes recovery when no cards can be returned");
    }

    private static void RecoverableCountExcludesPermanentAndNonLostCards()
    {
        CPlayerActor actor = Snapshot(3, 3, 2, 4);
        var ability = (CAbilityRecoverLostCards)ether.TopActionCardData.Abilities.Single();
        Require(RecoveryPlanner.RecoverableCount(actor, ability) == 3, "only the three ordinary Lost cards count");
        actor.CharacterClass.PermanentlyLostAbilityCards.Add(actor.CharacterClass.LostAbilityCards[0]);
        actor.CharacterClass.LostAbilityCards.Add(null);
        Require(RecoveryPlanner.RecoverableCount(actor, ability) == 2, "permanent-loss overlap and null entries do not count");
        actor.CharacterClass.DiscardedAbilityCards.Add(Card());
        Require(RecoveryPlanner.RecoverableCount(actor, ability) == 2, "adding a discard cannot inflate recovery");
    }

    private static void RetentionAppliesOnlyToAnUnusedCyclingOption()
    {
        CPlayerActor actor = Snapshot(3, 3, 2, 0);
        CAbilityCard card = Card(ether.TopActionCardData.Copy());
        Require(RecoveryPlanner.RetentionValue(actor, card) == 0, "card outside the cycling piles has no option");
        actor.CharacterClass.RoundAbilityCards[0] = card;
        float retained = RecoveryPlanner.RetentionValue(actor, card);
        Require(retained == RecoveryEvaluation.RetentionValue(3, 5) && float.IsFinite(retained),
            "actual retention uses Lost and all cycling piles, with a finite premium");
        card.SetSelectedAction(card.TopAction);
        Require(RecoveryPlanner.RetentionValue(actor, card) == retained, "selection alone does not spend the option");
        card.TopAction.Abilities.Single().AbilityHasHappened = true;
        Require(RecoveryPlanner.RetentionValue(actor, card) == 0, "completed permanent loss still in Round is not retained");
        card.TopAction.Abilities.Single().AbilityHasHappened = false;
        actor.CharacterClass.PermanentlyLostAbilityCards.Add(card);
        Require(RecoveryPlanner.RetentionValue(actor, card) == 0, "permanently lost Ether cannot recover itself");
        actor.CharacterClass.PermanentlyLostAbilityCards.Clear();
        actor.CharacterClass.LostAbilityCards.Add(card);
        Require(RecoveryPlanner.RetentionValue(actor, card) == 0, "ordinary-lost Ether is not an unused cycling option");
    }

    private static void WithBoard(Action<CPlayerActor, CEnemyActor, CAbilityCard, CAbilityCard> test,
        bool rangedEnemy = false, bool blockMovement = true)
    {
        // This is a managed snapshot, not a replay of R14's room. The synthetic partner supplies
        // ordinary Attack 2 / Move 2; Ether's printed halves are parsed from the installed rules.
        // Recovery cases block movement; approach cases open the board and use a stationary ranged enemy.
        CPlayerActor actor = Snapshot(3, 3, 2, 0);
        actor.Type = actor.OriginalType = CActor.EType.Player;
        actor.CauseOfDeath = CActor.ECauseOfDeath.StillAlive;
        actor.Health = actor.MaxHealth = 10;
        actor.TakingExtraTurnOfTypeStack = new();
        typeof(CActor).GetProperty("ActorGuid").SetValue(actor, "r14-reference-player");
        SetField(typeof(CActor), actor, "m_ArrayIndex", new Point(2, 2));
        SetField(typeof(CActor), actor, "m_Tokens", new CTokens(actor));
        SetField(typeof(CCharacterClass), actor.CharacterClass, "m_ActivatedCards", new List<CBaseCard>());
        SetField(typeof(CCharacterClass), actor.CharacterClass, "m_CharacterYML",
            new CharacterYMLData("reference-player") { ID = ether.CharacterID });

        var monster = (CMonsterClass)RuntimeHelpers.GetUninitializedObject(typeof(CMonsterClass));
        typeof(CMonsterClass).GetProperty("ActivatedCards").SetValue(monster, new List<CBaseCard>());
        SetField(typeof(CMonsterClass), monster, "m_CurrentMonsterStat",
            new BaseStats(1, 6, 0, rangedEnemy ? 2 : 0, rangedEnemy ? 3 : 1, 1, 0, 0, 0, 0, false, false, false, false, false, false,
                [], [], new(), [], []));
        var enemy = new CEnemyActor { Type = CActor.EType.Enemy, OriginalType = CActor.EType.Enemy,
            Health = 6, MaxHealth = 6, CauseOfDeath = CActor.ECauseOfDeath.StillAlive };
        typeof(CActor).GetProperty("ActorGuid").SetValue(enemy, "r14-reference-target");
        SetField(typeof(CActor), enemy, "m_Class", monster);
        SetField(typeof(CActor), enemy, "m_ArrayIndex", new Point(rangedEnemy ? 7 : 3, 2));
        SetField(typeof(CActor), enemy, "m_Tokens", new CTokens(enemy));

        var scenario = new CScenario("reference", "reference", 1, default, null);
        scenario.PlayerActors.Add(actor);
        scenario.Enemies.Add(enemy);
        var state = new ScenarioState();
        typeof(ScenarioState).GetProperty("ScenarioModifiers").SetValue(state, new List<CScenarioModifier>());
        typeof(ScenarioState).GetProperty("Props").SetValue(state, new List<CObjectProp>());
        foreach (string name in new[] { "Players", "Monsters", "HeroSummons", "AllyMonsters", "NeutralMonsters", "Enemy2Monsters", "Objects" })
        {
            PropertyInfo property = typeof(ScenarioState).GetProperty(name);
            property.SetValue(state, Activator.CreateInstance(property.PropertyType));
        }
        var map = new CMap { Revealed = true };
        var tiles = new CTile[10, 5];
        var paths = new CPathFinder(10, 5, true);
        for (int x = 0; x < 10; x++)
        for (int y = 0; y < 5; y++)
        {
            tiles[x, y] = new CTile { m_ArrayIndex = new Point(x, y), m_HexMap = map };
            paths.Nodes[x, y].Walkable = true;
            paths.Nodes[x, y].Blocked = blockMovement && new Point(x, y) != actor.ArrayIndex && new Point(x, y) != enemy.ArrayIndex;
        }
        var savedFields = new Dictionary<FieldInfo, object>();
        ScenarioState savedState = ScenarioManager.CurrentScenarioState;
        var savedStack = GameState.ExtraTurnActionSelectionFlagStack;
        var savedScalar = CActor.s_LOSTileScalar;
        try
        {
            foreach (var (type, name, value) in new (Type, string, object)[]
                { (typeof(ScenarioManager), "s_Scenario", scenario), (typeof(ScenarioManager), "s_TileArray", tiles),
                  (typeof(ScenarioManager), "s_PathFinder", paths), (typeof(ScenarioManager), "s_Width", 10),
                  (typeof(ScenarioManager), "s_Height", 5), (typeof(GameState), "s_CurrentActor", actor),
                  (typeof(GameState), "s_CurrentActionSelectionFlag", GameState.EActionSelectionFlag.None),
                  (typeof(GameState), "<RoundAbilityCardselected>k__BackingField", null),
                  (typeof(PhaseManager), "s_CurrentPhase", null) })
            {
                FieldInfo field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
                savedFields.Add(field, field.GetValue(null));
                field.SetValue(null, value);
            }
            ScenarioManager.CurrentScenarioState = state;
            GameState.ExtraTurnActionSelectionFlagStack = new();
            CActor.s_LOSTileScalar = new Vector(1f, 0.75f);
            CAction attack = new(null, null, CAbilityAttack.CreateDefaultAttack(2, 1, 1, false), 0, CBaseCard.ECardPile.Discarded);
            CAction move = new(null, null, CAbilityMove.CreateDefaultMove(2, false), 0, CBaseCard.ECardPile.Discarded);
            var card = new CAbilityCard(ether.Initiative, ether.ID, move.Copy(), attack.Copy(),
                ether.TopActionCardData.Copy(), ether.BottomActionCardData.Copy(), ether, ether.CharacterID, ether.ID);
            var partner = new CAbilityCard(50, -1, move.Copy(), attack.Copy(), attack.Copy(), move.Copy(),
                ether, ether.CharacterID, -1);
            actor.CharacterClass.RoundAbilityCards[0] = card;
            actor.CharacterClass.RoundAbilityCards[1] = partner;
            test(actor, enemy, card, partner);
        }
        finally
        {
            TacticalPlanner.Reset();
            foreach (var (field, value) in savedFields) field.SetValue(null, value);
            ScenarioManager.CurrentScenarioState = savedState;
            GameState.ExtraTurnActionSelectionFlagStack = savedStack;
            CActor.s_LOSTileScalar = savedScalar;
        }
    }

    private static void R14ChooseNextActionPrefersRecoveryOverLegalAttackTwo()
    {
        WithBoard((actor, enemy, card, partner) =>
        {
            var attackOption = (TacticalPlanner.PlannedAction)buildOption.Invoke(null,
                [actor, card, CBaseCard.ActionType.DefaultAttackAction]);
            Require(attackOption != null && Math.Abs(attackOption.Score - TacticalEvaluation.AttackValue(2, 6, false)) < 0.0001f,
                "real board/filter/LOS/shield path gives a legal nonlethal Attack 2, not a blank attack; score=" +
                attackOption?.Score + "; LOS=" + CActor.HaveLOS(ScenarioManager.Tiles[2, 2], ScenarioManager.Tiles[3, 2]));
            TacticalPlanner.Reset();
            var selected = TacticalPlanner.ChooseNextAction(actor);
            Require(selected?.Card == card && selected.ActionType == CBaseCard.ActionType.TopAction &&
                selected.Action.Abilities.Single() is CAbilityRecoverLostCards,
                "actual R14 pair selection chooses Ether recovery before its companion");
            Require(actor.CharacterClass.LostAbilityCards.Count == 3,
                "selecting the recovery plan does not execute recovery");
            actor.CharacterClass.LostAbilityCards.Clear();
            TacticalPlanner.Reset();
            Require(TacticalPlanner.ChooseNextAction(actor)?.FirstAttack != null,
                "same pair and board with empty Lost selects an attack instead");
            Require(enemy.Health == 6 && actor.CharacterClass.HandAbilityCards.Count == 3 &&
                actor.CharacterClass.RoundAbilityCards.Count == 2 && actor.CharacterClass.DiscardedAbilityCards.Count == 0 &&
                actor.ArrayIndex == new Point(2, 2) && enemy.ArrayIndex == new Point(3, 2),
                "planning does not execute damage, movement, or card recovery");
        });
    }

    private static void ActualLossRankingRetainsEtherButAllowsLastResort()
    {
        CPlayerActor actor = Snapshot(3, 3, 2, 0);
        CAbilityCard card = Card(ether.TopActionCardData.Copy());
        CAbilityCard ordinary = Card(new CAction(null, null, CAbilityMove.CreateDefaultMove(2, false),
            0, CBaseCard.ECardPile.Discarded), instance: -1);
        actor.CharacterClass.HandAbilityCards[0] = card;
        actor.CharacterClass.HandAbilityCards[1] = ordinary;
        Require(TacticalPlanner.ChooseCardToLose(actor, [card, ordinary]) == ordinary,
            "actual loss ranking preserves the unused recovery option when another card is eligible");
        Require(TacticalPlanner.ChooseCardToLose(actor, [card]) == card,
            "actual loss ranking permits Ether when it is the last eligible card");
        Require(TacticalPlanner.ChooseCardsToLose(actor, [card, ordinary], 2).SequenceEqual([ordinary, card]),
            "a mandatory two-card loss can include Ether after exhausting cheaper alternatives");
        Require(actor.CharacterClass.HandAbilityCards.Contains(card) && RecoveryPlanner.RetentionValue(actor, card) > 0,
            "ranking losses does not actually spend the recovery option");
    }

    private static void SetSecondAction(CPlayerActor actor, CAbilityCard companion)
    {
        // The real phase constructor dispatches game messages. Populate only its inert type field.
        var phase = (CPhaseActionSelection)RuntimeHelpers.GetUninitializedObject(typeof(CPhaseActionSelection));
        SetField(typeof(CPhase), phase, "m_PhaseType", CPhase.PhaseType.ActionSelection);
        typeof(PhaseManager).GetField("s_CurrentPhase", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, phase);
        typeof(GameState).GetField("s_CurrentActionSelectionFlag", BindingFlags.NonPublic | BindingFlags.Static)
            .SetValue(null, GameState.EActionSelectionFlag.BottomActionPlayed);
        typeof(GameState).GetProperty("RoundAbilityCardselected").SetValue(null, companion);
        companion.SetSelectedAction(companion.BottomAction);
        actor.CharacterClass.RoundAbilityCards.Remove(companion);
        actor.CharacterClass.DiscardedAbilityCards.Add(companion);
        Require(GameState.InternalCurrentActor == actor && PhaseManager.PhaseType == CPhase.PhaseType.ActionSelection &&
            GameState.CurrentActionSelectionSequence == GameState.ActionSelectionSequenceType.SecondAction && GameState.HasPlayedBottomAction,
            "fixture represents second-action top selection after the bottom card left Round");
    }

    private static void SecondActionRecoveryUsesCurrentActorAndPhaseOnly()
    {
        WithBoard((actor, enemy, card, companion) =>
        {
            var recovery = (CAbilityRecoverLostCards)card.TopAction.Abilities.Single();
            float first = RecoveryPlanner.Score(actor, recovery);
            SetSecondAction(actor, companion);
            float second = RecoveryPlanner.Score(actor, recovery);
            Require(Math.Abs(first - second) < 0.0001f && Math.Abs(second - 18.666666f) < 0.0001f,
                $"R14 actual pile scoring remains equivalent after discard: first={first}, second={second}");
            var selected = TacticalPlanner.ChooseNextAction(actor);
            Require(selected?.Card == card && selected.ActionType == CBaseCard.ActionType.TopAction,
                "actual second-action selection still chooses printed Ether over default Attack 2");

            float notSecond = RecoveryEvaluation.RecoveryScore(3, 3, 1, 1, false);
            FieldInfo current = typeof(GameState).GetField("s_CurrentActor", BindingFlags.NonPublic | BindingFlags.Static);
            current.SetValue(null, enemy);
            Require(RecoveryPlanner.Score(actor, recovery) == notSecond, "another actor's sequence cannot normalize this actor's piles");
            current.SetValue(null, actor);
            CPhase phase = PhaseManager.Phase;
            SetField(typeof(CPhase), phase, "m_PhaseType", CPhase.PhaseType.SelectAbilityCardsOrLongRest);
            Require(RecoveryPlanner.Score(actor, recovery) == notSecond, "stale second-action flags outside ActionSelection are ignored");
            SetField(typeof(CPhase), phase, "m_PhaseType", CPhase.PhaseType.ActionSelection);
            actor.TakingExtraTurnOfTypeStack.Push(CAbilityExtraTurn.EExtraTurnType.BothActionsLater);
            Require(RecoveryPlanner.Score(actor, recovery) == notSecond, "extra turns cannot use the ordinary Round normalization");
            actor.TakingExtraTurnOfTypeStack.Pop();
            FieldInfo sequence = typeof(GameState).GetField("s_CurrentActionSelectionFlag", BindingFlags.NonPublic | BindingFlags.Static);
            sequence.SetValue(null, GameState.EActionSelectionFlag.None);
            Require(RecoveryPlanner.Score(actor, recovery) == notSecond,
                "ActionSelection for the current actor is not sufficient before the first action is played");
            sequence.SetValue(null, GameState.EActionSelectionFlag.BottomActionPlayed);
            Require(RecoveryPlanner.Score(actor, recovery) == second && actor.CharacterClass.RoundAbilityCards.Count == 1 &&
                actor.CharacterClass.DiscardedAbilityCards.Single() == companion, "guards leave the real piles intact");
            Console.WriteLine($"EVIDENCE R14 second-action recovery={second:F6}; choice={selected.ActionType}");
        });
    }

    private static void LastPairChooseNextActionRecoversAfterCompanionDiscard()
    {
        WithBoard((actor, enemy, card, companion) =>
        {
            actor.CharacterClass.HandAbilityCards.Clear();
            actor.CharacterClass.LostAbilityCards.RemoveAt(2);
            SetSecondAction(actor, companion);
            var attack = (TacticalPlanner.PlannedAction)buildOption.Invoke(null,
                [actor, card, CBaseCard.ActionType.DefaultAttackAction]);
            float score = RecoveryPlanner.Score(actor, (CAbilityRecoverLostCards)card.TopAction.Abilities.Single());
            Require(attack != null && attack.Score == TacticalEvaluation.AttackValue(2, enemy.Health, false) &&
                score > attack.Score * 1.1f, "last pair has a real nonlethal attack alternative, but recovery rescues another turn");
            var selected = TacticalPlanner.ChooseNextAction(actor);
            Require(selected?.Card == card && selected.ActionType == CBaseCard.ActionType.TopAction &&
                selected.Action.Abilities.Single() is CAbilityRecoverLostCards,
                "h0 r1 d1 lost2 must select Ether as the second action rather than exhaust on Attack 2");
            Require(actor.CharacterClass.HandAbilityCards.Count == 0 && actor.CharacterClass.LostAbilityCards.Count == 2 &&
                actor.CharacterClass.RoundAbilityCards.Single() == card &&
                actor.CharacterClass.DiscardedAbilityCards.Single() == companion && enemy.Health == 6,
                "selection does not actually recover cards or execute the alternative attack");
            Console.WriteLine($"EVIDENCE last-pair second-action recovery={score:F3}; Attack2={attack.Score:F3}; choice={selected.ActionType}");
        });
    }

    private static CAbilityMove PrepareFutureMelee(CPlayerActor actor, CAbilityCard ordinary)
    {
        TacticalPlanner.Reset();
        actor.CharacterClass.RoundAbilityCards.Clear();
        actor.CharacterClass.LostAbilityCards.Clear();
        actor.CharacterClass.HandAbilityCards.Clear();
        for (int index = 0; index < 6; index++)
            actor.CharacterClass.HandAbilityCards.Add(new CAbilityCard(50, -100 - index,
                ordinary.DefaultMoveAction.Copy(), ordinary.DefaultAttackAction.Copy(),
                ordinary.TopAction.Copy(), ordinary.BottomAction.Copy(), ether, ether.CharacterID, -100 - index));
        var move = (CAbilityMove)CAbilityMove.CreateDefaultMove(2, false);
        SetField(typeof(CAbilityMove), move, "m_MoveCount", 2);
        Require(TacticalPlanner.GetFollowupAttacks(actor).Count == 0 && move.RemainingMoves == 2 &&
            ScenarioManager.CurrentScenarioState.DoorProps.Count == 0,
            "no committed plan or door: movement must use actual future melee cards, not an exploration bonus");
        return move;
    }

    private static int DistanceToEnemy(CTile tile, CEnemyActor enemy) =>
        ScenarioManager.GetTileDistance(tile.m_ArrayIndex.X, tile.m_ArrayIndex.Y, enemy.ArrayIndex.X, enemy.ArrayIndex.Y);

    private static void MoveDestinationAdvancesAtLimitedRiskButProtectsLowHealth()
    {
        WithBoard((actor, enemy, card, ordinary) =>
        {
            CAbilityMove move = PrepareFutureMelee(actor, ordinary);
            CTile start = ScenarioManager.Tiles[actor.ArrayIndex.X, actor.ArrayIndex.Y];
            Require(DistanceToEnemy(start, enemy) == 5 && enemy.MonsterClass.Move == 0 &&
                enemy.MonsterClass.Attack == 2 && enemy.MonsterClass.Range == 3,
                "Move 2 melee starts five hexes from a stationary ranged Attack 2 / Range 3 enemy");
            CTile healthy = TacticalPlanner.ChooseMoveDestination(actor, move);
            Require(healthy != null && healthy != start && DistanceToEnemy(healthy, enemy) == 3,
                "healthy destination selection must advance two hexes, not stall outside the ranged enemy");
            Require(TacticalPlanner.ThreatAt(actor, healthy.m_ArrayIndex) > TacticalPlanner.ThreatAt(actor, start.m_ArrayIndex),
                "the demonstrated progress tolerates limited extra exposure, not merely an equally safe move");
            actor.Health = 2;
            CTile cautious = TacticalPlanner.ChooseMoveDestination(actor, move);
            Require(cautious == start, "at low HP, the same safe starting position must not charge into ranged reach");

            SetField(typeof(CActor), actor, "m_ArrayIndex", healthy.m_ArrayIndex);
            CTile retreat = TacticalPlanner.ChooseMoveDestination(actor, move);
            Require(retreat != null && retreat != healthy && DistanceToEnemy(retreat, enemy) > 3 &&
                TacticalPlanner.ThreatAt(actor, retreat.m_ArrayIndex) < TacticalPlanner.ThreatAt(actor, healthy.m_ArrayIndex),
                "if already exposed at low HP, actual destination selection retreats out of ranged reach");
            Require(actor.ArrayIndex == healthy.m_ArrayIndex && actor.Health == 2 && enemy.Health == 6 &&
                actor.CharacterClass.HandAbilityCards.Count == 6 && move.RemainingMoves == 2,
                "destination selection does not execute movement, spend cards, or change HP");
            Console.WriteLine($"EVIDENCE destination distances: healthy 5->{DistanceToEnemy(healthy, enemy)}, " +
                $"lowHP-safe 5->{DistanceToEnemy(cautious, enemy)}, lowHP-exposed 3->{DistanceToEnemy(retreat, enemy)}");
        }, rangedEnemy: true, blockMovement: false);
    }

    private static void MoveDestinationRequiresAnAttackAndAnUnblockedRoute()
    {
        WithBoard((actor, enemy, card, ordinary) =>
        {
            CAbilityMove move = PrepareFutureMelee(actor, ordinary);
            CTile start = ScenarioManager.Tiles[actor.ArrayIndex.X, actor.ArrayIndex.Y];
            // A full-height barrier breaks every route to a firing hex without hiding the target.
            for (int y = 0; y < ScenarioManager.Height; y++) ScenarioManager.PathFinder.Nodes[5, y].Walkable = false;
            Require(TacticalPlanner.ChooseMoveDestination(actor, move) == start,
                "a visible hostile across an impassable barrier cannot earn unverified path progress");
            for (int y = 0; y < ScenarioManager.Height; y++) ScenarioManager.PathFinder.Nodes[5, y].Walkable = true;
            Require(TacticalPlanner.ChooseMoveDestination(actor, move) != start,
                "opening the route restores progress against the same hostile with no door");
            for (int index = 0; index < actor.CharacterClass.HandAbilityCards.Count; index++)
                actor.CharacterClass.HandAbilityCards[index] = Card(ordinary.BottomAction.Copy(), instance: -200 - index);
            Require(TacticalPlanner.ChooseMoveDestination(actor, move) == start,
                "without a supported future attack, movement cannot invent a melee goal");
        }, rangedEnemy: true, blockMovement: false);
    }
}
