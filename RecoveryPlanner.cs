using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using ScenarioRuleLibrary;
using ScenarioRuleLibrary.YML;

namespace GloomhavenPartyAI
{
    // Read-only policy. The caller owns whole-action admission, prompt validation and StepComplete.
    internal static class RecoveryPlanner
    {
        private static readonly FieldInfo DeferredInfusions = typeof(CAbility).GetField(
            "m_InfuseElements", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static bool IsSupported(CAbilityRecoverLostCards ability)
        {
            if (ability == null || ability.GetType() != typeof(CAbilityRecoverLostCards) ||
                ability.AbilityType != CAbility.EAbilityType.RecoverLostCards ||
                ability.Strength != int.MaxValue || ability.Range <= 0 || ability.Range == int.MaxValue ||
                ability.Targeting != CAbility.EAbilityTargeting.Range || ability.NumberTargets != 1 ||
                ability.TileFilter != CAbilityFilter.EFilterTile.None || ability.AreaEffect != null ||
                ability.AllTargets || ability.OneTargetAtATime || ability.IsSubAbility ||
                ability.IsInlineSubAbility || ability.UseSubAbilityTargeting || ability.IsMergedAbility ||
                ability.IsModifierAbility || ability.IsScenarioModifierAbility || ability.IsMonsterAbility ||
                ability.IsItemAbility || ability.ParentAbility != null || ability.IsControlAbility ||
                ability.AllTargetsOnMovePath || ability.AllTargetsOnMovePathSameStartAndEnd ||
                ability.AllTargetsOnAttackPath || ability.IsConsumeAbility || ability.AbilityTextOnly ||
                ability.OnDeath || ability.ProcessIfDead || ability.UseSpecialBaseStat ||
                ability.AddAttackBaseStat || ability.StrengthIsBase || ability.RangeIsBase || ability.TargetIsBase ||
                ability.StackedAttackEffectAbility || ability.TargetThisActorAutomatically != null ||
                ability.OriginatesFromAnAura || ability.ActiveBonusAddTargetBuff != 0 ||
                !IsEmpty(ability.AbilityEnhancements) || !IsEmpty(ability.SubAbilities) ||
                !IsEmpty(ability.ConditionalOverrides) || !IsEmpty(ability.ActiveConditionalOverrides) ||
                !IsEmpty(ability.CurrentOverrides) || !IsEmpty(ability.StatIsBasedOnXEntries) ||
                !IsEmpty(ability.PositiveConditions) || !IsEmpty(ability.NegativeConditions) ||
                !IsEmpty(ability.ResourcesToAddOnAbilityEnd) || !IsEmpty(ability.ResourcesToTakeFromTargets) ||
                !IsEmpty(ability.ResourcesToGiveToTargets) || !IsEmpty(ability.ActorsToIgnore) ||
                ability.Augment != null || ability.Song != null ||
                !IsPlainRequirements(ability.StartAbilityRequirements) || !IsPlainMisc(ability.MiscAbilityData) ||
                DeferredInfusions == null || !IsEmpty(DeferredInfusions.GetValue(ability) as ICollection))
            {
                return false;
            }

            AbilityData.ActiveBonusData bonus = ability.ActiveBonusData;
            if (bonus != null && (bonus.Duration != CActiveBonus.EActiveBonusDurationType.NA ||
                bonus.Behaviour != CActiveBonus.EActiveBonusBehaviourType.None || bonus.Requirements != null ||
                bonus.IsAura || bonus.IsToggleBonus || bonus.OverrideAsSong || bonus.IsSingleTargetBonus ||
                bonus.GiveAbilityCardToActor || bonus.ConsumeResources || bonus.AbilityData != null ||
                bonus.CostAbility != null || !IsEmpty(bonus.Consuming) || !IsEmpty(bonus.RequiredResources) ||
                !IsEmpty(bonus.ActiveBonusAbilityOverrides)))
            {
                return false;
            }

            // ApplyToActor dereferences this list despite HasCardsToRecover accepting null.
            return ability.RecoverCardsWithAbilityOfTypeFilter != null &&
                ability.RecoverCardsWithAbilityOfTypeFilter.Count == 0 &&
                IsPlainFilter(ability.AbilityFilter, CAbilityFilter.EFilterTargetType.Self);
        }

        internal static int RecoverableCount(CPlayerActor actor, CAbilityRecoverLostCards ability)
        {
            if (actor?.CharacterClass == null || !IsSupported(ability))
            {
                return 0;
            }
            // Do not count discards, active losses, projected companion losses or permanent losses.
            var cards = actor.CharacterClass;
            return cards.LostAbilityCards?.Count(card => card != null &&
                cards.PermanentlyLostAbilityCards?.Contains(card) != true) ?? 0;
        }

        // Net score for a single permanently-lost recovery action; do not apply LossPenalty again.
        internal static float Score(CPlayerActor actor, CAbilityRecoverLostCards ability)
        {
            if (actor?.CharacterClass == null || !IsSupported(ability))
            {
                return 0f;
            }
            var cards = actor.CharacterClass;
            // Sequence flags can outlive the turn. Extra turns use ExtraTurnCards, not Round.
            bool companionAlreadyPlayed = GameState.InternalCurrentActor == actor &&
                PhaseManager.PhaseType == CPhase.PhaseType.ActionSelection && !actor.IsTakingExtraTurn &&
                GameState.CurrentActionSelectionSequence == GameState.ActionSelectionSequenceType.SecondAction;
            return RecoveryEvaluation.RecoveryScore(RecoverableCount(actor, ability),
                cards.HandAbilityCards?.Count ?? 0, cards.RoundAbilityCards?.Count ?? 0,
                cards.DiscardedAbilityCards?.Count ?? 0, companionAlreadyPlayed);
        }

        // Add to ordinary future-card value when choosing rest/damage-prevention losses.
        internal static float RetentionValue(CPlayerActor actor, CAbilityCard card)
        {
            if (actor?.CharacterClass == null || !HasRecoveryAction(card))
            {
                return 0f;
            }
            var cards = actor.CharacterClass;
            if (cards.LostAbilityCards?.Contains(card) == true ||
                cards.PermanentlyLostAbilityCards?.Contains(card) == true ||
                !(cards.HandAbilityCards?.Contains(card) == true ||
                  cards.DiscardedAbilityCards?.Contains(card) == true ||
                  cards.RoundAbilityCards?.Contains(card) == true))
            {
                return 0f;
            }
            // A performed loss ability can still be in Round until its action finishes.
            if (cards.RoundAbilityCards?.Contains(card) == true && card.SelectedAction != null &&
                (card.SelectedAction.CardPile == CBaseCard.ECardPile.Lost ||
                 card.SelectedAction.CardPile == CBaseCard.ECardPile.PermanentlyLost) &&
                card.SelectedAction.Abilities.Any(ability => ability.AbilityHasHappened))
            {
                return 0f;
            }
            int lost = cards.LostAbilityCards?.Count(other => other != null &&
                cards.PermanentlyLostAbilityCards?.Contains(other) != true) ?? 0;
            int cycling = (cards.HandAbilityCards?.Count ?? 0) + (cards.RoundAbilityCards?.Count ?? 0) +
                (cards.DiscardedAbilityCards?.Count ?? 0);
            return RecoveryEvaluation.RetentionValue(lost, cycling);
        }

        internal static bool HasRecoveryAction(CAbilityCard card)
        {
            // Fixed action infusions (including Ether's Dark) are queued by the engine.
            // Any would require an element choice that this recovery policy does not resolve.
            return card != null && new[] { card.TopAction, card.BottomAction }.Any(action =>
                action?.CardPile == CBaseCard.ECardPile.PermanentlyLost &&
                IsEmpty(action.Augmentations) &&
                (action.Infusions == null || action.Infusions.All(element =>
                    element != ElementInfusionBoardManager.EElement.Any &&
                    Enum.IsDefined(typeof(ElementInfusionBoardManager.EElement), element))) &&
                action.Abilities?.Count == 1 &&
                IsSupported(action.Abilities[0] as CAbilityRecoverLostCards));
        }

        private static bool IsPlainFilter(CAbilityFilterContainer container,
            CAbilityFilter.EFilterTargetType targetType)
        {
            if (container?.AbilityFilters?.Count != 1 || container.AbilityFilters[0] == null ||
                container.HasNonTargetTypeFilters())
            {
                return false;
            }
            CAbilityFilter filter = container.AbilityFilters[0];
            // HasNonTargetTypeFilters omits these additional restrictions.
            return filter.FilterTargetType == targetType && !filter.Invert && !filter.UseTargetOriginalType &&
                IsEmpty(filter.FilterTargetHasCharacterResource) && IsEmpty(filter.SpecificAbilityNames) &&
                IsEmpty(filter.FilterTargetAdjacentValidTilesFilterList) &&
                IsEmpty(filter.FilterCasterAdjacentValidTilesFilterList);
        }

        private static bool IsPlainRequirements(CAbilityRequirements requirements)
        {
            if (requirements == null)
            {
                return true;
            }
            // Factory-created abilities contain a default requirements object, not null.
            foreach (PropertyInfo property in typeof(CAbilityRequirements).GetProperties())
            {
                object value = property.GetValue(requirements, null);
                if (property.Name == nameof(CAbilityRequirements.RequirementActorFilter))
                {
                    if (value != null && !IsPlainFilter((CAbilityFilterContainer)value,
                        CAbilityFilter.EFilterTargetType.Self | CAbilityFilter.EFilterTargetType.Enemy |
                        CAbilityFilter.EFilterTargetType.Ally | CAbilityFilter.EFilterTargetType.Companion))
                    {
                        return false;
                    }
                }
                else if (value != null && !(value is ICollection collection && collection.Count == 0) &&
                    !(property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) == null &&
                      value.Equals(Activator.CreateInstance(property.PropertyType))))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsPlainMisc(AbilityData.MiscAbilityData data)
        {
            if (data == null)
            {
                return true;
            }
            foreach (FieldInfo field in typeof(AbilityData.MiscAbilityData).GetFields())
            {
                if (field.Name == nameof(AbilityData.MiscAbilityData.FilterSpecified) ||
                    field.Name == nameof(AbilityData.MiscAbilityData.TargetOneEnemyWithAllAttacks) &&
                    !Equals(field.GetValue(data), true))
                {
                    continue;
                }
                if (field.GetValue(data) != null)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsEmpty(ICollection values)
        {
            return values == null || values.Count == 0;
        }
    }
}
