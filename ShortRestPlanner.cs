using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using GLOOM;
using HarmonyLib;
using ScenarioRuleLibrary;
using Script.GUI.Popups;
using UnityEngine;
using UnityEngine.Events;

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

        internal static string Status { get; private set; } = "idle";
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
        }

        // True means an attempt was claimed, possibly still preparing; do not call TryStart again.
        // The controller must exclude ALL other party decisions while IsPendingAny is true.
        internal static bool TryStart(CPlayerActor actor)
        {
            if (ScenarioRuleClient.s_MainThread != Thread.CurrentThread || IsPendingAny) return false;
            Status = "not_ready";
            if (!CanAct(actor) || Buttons == null || Content == null || RestButton == null ||
                Rested == null || LosingCards == null || FirstDraw == null || SecondDraw == null || RuntimeCalls == null ||
                Previewing == null || DeckPreview == null ||
                !TargetMethods().All(method => Harmony.GetPatchInfo(method)?.Prefixes.Any(p =>
                    p.PatchMethod.DeclaringType == typeof(ShortRestPlanner)) == true &&
                    Harmony.GetPatchInfo(method)?.Postfixes.Any(p =>
                    p.PatchMethod.DeclaringType == typeof(ShortRestPlanner)) == true)) return false;
            CardsHandManager manager = CardsHandManager.Instance;
            CardsHandUI hand = manager?.GetHand(actor);
            DialogPopup popup = UIManager.Instance?.dialogPopup;
            if (hand == null || hand.PlayerActor != actor || popup == null || !IdleUi(manager) ||
                manager.CurrentHand == null || manager.ActivePlayer != manager.CurrentHand.PlayerActor ||
                !manager.IsShowingPlayerHand(manager.ActivePlayer) || !manager.CurrentHand.IsInteractable ||
                manager.CurrentHand.currentMode != CardHandMode.CardsSelection || !HandReady(hand)) return false;
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
            try
            {
                if (_previousHand != null)
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
                return false;
            }
            if (_submitted || !CanAct(actor)) return false;
            if (!MatchesPrompt())
            {
                Status = "manual";
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

        private static bool HandReady(CardsHandUI hand)
        {
            ShortRest rest = RestButton.GetValue(hand) as ShortRest;
            return hand.currentMode == CardHandMode.CardsSelection && hand.SelectedCards.Count == 0 &&
                hand.ShortRestedCard == null && !AnimationInProgress(hand) &&
                Equals(Rested.GetValue(hand), false) && Equals(FirstDraw.GetValue(hand), -1) &&
                Equals(SecondDraw.GetValue(hand), -1) && rest != null && rest.PlayerActor == hand.PlayerActor &&
                rest.Button != null && !rest.IsSelected && hand.GetCard(-1) != null && !hand.GetCard(-1).IsSelected &&
                hand.PlayerActor.CharacterClass.DiscardedAbilityCards.All(card =>
                    card != null && hand.GetCard(card.ID)?.fullAbilityCard != null);
        }

        private static bool IdleUi(CardsHandManager manager)
        {
            return manager != null && UIManager.Instance?.dialogPopup != null &&
                !UIManager.Instance.dialogPopup.IsOpen() && Equals(Previewing.GetValue(manager), false) &&
                DeckPreview.GetValue(manager) is CardsHandPreviewWindow preview && !preview.IsOpen &&
                !manager.IsFullCardPreviewShowing && manager.CardHandsUI.All(hand => hand != null &&
                    !hand.IsPreviewingCards && !AnimationInProgress(hand) && hand.ShortRestedCard == null &&
                    !((RestButton.GetValue(hand) as ShortRest)?.IsSelected ?? false));
        }

        private static bool Prepare()
        {
            if (Time.realtimeSinceStartup >= _prepareDeadline || _focusChanged || _hand == null ||
                CardsHandManager.Instance != _manager || _manager.CurrentHand != _hand ||
                _manager.ActivePlayer != _owner || UIManager.Instance?.dialogPopup != _popup ||
                !ReferenceEquals(_scenario, ScenarioManager.CurrentScenarioState) ||
                ScenarioManager.CurrentScenarioState.RoundNumber != _round || _owner.Health != _health ||
                PhaseManager.PhaseType != CPhase.PhaseType.SelectAbilityCardsOrLongRest ||
                !_owner.CharacterClass.DiscardedAbilityCards.SequenceEqual(_discards) || !HandReady(_hand))
            {
                // No random draw has occurred: release the preparation lock and let the caller hand off.
                Reset();
                Status = "manual";
                return false;
            }
            if (!CanAct(_owner) || !IdleUi(_manager) || !_manager.IsShowingPlayerHand(_owner) ||
                !_hand.IsInteractable) return false;
            ShortRest rest = (ShortRest)RestButton.GetValue(_hand);
            if (!rest.gameObject.activeInHierarchy || !rest.Button.IsInteractable()) return false;
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
            return Plugin.AutomateShortRests.Value && actor?.CharacterClass != null && !actor.IsDead && actor.Health > 0 &&
                !actor.IsTakingExtraTurn && !FFSNetwork.IsOnline && AutomationController.IsAutomated(actor) &&
                AutomationController.IsScenarioReady() && !ScenarioRuleClient.IsProcessingOrMessagesQueued &&
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
            }
            catch (Exception exception)
            {
                Status = "manual";
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
