using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using GLOOM;
using HarmonyLib;
using ScenarioRuleLibrary;
using Script.GUI.Popups;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace GloomhavenPartyAI
{
    // Patch this type separately. All entry points run on the scenario main thread.
    [HarmonyPatch]
    internal static class ShortRestPlanner
    {
        private static readonly FieldInfo Buttons = AccessTools.Field(typeof(DialogPopup), "optionButtons");
        private static readonly FieldInfo Content = AccessTools.Field(typeof(DialogPopup), "contentState");
        private static readonly FieldInfo RestButton = AccessTools.Field(typeof(CardsHandUI), "shortRest");
        private static readonly FieldInfo Rested = AccessTools.Field(typeof(CardsHandUI), "shortRested");
        private static readonly FieldInfo LosingCards = AccessTools.Field(typeof(CardsHandUI), "animatedLosingCard");
        private static readonly FieldInfo FirstDraw = AccessTools.Field(typeof(CardsHandUI), "shortRestLostCardID");
        private static readonly FieldInfo SecondDraw = AccessTools.Field(typeof(CardsHandUI), "shortRestAlternateLostCardID");
        private static readonly FieldInfo Previewing = AccessTools.Field(typeof(CardsHandManager), "isPreviewing");
        private static readonly FieldInfo DeckPreview = AccessTools.Field(typeof(CardsHandManager), "previewDecksWindow");
        private static readonly FieldInfo CardViews = AccessTools.Field(typeof(CardsHandUI), "cardsUI");
        private static readonly FieldInfo UiLocks = AccessTools.Field(typeof(UIManager), "elementsLockUI");
        private static readonly FieldInfo RestConfirmation = AccessTools.Field(typeof(ShortRest), "yesNoDialog");
        private static readonly FieldInfo ConfirmationWindow = AccessTools.Field(typeof(YesNoDialog), "window");
        private static readonly FieldInfo EventCalls = AccessTools.Field(typeof(UnityEventBase), "m_Calls");
        private static readonly FieldInfo RuntimeCalls = EventCalls == null ? null :
            AccessTools.Field(EventCalls.FieldType, "m_RuntimeCalls");
        private static CPlayerActor _owner;
        private static CardsHandUI _hand;
        private static CardsHandUI _previousHand;
        private static CardsHandManager _manager;
        private static CPlayerActor _expectedSwitch;
        private static DialogPopup _popup;
        private static object _scenario;
        private static int _round, _health, _frame, _shows;
        private static long _generation, _ownedGeneration;
        private static bool _invoking, _redrawn, _submitted;
        private static bool _preparing, _focusChanged;
        private static float _prepareDeadline;
        private static float _completionDeadline = -1f;
        private static CAbilityCard[] _discards;
        private static CAbilityCard _drawn, _displayed;
        private static GameObject _content;
        private static DialogOption[] _options;
        private static UnityAction[] _callbacks;
        private static ExtendedButton[] _buttons;
        private static object[][] _listeners;
        private static bool _capabilityChecked, _capabilityReady;
        private static CPlayerActor _blockedActor;
        private static string _reportedBlock;

        internal static string Status { get; private set; } = "idle";
        internal static string LastBlockReason { get; private set; } = "rest_ready";
        internal static bool IsTransientBlock { get; private set; }
        internal static CPlayerActor Owner => _owner;
        internal static bool IsPending(CPlayerActor actor) => IsPendingAny && ReferenceEquals(actor, _owner);
        internal static bool IsPendingAny => _owner != null;

        // Lifecycle only. On a toggle, retain the token; TryContinue cannot click for a manual owner.
        // Reset never closes a popup, restores a pile, or resets the game's cached RNG draws.
        internal static void Reset()
        {
            _owner = null;
            _hand = null;
            _previousHand = null;
            _manager = null;
            _expectedSwitch = null;
            _popup = null;
            _scenario = null;
            _discards = null;
            _drawn = _displayed = null;
            _content = null;
            _options = null;
            _callbacks = null;
            _buttons = null;
            _listeners = null;
            _invoking = _redrawn = _submitted = false;
            _preparing = _focusChanged = false;
            _completionDeadline = -1f;
            Status = "idle";
            LastBlockReason = "rest_ready";
            IsTransientBlock = false;
        }

        // True means an attempt was claimed, possibly still preparing; do not call TryStart again.
        // The controller must exclude ALL other party decisions while IsPendingAny is true.
        internal static bool TryStart(CPlayerActor actor)
        {
            if (ScenarioRuleClient.s_MainThread != Thread.CurrentThread) return false;
            EnsureCapability();
            if (IsPendingAny) return Block(actor, "rest_other_hand_busy", _hand, transient: false);
            Status = "not_ready";
            string reason = PreflightReason(actor, prepared: false, out CardsHandUI blocker);
            if (reason != "rest_ready") return Block(actor, reason, blocker);
            if (!AutomationController.IsAutomated(actor)) return Block(actor, "rest_rules");
            CardsHandManager manager = CardsHandManager.Instance;
            CardsHandUI hand = manager?.GetHand(actor);
            DialogPopup popup = UIManager.Instance?.dialogPopup;
            _owner = actor;
            _hand = hand;
            _manager = manager;
            _previousHand = manager.CurrentHand != hand ? manager.CurrentHand : null;
            _popup = popup;
            _scenario = ScenarioManager.CurrentScenarioState;
            _round = ScenarioManager.CurrentScenarioState.RoundNumber;
            _health = actor.Health;
            _discards = actor.CharacterClass.DiscardedAbilityCards.ToArray();
            _preparing = true;
            _prepareDeadline = Time.realtimeSinceStartup + 3f;
            _frame = Time.frameCount;
            Status = "preparing";
            LastBlockReason = "rest_ready";
            IsTransientBlock = false;
            try
            {
                if (manager.CurrentHand != hand || !manager.IsShowingPlayerHand(actor))
                {
                    _invoking = true;
                    _expectedSwitch = actor;
                    manager.SwitchHand(actor);
                }
            }
            catch (Exception exception)
            {
                Reset();
                Status = "manual";
                Block(actor, "rest_target_hand", transient: false);
                Plugin.Log?.LogWarning("Short rest preparation left manual: " + exception.Message);
                return true;
            }
            finally
            {
                _expectedSwitch = null;
                _invoking = false;
            }
            Prepare();
            return true;
        }

        internal static bool TryContinue(CPlayerActor actor)
        {
            if (ScenarioRuleClient.s_MainThread != Thread.CurrentThread || !IsPending(actor) ||
                _invoking || Time.frameCount <= _frame) return false;
            if (actor.CharacterClass == null || !ReferenceEquals(_scenario, ScenarioManager.CurrentScenarioState) ||
                ScenarioManager.CurrentScenarioState.RoundNumber != _round ||
                _manager == null || CardsHandManager.Instance != _manager ||
                PhaseManager.PhaseType != CPhase.PhaseType.SelectAbilityCardsOrLongRest)
            {
                Reset();
                Status = "manual";
                Block(actor, "rest_rules", transient: false);
                return false;
            }
            if (_preparing) return Prepare();
            // Poll even when automation is disabled, so a manually completed rest can release its lock.
            bool settled = !ScenarioRuleClient.IsProcessingOrMessagesQueued &&
                (_popup == null || !_popup.IsOpen()) &&
                (UIManager.Instance?.dialogPopup == null || !UIManager.Instance.dialogPopup.IsOpen());
            if (actor.CharacterClass.HasShortRested && settled)
            {
                if (actor.IsDead || actor.Health <= 0 || _hand == null || !_hand.isActiveAndEnabled ||
                    !_hand.gameObject.activeInHierarchy || _focusChanged || _manager.CurrentHand != _hand ||
                    _manager.ActivePlayer != actor || !_manager.IsShowingPlayerHand(actor) ||
                    _manager.GetHand(actor) != _hand)
                {
                    Complete(restoreHand: false);
                    return true;
                }
                if (_hand.ShortRestedCard == null && !AnimationInProgress(_hand))
                {
                    Complete();
                    return true;
                }
                // Hiding a hand can stop AnimateCardsLost without clearing its flag. Never edit that flag.
                if (_completionDeadline < 0f) _completionDeadline = Time.realtimeSinceStartup + 10f;
                if (Time.realtimeSinceStartup < _completionDeadline) return false;
                Reset();
                Status = "manual";
                Block(actor, "rest_target_hand", transient: false);
                return true;
            }
            _completionDeadline = -1f;
            if (_hand == null || _manager.GetHand(actor) != _hand ||
                (settled && !actor.CharacterClass.HasShortRested && _hand.ShortRestedCard == null &&
                 Equals(FirstDraw.GetValue(_hand), -1) && Equals(SecondDraw.GetValue(_hand), -1)))
            {
                // The hand was replaced or the game reset the uncommitted rest (for example, undo).
                Reset();
                Status = "manual";
                Block(actor, "rest_target_hand", transient: false);
                return false;
            }
            if (_submitted || !CanAct(actor)) return false;
            if (!MatchesPrompt())
            {
                Status = "manual";
                Block(actor, "rest_modal", transient: false);
                return false;
            }
            int option = !_redrawn && actor.Health > 1 && _options.Length == 2 &&
                RecoveryPlanner.HasRecoveryAction(_displayed) &&
                !actor.CharacterClass.LostAbilityCards.Contains(_displayed) &&
                !actor.CharacterClass.PermanentlyLostAbilityCards.Contains(_displayed) ? 1 : 0;
            ExtendedButton button = _buttons[option];
            if (!button.isActiveAndEnabled || !button.gameObject.activeInHierarchy || !button.IsInteractable())
                return false;
            if (option == 1) _redrawn = true;
            else _submitted = true;
            // Invoke the game's button, including Hide and its original closure, never a rule shortcut.
            InvokeUi(() => button.onClick.Invoke());
            if (Status == "redrawn" || Status == "submitted")
                DeveloperDiagnostics.Record("rest", actor, "action=rest;reason=" + (option == 1 ? "redraw" : "short_rest"));
            return true;
        }

        private static bool AnimationInProgress(CardsHandUI hand)
        {
            // Cancellation clears this private flag but can leave AnimatingLostCards true.
            // Reading both prevents a hidden/cancelled hand from blocking every later rest.
            return hand != null && hand.AnimatingLostCards &&
                (LosingCards == null || Equals(LosingCards.GetValue(hand), true));
        }

        private static void EnsureCapability()
        {
            if (_capabilityChecked) return;
            _capabilityChecked = true;
            _capabilityReady = true;
            // Resolve/inspect once after PatchAll, not during every frame of a UI wait.
            var required = new[]
            {
                new { Field = Buttons, Name = "DialogPopup.optionButtons" },
                new { Field = Content, Name = "DialogPopup.contentState" },
                new { Field = RestButton, Name = "CardsHandUI.shortRest" },
                new { Field = Rested, Name = "CardsHandUI.shortRested" },
                new { Field = LosingCards, Name = "CardsHandUI.animatedLosingCard" },
                new { Field = FirstDraw, Name = "CardsHandUI.shortRestLostCardID" },
                new { Field = SecondDraw, Name = "CardsHandUI.shortRestAlternateLostCardID" },
                new { Field = Previewing, Name = "CardsHandManager.isPreviewing" },
                new { Field = DeckPreview, Name = "CardsHandManager.previewDecksWindow" },
                new { Field = CardViews, Name = "CardsHandUI.cardsUI" },
                new { Field = UiLocks, Name = "UIManager.elementsLockUI" },
                new { Field = RestConfirmation, Name = "ShortRest.yesNoDialog" },
                new { Field = ConfirmationWindow, Name = "YesNoDialog.window" },
                new { Field = EventCalls, Name = "UnityEventBase.m_Calls" },
                new { Field = RuntimeCalls, Name = "InvokableCallList.m_RuntimeCalls" }
            };
            foreach (var member in required)
                if (member.Field == null)
                {
                    _capabilityReady = false;
                    Plugin.Log?.LogWarning("Short rest capability missing field: " + member.Name);
                }
            MethodBase[] methods = TargetMethods().ToArray();
            foreach (string name in new[] { "Show", "Hide", "ShowLoadedContent" })
                if (!methods.Any(method => method?.DeclaringType == typeof(DialogPopup) && method.Name == name))
                {
                    _capabilityReady = false;
                    Plugin.Log?.LogWarning("Short rest capability missing method: DialogPopup." + name);
                }
            foreach (MethodBase method in methods)
            {
                var patches = method == null ? null : Harmony.GetPatchInfo(method);
                bool prefix = patches?.Prefixes.Any(p => p.PatchMethod.DeclaringType == typeof(ShortRestPlanner)) == true;
                bool postfix = patches?.Postfixes.Any(p => p.PatchMethod.DeclaringType == typeof(ShortRestPlanner)) == true;
                if (!prefix || !postfix)
                {
                    _capabilityReady = false;
                    Plugin.Log?.LogWarning("Short rest capability missing " +
                        (method == null ? "method" : !prefix && !postfix ? "prefix and postfix" : !prefix ? "prefix" : "postfix") + ": " +
                        (method == null ? "CardsHandManager.SwitchHand(CPlayerActor)" :
                         method.DeclaringType.FullName + "." + method));
                }
            }
        }

        private static bool HasPendingDraw(CardsHandUI hand)
        {
            return hand.ShortRestedCard != null || !Equals(FirstDraw.GetValue(hand), -1) ||
                !Equals(SecondDraw.GetValue(hand), -1);
        }

        private static string UiBlock(CardsHandManager manager, out CardsHandUI blocker)
        {
            blocker = null;
            if (manager == null || UIManager.Instance?.dialogPopup == null) return "rest_target_hand";
            bool modal = UIManager.Instance.dialogPopup.IsOpen();
            // The manager's full-preview getter aggregates hidden hand flags, not viewer visibility.
            if (!Equals(Previewing.GetValue(manager), false) ||
                !(DeckPreview.GetValue(manager) is CardsHandPreviewWindow preview) || preview.IsOpen ||
                Singleton<FullCardHandViewer>.Instance?.IsActive == true) return "rest_preview";
            // Only pending draws and genuinely live UI ownership cross hand boundaries. A hidden
            // hand's preview/selection/rested flags do not describe the current view.
            foreach (CardsHandUI hand in manager.CardHandsUI)
            {
                if (hand == null) continue;
                ShortRest rest = RestButton.GetValue(hand) as ShortRest;
                YesNoDialog confirmation = rest == null ? null : RestConfirmation.GetValue(rest) as YesNoDialog;
                UIWindow window = confirmation == null ? null : ConfirmationWindow.GetValue(confirmation) as UIWindow;
                bool visible = hand.isActiveAndEnabled && hand.gameObject.activeInHierarchy;
                if (HasPendingDraw(hand) || (visible && AnimationInProgress(hand)) ||
                    (window != null && window.IsOpen && window.gameObject.activeInHierarchy) ||
                    (visible && rest != null && rest.IsSelected))
                {
                    blocker = hand;
                    return modal ? "rest_modal" : "rest_other_hand_busy";
                }
                if (visible && hand.IsPreviewingCards)
                {
                    blocker = hand;
                    return "rest_preview";
                }
            }
            UIWindow focused = UIWindow.FocusedWindow;
            if (modal || (focused != null && focused.IsPopUp && focused.IsOpen && focused.gameObject.activeInHierarchy) ||
                !(UiLocks.GetValue(UIManager.Instance) is HashSet<GameObject> locks) || locks.Count != 0)
                return "rest_modal";
            return "rest_ready";
        }

        private static bool CardViewsReady(CardsHandUI hand)
        {
            if (hand?.PlayerActor?.CharacterClass == null ||
                !(CardViews.GetValue(hand) is List<AbilityCardUI> views)) return false;
            int longRest = 0;
            foreach (AbilityCardUI view in views)
                if (view != null && view.CardID == -1)
                {
                    if (!view.IsLongRest || view.PlayerActor != hand.PlayerActor) return false;
                    longRest++;
                }
            if (longRest != 1) return false;
            // PerformShortRest draws from ALL discards and GetCardUI matches AbilityCard identity,
            // not CardType, visibility or selectability. GetCard also logs on misses, so don't poll it.
            foreach (CAbilityCard card in hand.PlayerActor.CharacterClass.DiscardedAbilityCards)
            {
                if (card == null) return false;
                int matches = 0;
                foreach (AbilityCardUI view in views)
                    if (view != null && view.CardID == card.ID)
                    {
                        if (!ReferenceEquals(view.AbilityCard, card) || view.PlayerActor != hand.PlayerActor ||
                            view.fullAbilityCard == null) return false;
                        matches++;
                    }
                if (matches != 1) return false;
            }
            return true;
        }

        private static string PreflightReason(CPlayerActor actor, bool prepared, out CardsHandUI blocker)
        {
            blocker = null;
            if (!_capabilityReady) return "rest_capability";
            if (ScenarioRuleClient.IsProcessingOrMessagesQueued) return "rest_queue";
            if (!RulesReady(actor)) return "rest_rules";
            CardsHandManager manager = CardsHandManager.Instance;
            string reason = UiBlock(manager, out blocker);
            if (reason == "rest_other_hand_busy" && blocker?.PlayerActor == actor) return "rest_target_hand";
            if (reason != "rest_ready") return reason;
            CardsHandUI hand = manager.GetHand(actor);
            CardsHandUI current = manager.CurrentHand;
            if (hand == null || hand.PlayerActor != actor) return "rest_target_hand";
            if (current != null && current.isActiveAndEnabled && current.gameObject.activeInHierarchy &&
                (manager.ActivePlayer != current.PlayerActor || current.currentMode != CardHandMode.CardsSelection))
            {
                blocker = current;
                return current == hand ? "rest_target_hand" : "rest_other_hand_busy";
            }
            // Hidden target presentation is allowed to refresh through the game's claimed SwitchHand.
            if (!prepared) return "rest_ready";
            if (current != hand || manager.ActivePlayer != actor || !hand.isActiveAndEnabled ||
                !hand.gameObject.activeInHierarchy || !manager.IsShowingPlayerHand(actor) ||
                !hand.IsInteractable || hand.currentMode != CardHandMode.CardsSelection || hand.SelectedCards.Count != 0 ||
                !Equals(Rested.GetValue(hand), false)) return "rest_target_hand";
            ShortRest rest = RestButton.GetValue(hand) as ShortRest;
            if (rest == null || rest.PlayerActor != actor || rest.Button == null || rest.IsSelected ||
                !rest.isActiveAndEnabled || !rest.gameObject.activeInHierarchy || !rest.Button.IsInteractable())
                return "rest_target_hand";
            return CardViewsReady(hand) ? "rest_ready" : "rest_card_views";
        }

        private static bool IdleUi(CardsHandManager manager) => UiBlock(manager, out _) == "rest_ready";

        private static bool Block(CPlayerActor actor, string reason, CardsHandUI blocker = null, bool transient = true)
        {
            LastBlockReason = reason;
            IsTransientBlock = transient && reason != "rest_capability" && reason != "rest_rules";
            if (!ReferenceEquals(_blockedActor, actor) || _reportedBlock != reason)
            {
                _blockedActor = actor;
                _reportedBlock = reason;
                if (DeveloperDiagnostics.Enabled)
                {
                    try
                    {
                        DeveloperDiagnostics.Record("short_rest_blocked", actor,
                            "action=rest;reason=" + reason + PreflightDetail(actor, blocker, diagnostic: true));
                    }
                    catch (Exception exception)
                    {
                        Plugin.Log?.LogWarning("Short rest diagnostic snapshot unavailable: " + exception.Message);
                    }
                }
            }
            return false;
        }

        // Main-thread, read-only polling. Save at a transient short-rest handoff and compare later;
        // only clear that handoff when !IsPendingAny. Never use it to recover an owned/manual popup.
        internal static string PreflightSignature(CPlayerActor actor)
        {
            if (ScenarioRuleClient.s_MainThread != Thread.CurrentThread) return "rest_capability";
            string reason = PreflightReason(actor, prepared: true, out CardsHandUI blocker);
            return reason + PreflightDetail(actor, blocker);
        }

        private static string PreflightDetail(CPlayerActor actor, CardsHandUI blocker, bool diagnostic = false)
        {
            CardsHandManager manager = CardsHandManager.Instance;
            CardsHandUI hand = actor == null ? null : manager?.GetHand(actor);
            UIManager ui = UIManager.Instance;
            CardsHandPreviewWindow preview = manager == null ? null : DeckPreview?.GetValue(manager) as CardsHandPreviewWindow;
            UIWindow focused = UIWindow.FocusedWindow;
            string detail = string.Format(CultureInfo.InvariantCulture,
                ";actor_id={0};blocking_actor_id={1};current_actor_id={2};target_actor_id={3};active_actor_id={4}" +
                ";mode={5};queue={6};modal={7};ui_preview={8};deck_preview={9};full_preview={10};ui_locks={11};current_mode={12}",
                actor?.ID ?? -1, blocker?.PlayerActor?.ID ?? -1, manager?.CurrentHand?.PlayerActor?.ID ?? -1,
                hand?.PlayerActor?.ID ?? -1, manager?.ActivePlayer?.ID ?? -1,
                hand == null ? -1 : (int)hand.currentMode, ScenarioRuleClient.IsProcessingOrMessagesQueued ? 1 : 0,
                ui?.dialogPopup == null ? -1 : ui.dialogPopup.IsOpen() ? 1 : 0,
                manager == null ? -1 : Equals(Previewing?.GetValue(manager), true) ? 1 : 0,
                preview == null ? -1 : preview.IsOpen ? 1 : 0,
                Singleton<FullCardHandViewer>.Instance == null ? -1 : Singleton<FullCardHandViewer>.Instance.IsActive ? 1 : 0,
                ui == null ? -1 : (UiLocks?.GetValue(ui) as HashSet<GameObject>)?.Count ?? -1,
                manager?.CurrentHand == null ? -1 : (int)manager.CurrentHand.currentMode);
            // The diagnostics writer bounds detail to 1024 characters. Keep extended retry state
            // out of that event; neither path walks scene objects or calls logging GetCard lookups.
            if (diagnostic) return detail + HandDetail("target", hand, true) + HandDetail("blocking", blocker, true);
            return detail + string.Format(CultureInfo.InvariantCulture,
                ";phase={0};round={1};health={2};hand={3};discarded={4};round_cards={5};rules_ready={6}" +
                ";manager_id={7};popup_id={8};focused_modal_id={9};target_card_views={10}",
                PhaseManager.CurrentPhase == null ? -1 : (int)PhaseManager.PhaseType,
                ScenarioManager.CurrentScenarioState?.RoundNumber ?? -1, actor?.Health ?? -1,
                actor?.CharacterClass?.HandAbilityCards.Count ?? -1, actor?.CharacterClass?.DiscardedAbilityCards.Count ?? -1,
                actor?.CharacterClass?.RoundAbilityCards.Count ?? -1, RulesReady(actor) ? 1 : 0,
                manager == null ? -1 : manager.GetInstanceID(), ui?.dialogPopup == null ? -1 : ui.dialogPopup.GetInstanceID(),
                focused != null && focused.IsPopUp && focused.IsOpen && focused.gameObject.activeInHierarchy ? focused.GetInstanceID() : -1,
                _capabilityReady && CardViewsReady(hand) ? 1 : 0) +
                HandDetail("target", hand) + HandDetail("current", manager?.CurrentHand) + HandDetail("blocking", blocker);
        }

        private static string HandDetail(string prefix, CardsHandUI hand, bool diagnostic = false)
        {
            ShortRest rest = hand == null ? null : RestButton?.GetValue(hand) as ShortRest;
            YesNoDialog confirmation = rest == null ? null : RestConfirmation?.GetValue(rest) as YesNoDialog;
            UIWindow window = confirmation == null ? null : ConfirmationWindow?.GetValue(confirmation) as UIWindow;
            string detail = string.Format(CultureInfo.InvariantCulture,
                ";{0}_hand_id={1};{0}_mode={2};{0}_rested={3};{0}_ui_rested={4};{0}_animation={5};{0}_ui_animation={6}" +
                ";{0}_draw={7};{0}_alternate_draw={8};{0}_draw_card={9};{0}_preview={10}",
                prefix, hand == null ? -1 : hand.GetInstanceID(), hand == null ? -1 : (int)hand.currentMode,
                hand?.PlayerActor?.CharacterClass == null ? -1 : hand.PlayerActor.CharacterClass.HasShortRested ? 1 : 0,
                hand == null || Rested == null ? -1 : Equals(Rested.GetValue(hand), true) ? 1 : 0,
                hand == null ? -1 : hand.AnimatingLostCards ? 1 : 0,
                hand == null || LosingCards == null ? -1 : Equals(LosingCards.GetValue(hand), true) ? 1 : 0,
                hand == null ? -1 : FirstDraw?.GetValue(hand) ?? -1, hand == null ? -1 : SecondDraw?.GetValue(hand) ?? -1,
                hand?.ShortRestedCard?.ID ?? -1, hand == null ? -1 : hand.IsPreviewingCards ? 1 : 0);
            if (diagnostic) return detail;
            return detail + string.Format(CultureInfo.InvariantCulture,
                ";{0}_visible={1};{0}_interactable={2};{0}_selected={3};{0}_rest_selected={4};{0}_rest_visible={5}" +
                ";{0}_rest_interactable={6};{0}_rest_actor_id={7};{0}_confirmation={8};{0}_showing={9}", prefix,
                hand != null && hand.isActiveAndEnabled && hand.gameObject.activeInHierarchy ? 1 : 0,
                hand == null ? -1 : hand.IsInteractable ? 1 : 0, hand?.SelectedCards?.Count ?? -1,
                rest == null ? -1 : rest.IsSelected ? 1 : 0,
                rest != null && rest.isActiveAndEnabled && rest.gameObject.activeInHierarchy ? 1 : 0,
                rest?.Button == null ? -1 : rest.Button.IsInteractable() ? 1 : 0, rest?.PlayerActor?.ID ?? -1,
                window != null && window.IsOpen && window.gameObject.activeInHierarchy ? 1 : 0,
                hand != null && CardsHandManager.Instance?.IsShowingPlayerHand(hand.PlayerActor) == true ? 1 : 0);
        }

        private static bool Prepare()
        {
            if (_focusChanged || _hand == null ||
                CardsHandManager.Instance != _manager || _manager.CurrentHand != _hand ||
                _manager.ActivePlayer != _owner || UIManager.Instance?.dialogPopup != _popup ||
                !ReferenceEquals(_scenario, ScenarioManager.CurrentScenarioState) ||
                ScenarioManager.CurrentScenarioState.RoundNumber != _round || _owner.Health != _health ||
                PhaseManager.PhaseType != CPhase.PhaseType.SelectAbilityCardsOrLongRest ||
                !_owner.CharacterClass.DiscardedAbilityCards.SequenceEqual(_discards))
            {
                // No random draw has occurred: release the preparation lock and let the caller hand off.
                CPlayerActor actor = _owner;
                Reset();
                Status = "manual";
                Block(actor, "rest_target_hand", transient: false);
                return false;
            }
            string reason = PreflightReason(_owner, prepared: true, out CardsHandUI blocker);
            if (reason == "rest_ready" && !AutomationController.IsAutomated(_owner)) reason = "rest_rules";
            if (reason != "rest_ready")
            {
                CPlayerActor actor = _owner;
                if (Time.realtimeSinceStartup >= _prepareDeadline || reason == "rest_rules" || reason == "rest_capability")
                {
                    Reset();
                    Status = "manual";
                }
                return Block(actor, reason, blocker);
            }
            LastBlockReason = "rest_ready";
            IsTransientBlock = false;
            _blockedActor = null;
            _reportedBlock = null;
            _preparing = false;
            InvokeUi(() => _hand.PerformShortRest(_owner));
            return true;
        }

        private static void Complete(bool restoreHand = true)
        {
            try
            {
                if (restoreHand && !_owner.IsDead && _owner.Health > 0 && !_focusChanged &&
                    !FFSNetwork.IsOnline && _previousHand != null &&
                    _previousHand.PlayerActor != null && !_previousHand.PlayerActor.IsDead &&
                    _previousHand.PlayerActor.Health > 0 && CardsHandManager.Instance == _manager &&
                    _manager.CurrentHand == _hand && _manager.ActivePlayer == _owner &&
                    _manager.IsShowingPlayerHand(_owner) && _hand.IsInteractable &&
                    _hand.currentMode == CardHandMode.CardsSelection &&
                    _manager.GetHand(_previousHand.PlayerActor) == _previousHand &&
                    _previousHand.currentMode == CardHandMode.CardsSelection &&
                    ReferenceEquals(_scenario, ScenarioManager.CurrentScenarioState) &&
                    ScenarioManager.CurrentScenarioState.RoundNumber == _round &&
                    PhaseManager.PhaseType == CPhase.PhaseType.SelectAbilityCardsOrLongRest && IdleUi(_manager))
                {
                    // Keep ownership during synchronous UI callbacks; status getters never switch hands.
                    _invoking = true;
                    _expectedSwitch = _previousHand.PlayerActor;
                    _manager.SwitchHand(_expectedSwitch);
                }
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogWarning("Short rest hand restoration skipped: " + exception.Message);
            }
            finally
            {
                Reset();
                Status = "completed";
            }
        }

        private static bool CanAct(CPlayerActor actor)
        {
            return RulesReady(actor) && AutomationController.IsAutomated(actor) &&
                !ScenarioRuleClient.IsProcessingOrMessagesQueued;
        }

        private static bool RulesReady(CPlayerActor actor)
        {
            return Plugin.AutomateShortRests?.Value == true && actor?.CharacterClass != null && !actor.IsDead && actor.Health > 0 &&
                !actor.IsTakingExtraTurn && !FFSNetwork.IsOnline && AutomationController.IsScenarioReady() &&
                ScenarioManager.CurrentScenarioState != null &&
                PhaseManager.PhaseType == CPhase.PhaseType.SelectAbilityCardsOrLongRest &&
                !actor.CharacterClass.ImprovedShortRest && !actor.CharacterClass.LongRest &&
                !actor.IsLongRestSelected && !actor.CharacterClass.HasShortRested &&
                actor.CharacterClass.RoundAbilityCards.Count == 0 &&
                actor.CharacterClass.DiscardedAbilityCards.Count >= 2 &&
                actor.CharacterClass.HandAbilityCards.Count + actor.CharacterClass.DiscardedAbilityCards.Count - 1 >= 2;
        }

        private static void InvokeUi(Action action)
        {
            _invoking = true;
            _shows = 0;
            _options = null;
            try
            {
                action();
                Status = _submitted ? "submitted" : _shows == 1 && MatchesPrompt() ?
                    (_redrawn ? "redrawn" : "started") : "manual";
                if (Status == "manual") Block(_owner, "rest_modal", transient: false);
                else
                {
                    LastBlockReason = "rest_ready";
                    IsTransientBlock = false;
                }
            }
            catch (Exception exception)
            {
                Status = "manual";
                Block(_owner, "rest_modal", transient: false);
                Plugin.Log?.LogWarning("Short rest left manual: " + exception.Message);
            }
            finally
            {
                _frame = Time.frameCount;
                _invoking = false;
            }
        }

        private static bool MatchesPrompt()
        {
            if (_focusChanged || _options == null || _popup == null || !_popup.IsOpen() ||
                UIManager.Instance?.dialogPopup != _popup || _generation != _ownedGeneration ||
                !ReferenceEquals(_scenario, ScenarioManager.CurrentScenarioState) ||
                ScenarioManager.CurrentScenarioState.RoundNumber != _round || _owner.Health != _health ||
                _hand == null || CardsHandManager.Instance?.CurrentHand != _hand ||
                CardsHandManager.Instance.ActivePlayer != _owner || _hand.PlayerActor != _owner ||
                _hand.currentMode != CardHandMode.CardsSelection || _hand.SelectedCards.Count != 0 ||
                !ReferenceEquals(_hand.ShortRestedCard, _drawn) ||
                !_owner.CharacterClass.DiscardedAbilityCards.SequenceEqual(_discards)) return false;
            IList contents = Content.GetValue(_popup) as IList;
            if (_content == null || contents?.Count != 1 || !ReferenceEquals(
                AccessTools.Field(contents[0].GetType(), "GameObject")?.GetValue(contents[0]), _content) ||
                _hand.GetCard(_displayed.ID)?.fullAbilityCard?.gameObject != _content) return false;
            var buttons = Buttons.GetValue(_popup) as List<InputButton>;
            if (buttons == null || buttons.Count < _options.Length ||
                buttons.Count(b => b != null && b.gameObject.activeSelf) != _options.Length)
                return false;
            for (int i = 0; i < _options.Length; i++)
            {
                string text = i == 0 ? string.Format(LocalizationManager.GetTranslation("GUI_LOSE_CARD"),
                    _hand.GetCard(_displayed.ID).fullAbilityCard.Title) : LocalizationManager.GetTranslation("GUI_REDRAW_CARD");
                KeyAction key = i == 1 ? KeyAction.UI_CANCEL : _options.Length == 2 ? KeyAction.UI_SUBMIT : KeyAction.CONFIRM_ACTION_BUTTON;
                if (buttons[i] == null || _buttons[i] == null || _buttons[i].buttonText == null ||
                    _options[i].text != text || _options[i].keyAction != key ||
                    !ReferenceEquals(_options[i].onMouseClickAction, _callbacks[i]) ||
                    buttons[i].ExtendedButton != _buttons[i] || _buttons[i].buttonText.text != text ||
                    !Listeners(_buttons[i]).SequenceEqual(_listeners[i])) return false;
            }
            return true;
        }

        private static object[] Listeners(ExtendedButton button)
        {
            if (button == null || button.onClick.GetPersistentEventCount() != 0) return new object[0];
            return (RuntimeCalls?.GetValue(EventCalls.GetValue(button.onClick)) as IList)?.Cast<object>().ToArray()
                ?? new object[0];
        }

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            return typeof(DialogPopup).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => method.Name == "Show" || method.Name == "Hide" || method.Name == "ShowLoadedContent")
                .Concat(new[] { AccessTools.Method(typeof(CardsHandManager), "SwitchHand", new[] { typeof(CPlayerActor) }) });
        }

        [HarmonyPrefix]
        private static void Prefix(object __instance, object[] __args, out long __state)
        {
            __state = _generation;
            if (__instance is DialogPopup) __state = ++_generation;
            else if (_owner != null)
            {
                if (!ReferenceEquals(__instance, _manager) || !ReferenceEquals(__args[0], _expectedSwitch))
                    _focusChanged = true;
                _expectedSwitch = null;
            }
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance, object[] __args, long __state)
        {
            if (!_invoking || _preparing || !ReferenceEquals(__instance, _popup) || __args.Length < 2 ||
                !(__args[0] is GameObject content) || !(__args[1] is DialogOption[] options)) return;
            _shows++;
            // GameObject Show delegates once to List<GameObject> Show. Any additional replacement fails closed.
            if (_shows != 1 || _generation != __state + 1 || options.Length != (_redrawn || _health <= 1 ? 1 : 2)) return;
            try
            {
                _drawn = _hand.ShortRestedCard;
                _displayed = _discards.SingleOrDefault(card => _hand.GetCard(card.ID)?.fullAbilityCard?.gameObject == content);
                // Redraw stores _shortRestedCard BEFORE the tutorial override; track both identities.
                if (_drawn == null || _displayed == null || (!_redrawn && _drawn != _displayed)) return;
                var buttons = Buttons.GetValue(_popup) as List<InputButton>;
                if (buttons == null || buttons.Count < options.Length || options.Any(o => o?.onMouseClickAction == null)) return;
                _content = content;
                _callbacks = options.Select(o => o.onMouseClickAction).ToArray();
                _buttons = buttons.Take(options.Length).Select(b => b.ExtendedButton).ToArray();
                _listeners = _buttons.Select(Listeners).ToArray();
                if (_listeners.Any(list => list.Length != 1)) return;
                _ownedGeneration = _generation;
                _options = options.ToArray();
            }
            catch (Exception)
            {
                _options = null;
            }
        }
    }
}
