using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using ScenarioRuleLibrary;
using ScenarioRuleLibrary.YML;

namespace GloomhavenPartyAI
{
    /// <summary>
    /// Main-thread, offline-only item submissions. The caller owns AutomateItems/IsAutomated checks
    /// and prompt scheduling. True means one command was submitted, NOT that an item was consumed.
    /// After true, stop the current decision, drain the rule queue and revalidate/replan the prompt.
    /// Wire TryConfirmHealingItem into targeting prompts BEFORE enabling action-selection item use:
    /// even a Self heal opens a confirmation prompt. No recovery/element/choice resolver is provided.
    /// </summary>
    internal static class ItemPlanner
    {
        private static readonly FieldInfo SlotInteractable = typeof(UIUseSlot<CItem>).GetField(
            "interactable", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SlotActor = typeof(UIUseSlot<CItem>).GetField(
            "actor", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo DeferredInfusions = typeof(CAbility).GetField(
            "m_InfuseElements", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>Use at ActionSelection, before selecting a card or ending the turn.</summary>
        internal static bool TryUseDuringActionSelection(CPlayerActor actor)
        {
            // DuringOwnTurn is also legal after a completed long rest, but not during bonus choices.
            if (!CanSubmit(actor) || PhaseManager.PhaseType != CPhase.PhaseType.ActionSelection ||
                actor.CharacterClass.LongRest || GameState.PendingOnLongRestBonuses.Count != 0 ||
                actor.Inventory.SelectedItems.Count != 0 ||
                CActiveBonus.FindApplicableActiveBonuses(actor, CAbility.EAbilityType.BlockHealing).Count > 0)
            {
                return false;
            }

            return TryUseBest(actor, null, item =>
            {
                if (item.YMLData.ItemType != CItem.EItemType.Ability ||
                    item.YMLData.Trigger != CItem.EItemTrigger.DuringOwnTurn ||
                    !IsHealingItem(item))
                {
                    return 0f;
                }
                CAbilityHeal heal = (CAbilityHeal)item.YMLData.Data.Abilities[0];
                return ItemEvaluation.HealingScore(actor.Health, actor.MaxHealth, heal.Strength,
                    actor.Tokens.HasKey(CCondition.ENegativeCondition.Poison),
                    actor.Tokens.HasKey(CCondition.ENegativeCondition.Wound),
                    item.YMLData.Usage == CItem.EUsageType.Consumed);
            });
        }

        /// <summary>
        /// Call before the ordinary heal planner at ActorIsSelectingTargetingFocus (and on resume).
        /// Confirms only the current, plain Self heal belonging to a selected supported item.
        /// The game already selects Self; TileSelected would toggle it OFF. Submit StepComplete once.
        /// </summary>
        internal static bool TryConfirmHealingItem(CPlayerActor actor, CAbilityHeal heal)
        {
            if (!CanSubmit(actor) || !IsCurrentAbility(actor, heal) || !IsPlainSelfHeal(heal) ||
                !heal.CanReceiveTileSelection() || heal.IsWaitingForSingleTargetItemOrActiveBonus() ||
                !heal.EnoughTargetsSelected() || heal.ActorsToTarget.Count != 1 ||
                heal.ActorsToTarget[0] != actor)
            {
                return false;
            }
            CPhaseAction.CPhaseAbility phaseAbility = ((CPhaseAction)PhaseManager.Phase).CurrentPhaseAbility;
            CItem item = phaseAbility.m_BaseCard as CItem;
            // Root item actions have no ItemID; only items stacked into another action have one.
            if (item == null || GameState.CurrentActionInitiator != GameState.EActionInitiator.ItemCard ||
                GameState.CurrentAction?.BaseCard != item ||
                !actor.Inventory.AllItems.Contains(item) || item.SlotState != CItem.EItemSlotState.Selected ||
                !IsHealingItem(item))
            {
                return false;
            }

            uint messageId = ScenarioRuleClient.StepComplete();
            if (messageId == 0)
            {
                return false;
            }
            Record("item_confirmed", actor, item, "supported", messageId: messageId);
            return true;
        }

        /// <summary>
        /// Call before submitting movement, only for a route the caller intends to take.
        /// usefulPathCost MUST be a game-calculated, legal/safe route cost under this move's rules,
        /// not straight-line distance. Zero means no demonstrated need. No speculative boot spending.
        /// On true, recompute reachable tiles and destination after the override is processed.
        /// </summary>
        internal static bool TryUseForMove(CPlayerActor actor, CAbilityMove move, int usefulPathCost)
        {
            if (!CanUseMoveItem(actor, move))
            {
                return false;
            }
            return TryUseBest(actor, move, item =>
                ItemEvaluation.MovementScore(move.RemainingMoves, usefulPathCost, PlainMoveBonus(item)),
                pathCost: usefulPathCost);
        }

        /// <summary>
        /// Read-only main-thread query using the same eligibility checks as TryUseForMove.
        /// Returns the largest available single-item bonus, never a sum or total movement budget.
        /// Zero means no supported usable boost. No toggle, restart, or ability mutation is performed.
        /// The caller still owns AutomateItems/IsAutomated and must revalidate before submitting.
        /// </summary>
        internal static int AvailableMoveBonus(CPlayerActor actor, CAbilityMove move)
        {
            if (!CanUseMoveItem(actor, move))
            {
                return 0;
            }
            int maximum = 0;
            foreach (CItem item in actor.Inventory.AllItems)
            {
                try
                {
                    if (IsAvailable(actor, item, move))
                    {
                        maximum = Math.Max(maximum, PlainMoveBonus(item));
                    }
                }
                catch (Exception)
                {
                    Record("item_evaluation", actor, item, "exception");
                }
            }
            return maximum;
        }

        private static bool CanUseMoveItem(CPlayerActor actor, CAbilityMove move)
        {
            return CanSubmit(actor) && IsCurrentAbility(actor, move) &&
                TacticalPlanner.IsSupportedMove(move) && !move.IsItemAbility && !move.IsMergedAbility &&
                move.State == CAbilityMove.EMoveState.ActorIsSelectingMoveTile && !move.HasMoved &&
                !actor.Tokens.HasKey(CCondition.ENegativeCondition.Immobilize) &&
                actor.Inventory.SelectedItems.Count == 0 && move.ActiveOverrideItems.Count == 0;
        }

        private static int PlainMoveBonus(CItem item)
        {
            return item.YMLData.ItemType == CItem.EItemType.Override &&
                item.YMLData.Trigger == CItem.EItemTrigger.SingleAbility &&
                item.YMLData.Usage == CItem.EUsageType.Spent &&
                TryGetPlainOverride(item, out int strength, out bool advantage) && !advantage
                ? strength : 0;
        }

        /// <summary>
        /// Call at SelectAttackFocus, before StepComplete, with the target the controller intends
        /// to attack. Only the tactical planner's supported single-target attacks are eligible.
        /// The target need not yet be selected, but must be in the game's ValidActorsInRange.
        /// </summary>
        internal static bool TryUseForAttack(CPlayerActor actor, CAbilityAttack attack, CActor intendedTarget)
        {
            // ActiveOverrideItems records applied modifiers, not just currently selected inventory
            // slots. Keep the one-item policy even if an EntireAction item has already been spent.
            if (!CanSubmit(actor) || !IsCurrentAbility(actor, attack) ||
                !TacticalPlanner.IsSupportedAttack(attack) || attack.IsItemAbility || attack.IsMergedAbility ||
                attack.State != CAbilityAttack.EAttackState.SelectAttackFocus || attack.AbilityHasHappened ||
                attack.IsWaitingForSingleTargetItemOrActiveBonus() ||
                actor.Tokens.HasKey(CCondition.ENegativeCondition.Disarm) ||
                actor.Inventory.SelectedItems.Count != 0 || attack.ActiveOverrideItems.Count != 0 ||
                intendedTarget == null || intendedTarget == actor || intendedTarget.IsDead ||
                !attack.ValidActorsInRange.Contains(intendedTarget))
            {
                return false;
            }
            bool alreadyHasAdvantage = actor.Tokens.HasKey(CCondition.EPositiveCondition.Strengthen) ||
                actor.Tokens.HasKey(CCondition.EPositiveCondition.Advantage) ||
                attack.MiscAbilityData?.AttackHasAdvantage == true ||
                CActiveBonus.FindApplicableActiveBonuses(actor, CAbility.EAbilityType.Advantage).Count > 0;
            int shield = Math.Max(0, intendedTarget.CalculateShield(attack) - attack.Pierce);
            return TryUseBest(actor, attack, item =>
            {
                if (item.YMLData.ItemType != CItem.EItemType.Override ||
                    (item.YMLData.Trigger != CItem.EItemTrigger.SingleAbility &&
                     item.YMLData.Trigger != CItem.EItemTrigger.EntireAction) ||
                    !TryGetPlainOverride(item, out int strength, out bool advantage))
                {
                    return 0f;
                }
                return ItemEvaluation.AttackScore(attack.ModifiedStrength(), shield, intendedTarget.Health,
                    strength, advantage, alreadyHasAdvantage, item.YMLData.Usage == CItem.EUsageType.Consumed);
            }, targetId: intendedTarget.ID);
        }

        private static bool CanSubmit(CPlayerActor actor)
        {
            return actor != null && !FFSNetwork.IsOnline && !ScenarioRuleClient.IsProcessingOrMessagesQueued &&
                GameState.InternalCurrentActor == actor && !actor.IsDead && actor.Health > 0 &&
                actor.Type == CActor.EType.Player && actor.Inventory != null && !actor.ItemLocked &&
                !GameState.WaitingForPlayerToSelectDamageResponse &&
                !GameState.WaitingForMercenarySpecialMechanicSlotChoice &&
                !actor.Tokens.HasKey(CCondition.ENegativeCondition.Stun) &&
                !actor.Tokens.HasKey(CCondition.ENegativeCondition.Sleep);
        }

        private static bool IsCurrentAbility(CPlayerActor actor, CAbility ability)
        {
            return ability != null && ability.TargetingActor == actor &&
                PhaseManager.Phase is CPhaseAction phase && phase.CurrentPhaseAbility?.m_Ability == ability;
        }

        private static bool TryUseBest(CPlayerActor actor, CAbility ability, Func<CItem, float> score,
            int? targetId = null, int? pathCost = null)
        {
            CItem best = null;
            float bestScore = 0f;
            foreach (CItem item in actor.Inventory.AllItems)
            {
                try
                {
                    if (!IsAvailable(actor, item, ability))
                    {
                        Record("item_candidate", actor, item, "unavailable", targetId: targetId, pathCost: pathCost);
                        continue;
                    }
                    Record("item_candidate", actor, item, "supported", targetId: targetId, pathCost: pathCost);
                    float value = score(item);
                    Record("item_evaluation", actor, item, value > 0f ? "supported" : "unhelpful",
                        value, targetId, pathCost);
                    if (value > bestScore || value == bestScore && value > 0f && best != null &&
                        item.YMLData.Usage == CItem.EUsageType.Spent && best.YMLData.Usage == CItem.EUsageType.Consumed)
                    {
                        best = item;
                        bestScore = value;
                    }
                }
                catch (Exception)
                {
                    Record("item_evaluation", actor, item, "exception", targetId: targetId, pathCost: pathCost);
                }
            }
            if (best == null || !CanSubmit(actor) || !IsAvailable(actor, best, ability))
            {
                return false;
            }

            // UseItemService does the UI interlocks then queues ScenarioRuleClient.ToggleItem.
            // CInventory.UseItem only marks spent/consumed; it does NOT activate the effect.
            new UseItemService(actor).UseItem(best, networkActionIfOnline: false);
            Record("item_submitted", actor, best, "best_score", bestScore, targetId, pathCost);
            return true;
        }

        private static bool IsAvailable(CPlayerActor actor, CItem item, CAbility ability)
        {
            if (item == null || item.SlotState != CItem.EItemSlotState.Useable || actor?.Inventory == null ||
                !actor.Inventory.AllItems.Contains(item) || !HasSupportedMetadata(item))
            {
                return false;
            }
            ItemData data = item.YMLData.Data;
            if (data.CompareAbility != null && (ability == null || !data.CompareAbility.CompareAbility(ability)))
            {
                return false;
            }
            if (data.ItemRequirements != null && !data.ItemRequirements.MeetsAbilityRequirements(actor, ability))
            {
                return false;
            }
            if (!Singleton<UIUseItemsBar>.IsInitialized || Choreographer.s_Choreographer == null ||
                Choreographer.s_Choreographer.readyButton == null ||
                Choreographer.s_Choreographer.m_SkipButton == null ||
                Choreographer.s_Choreographer.m_UndoButton == null)
            {
                return false;
            }
            UIUseItemsBar bar = Singleton<UIUseItemsBar>.Instance;
            return bar.IsShown && bar.ItemSlots.TryGetValue(item, out UIUseItemScenario slot) &&
                slot != null && slot.isActiveAndEnabled && !slot.IsSelected() &&
                slot.ConsumesNum == 0 && slot.InfusionsNum == 0 && SlotInteractable != null && SlotActor != null &&
                Equals(SlotInteractable.GetValue(slot), true) && ReferenceEquals(SlotActor.GetValue(slot), actor);
        }

        private static bool HasSupportedMetadata(CItem item)
        {
            ItemCardYMLData yml = item.YMLData;
            ItemData data = yml.Data;
            return data != null && (yml.Usage == CItem.EUsageType.Spent || yml.Usage == CItem.EUsageType.Consumed) &&
                yml.PermanentlyConsumed != true && yml.UsedWhenEquipped != true &&
                yml.Slot != CItem.EItemSlot.QuestItem && IsEmpty(yml.Consumes) && IsEmpty(item.ChosenElement) &&
                data.CharacterFilter == null && IsEmpty(data.CompareConditions) && data.ShieldValue == 0 &&
                data.RetaliateValue == 0 && data.SmallSlots == 0 &&
                IsEmpty(data.AdditionalModifiers) && IsEmpty(data.RemoveModifiers);
        }

        private static bool IsHealingItem(CItem item)
        {
            return HasSupportedMetadata(item) && item.YMLData.ItemType == CItem.EItemType.Ability &&
                item.YMLData.Trigger == CItem.EItemTrigger.DuringOwnTurn &&
                IsEmpty(item.YMLData.Data.Overrides) && item.YMLData.Data.Abilities?.Count == 1 &&
                IsPlainSelfHeal(item.YMLData.Data.Abilities[0] as CAbilityHeal);
        }

        private static bool IsPlainSelfHeal(CAbilityHeal heal)
        {
            if (heal == null || heal.GetType() != typeof(CAbilityHeal) || heal.Strength <= 0 ||
                heal.Strength == int.MaxValue || heal.Range <= 0 ||
                heal.Targeting != CAbility.EAbilityTargeting.Range || heal.NumberTargets != 1 || heal.AreaEffect != null ||
                heal.AllTargets || heal.OneTargetAtATime || heal.IsSubAbility || heal.IsInlineSubAbility ||
                heal.IsMergedAbility || heal.ParentAbility != null || heal.AllTargetsOnMovePath ||
                heal.AllTargetsOnAttackPath || heal.IsConsumeAbility || heal.AbilityTextOnly || heal.OnDeath ||
                heal.UseSpecialBaseStat || heal.ProcessIfDead || heal.IsControlAbility ||
                !IsEmpty(heal.AbilityEnhancements) || !IsEmpty(heal.SubAbilities) || !IsEmpty(heal.ConditionalOverrides) ||
                !IsEmpty(heal.StatIsBasedOnXEntries) || !IsEmpty(heal.PositiveConditions) ||
                !IsEmpty(heal.NegativeConditions) || !IsEmpty(heal.ResourcesToAddOnAbilityEnd) ||
                !IsEmpty(heal.ResourcesToTakeFromTargets) || !IsEmpty(heal.ResourcesToGiveToTargets) ||
                heal.Augment != null || heal.Song != null ||
                heal.ActiveBonusData != null && (heal.ActiveBonusData.Duration != CActiveBonus.EActiveBonusDurationType.NA ||
                    heal.ActiveBonusData.Behaviour != CActiveBonus.EActiveBonusBehaviourType.None ||
                    !IsEmpty(heal.ActiveBonusData.Consuming) || !IsEmpty(heal.ActiveBonusData.ActiveBonusAbilityOverrides) ||
                    heal.ActiveBonusData.AbilityData != null || heal.ActiveBonusData.CostAbility != null) ||
                heal.StartAbilityRequirements != null &&
                    heal.StartAbilityRequirements.StartAbilityRequirementType != CAbilityRequirements.EStartAbilityRequirementType.None ||
                !IsPlainMisc(heal.MiscAbilityData, false) || DeferredInfusions == null ||
                !IsEmpty(DeferredInfusions.GetValue(heal) as ICollection))
            {
                return false;
            }
            CAbilityFilterContainer filter = heal.AbilityFilter;
            CAbilityHeal.HealAbilityData data = heal.HealData;
            return filter?.AbilityFilters?.Count == 1 && !filter.HasNonTargetTypeFilters() &&
                !filter.AbilityFilters[0].Invert &&
                filter.HasTargetTypeFlag(CAbilityFilter.EFilterTargetType.Self, exclusive: true) &&
                data != null && !data.IgnoreTokens && IsEmpty(data.PositiveConditionsToAddIfHealRemovesPoison) &&
                IsEmpty(data.NegativeConditionsToAddIfHealRemovesPoison) &&
                IsEmpty(data.PositiveConditionsToAddIfHealRemovesWound) &&
                IsEmpty(data.NegativeConditionsToAddIfHealRemovesWound);
        }

        private static bool TryGetPlainOverride(CItem item, out int strength, out bool advantage)
        {
            strength = 0;
            advantage = false;
            ItemData data = item.YMLData.Data;
            if (!IsEmpty(data.Abilities) || data.Overrides?.Count != 1)
            {
                return false;
            }
            CAbilityOverride effect = data.Overrides[0];
            if (effect == null || !IsPlainMisc(effect.MiscAbilityData, true))
            {
                return false;
            }
            // Overrides have many nullable knobs. Whitelist populated fields instead of silently
            // accepting a new effect (sub-ability, element picker, self-damage, target count, etc.).
            foreach (PropertyInfo property in typeof(CAbilityOverride).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                switch (property.Name)
                {
                    case nameof(CAbilityOverride.AbilityName):
                    case nameof(CAbilityOverride.ParentName):
                    case nameof(CAbilityOverride.OriginalAbility):
                    case nameof(CAbilityOverride.Strength):
                    case nameof(CAbilityOverride.MiscAbilityData):
                        continue;
                    case nameof(CAbilityOverride.IsConsumeAbility):
                    case nameof(CAbilityOverride.IsInlineSubAbility):
                    case nameof(CAbilityOverride.IsSubAbility):
                        if (!Equals(property.GetValue(effect, null), true))
                        {
                            continue;
                        }
                        return false;
                }
                object value = property.GetValue(effect, null);
                if (value != null && !(value is ICollection collection && collection.Count == 0))
                {
                    return false;
                }
            }
            // OverrideAbilityValues adds Strength to m_Strength/m_ModifiedStrength; this is a
            // delta, not a replacement stat. StrengthIsBase/stat-scaling fields remain rejected.
            strength = effect.Strength ?? 0;
            advantage = effect.MiscAbilityData?.AttackHasAdvantage == true;
            return strength >= 0 && strength < int.MaxValue && (strength > 0 || advantage);
        }

        private static bool IsPlainMisc(AbilityData.MiscAbilityData data, bool allowAdvantage)
        {
            if (data == null)
            {
                return true;
            }
            foreach (FieldInfo field in typeof(AbilityData.MiscAbilityData).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.Name == nameof(AbilityData.MiscAbilityData.FilterSpecified) ||
                    allowAdvantage && field.Name == nameof(AbilityData.MiscAbilityData.AttackHasAdvantage))
                {
                    continue;
                }
                // The YAML parser explicitly defaults this nullable field to false, even on heals.
                if (field.Name == nameof(AbilityData.MiscAbilityData.TargetOneEnemyWithAllAttacks) &&
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

        private static void Record(string kind, CActor actor, CItem item, string reason,
            float? score = null, int? targetId = null, int? pathCost = null, uint? messageId = null)
        {
            try
            {
                if (!DeveloperDiagnostics.Enabled)
                {
                    return;
                }
                // Only numeric IDs and allowlisted codes: never item names or exception messages.
                string detail = "action=item;reason=" + reason;
                if (item != null) detail += ";item_id=" + item.ID.ToString(CultureInfo.InvariantCulture);
                if (score.HasValue) detail += ";score=" + score.Value.ToString("0.00", CultureInfo.InvariantCulture);
                if (targetId.HasValue) detail += ";target_id=" + targetId.Value.ToString(CultureInfo.InvariantCulture);
                if (pathCost.HasValue) detail += ";path_cost=" + pathCost.Value.ToString(CultureInfo.InvariantCulture);
                if (messageId.HasValue) detail += ";message_id=" + messageId.Value.ToString(CultureInfo.InvariantCulture);
                DeveloperDiagnostics.Record(kind, actor, detail);
            }
            catch (Exception)
            {
                // Diagnostics must never interrupt a submitted command or a live decision.
            }
        }
    }
}
