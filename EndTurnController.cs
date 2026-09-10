using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using ScenarioRuleLibrary;
using UnityEngine.UI;

namespace GloomhavenPartyAI
{
    // Patch this type separately. Entry points and UI callbacks run on the scenario main thread.
    [HarmonyPatch]
    internal static class EndTurnController
    {
        private sealed class Request
        {
            internal CPhase Phase;
            internal CPlayerActor Actor;
            internal ReadyButton Button;
            internal Action Callback;
            internal bool ClickEntered, CallbackStarted, PassIssued, Failed, SubmissionRejected;

            internal bool BlocksDuplicates => !Failed || (CallbackStarted && !SubmissionRejected);

            internal void Invoke()
            {
                // Never acquire a new phase token from a delayed callback, even for the same actor.
                if (!ReferenceEquals(CurrentRequest(), this) || CallbackStarted || Failed ||
                    FFSNetwork.IsOnline || !AutomationController.IsScenarioReady() ||
                    !ReferenceEquals(GameState.InternalCurrentActor, Actor)) return;
                CallbackStarted = true;
                Request previous = _executing;
                _executing = this;
                try
                {
                    Callback();
                }
                catch
                {
                    Failed = true;
                    throw;
                }
                finally
                {
                    _executing = previous;
                }
            }
        }

        private static readonly FieldInfo ButtonState = typeof(ReadyButton).GetField("buttonState", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo WarningMask = typeof(ReadyButton).GetField("warningMask", BindingFlags.Instance | BindingFlags.NonPublic);
        private static Request _pending, _clickRequest, _executing;
        private static bool _callbackPatchReady;

        // Scenario lifecycle only, not an automation toggle. Invalidates already-captured callbacks too.
        internal static void Reset()
        {
            _pending = null;
            _clickRequest = null;
            _executing = null;
        }

        private static Request CurrentRequest()
        {
            if (_pending != null && !ReferenceEquals(_pending.Phase, PhaseManager.CurrentPhase))
                _pending = null;
            return _pending;
        }

        internal static bool IsPending(CPlayerActor actor)
        {
            Request request = CurrentRequest();
            return request != null && !request.Failed && ReferenceEquals(request.Actor, actor);
        }

        internal static bool HasFailed(CPlayerActor actor)
        {
            Request request = CurrentRequest();
            return request != null && request.Failed && ReferenceEquals(request.Actor, actor);
        }

        // Call on an acceptance timeout before handing off. Do not retry an uncertain submission.
        // Invalidate late callbacks. Keep duplicate protection if a callback may already have submitted;
        // otherwise manual input is safe, but automation still sees HasFailed until the phase changes.
        internal static void MarkFailed(CPlayerActor actor)
        {
            Request request = CurrentRequest();
            if (request != null && ReferenceEquals(request.Actor, actor)) request.Failed = true;
        }

        // True means the UI accepted one request, not that its delayed callback or rule handshake finished.
        // The caller owns automation policy and retries false only while this exact prompt is still live.
        internal static bool TryEndTurn(CPlayerActor actor)
        {
            if (ScenarioRuleClient.s_MainThread != Thread.CurrentThread || FFSNetwork.IsOnline ||
                !_callbackPatchReady || CurrentRequest() != null || actor == null || actor.IsDead ||
                !ReferenceEquals(GameState.InternalCurrentActor, actor) ||
                !(PhaseManager.CurrentPhase is CPhaseActionSelection) ||
                ScenarioRuleClient.ScenarioRuleClientStopped || ScenarioRuleClient.IsProcessingOrMessagesQueued ||
                GameState.WaitingForMercenarySpecialMechanicSlotChoice ||
                GameState.PendingOnLongRestBonuses.Count != 0) return false;

            CCharacterClass character = actor.CharacterClass;
            if (character == null || character.LongRest ||
                (GameState.CurrentActionSelectionSequence != GameState.ActionSelectionSequenceType.Complete &&
                 !(character.HasLongRested && !actor.IsTakingExtraTurn))) return false;

            Choreographer choreographer = Choreographer.s_Choreographer;
            ReadyButton button = choreographer == null ? null : choreographer.readyButton;
            UIConfirmationBoxManager confirmation = Singleton<UIConfirmationBoxManager>.Instance;
            if (button == null || ButtonState == null || WarningMask == null ||
                !ReferenceEquals(choreographer.m_CurrentActor, actor) || choreographer.IsRestarting ||
                choreographer.m_MessageQueue.Count != 0 || !choreographer.ThisPlayerHasTurnControl ||
                choreographer.LastMessage == null ||
                choreographer.LastMessage.m_Type == CMessageData.MessageType.EndAbilityAnimSync ||
                SceneController.Instance?.GlobalErrorMessage?.ShowingMessage != false ||
                FFSNetwork.IsStartingUp || FFSNetwork.IsShuttingDown || confirmation == null ||
                confirmation.IsRequested || confirmation.IsOpen || !IsEndTurnButton(button) ||
                !button.isActiveAndEnabled || !button.IsVisibility || button.ButtonComponent == null ||
                !button.ButtonComponent.isActiveAndEnabled || !button.ButtonComponent.IsInteractable() ||
                !(WarningMask.GetValue(button) is Button warning) || warning.gameObject.activeSelf) return false;

            var request = new Request { Actor = actor, Phase = PhaseManager.CurrentPhase, Button = button };
            _pending = request;
            _clickRequest = request;
            try
            {
                // Preserve queue consumption, selection cleanup, effects, and the game's own Pass callback.
                button.OnClickInternal(false);
                if (!request.ClickEntered || request.Callback == null)
                {
                    request.Failed = true;
                    if (ReferenceEquals(_pending, request)) _pending = null;
                    return false;
                }
                return !request.Failed;
            }
            catch
            {
                request.Failed = true;
                throw;
            }
            finally
            {
                _clickRequest = null;
            }
        }

        private static bool IsEndTurnButton(ReadyButton button)
        {
            return ButtonState != null &&
                (ReadyButton.EButtonState)ButtonState.GetValue(button) == ReadyButton.EButtonState.EREADYBUTTONENDTURN;
        }

        [HarmonyPatch(typeof(ReadyButton), nameof(ReadyButton.OnClickInternal))]
        [HarmonyPrefix]
        private static bool ReadyClickPrefix(ReadyButton __instance, out Request __state)
        {
            __state = _clickRequest;
            _clickRequest = null;
            if (FFSNetwork.IsOnline || !IsEndTurnButton(__instance)) return true;
            // EPASS here skips the phase which alone accepts EENDTURNSYNCHRONISE. No click reason is needed.
            if (PhaseManager.CurrentPhase is CPhaseEndTurn) return false;
            Request request = CurrentRequest();
            if (request == null || !request.BlocksDuplicates || !ReferenceEquals(request.Button, __instance)) return true;
            if (!ReferenceEquals(__state, request) || request.ClickEntered || request.Failed) return false;
            UIConfirmationBoxManager confirmation = Singleton<UIConfirmationBoxManager>.Instance;
            if (confirmation == null || confirmation.IsRequested || confirmation.IsOpen) return false;
            request.ClickEntered = true;
            _clickRequest = request;
            return true;
        }

        [HarmonyPatch(typeof(ReadyButton), nameof(ReadyButton.OnClickInternal))]
        [HarmonyFinalizer]
        private static void ReadyClickFinalizer(Request __state)
        {
            // Nested, unrelated confirmations must not inherit the outer end-turn callback capture.
            _clickRequest = __state;
        }

        [HarmonyPatch(typeof(Choreographer), nameof(Choreographer.Pass))]
        [HarmonyPrefix]
        private static bool PassPrefix()
        {
            if (FFSNetwork.IsOnline) return true;
            if (PhaseManager.CurrentPhase is CPhaseEndTurn) return false;
            Request request = CurrentRequest();
            if (request == null || !request.BlocksDuplicates) return true;
            if (!ReferenceEquals(_executing, request) || request.PassIssued || request.Failed) return false;
            request.PassIssued = true;
            return true;
        }

        [HarmonyPatch(typeof(ScenarioRuleClient), nameof(ScenarioRuleClient.Pass))]
        [HarmonyPostfix]
        private static void RulePassPostfix(uint __result)
        {
            if (_executing != null && __result == 0)
            {
                _executing.Failed = true;
                _executing.SubmissionRejected = true;
            }
        }

        private static Action CaptureCallback(Action callback, ReadyButton button)
        {
            Request request = _clickRequest;
            if (callback == null || request == null || !request.ClickEntered ||
                !ReferenceEquals(request.Button, button)) return callback;
            request.Callback = callback;
            return request.Invoke;
        }

        [HarmonyPatch(typeof(ReadyButton), nameof(ReadyButton.OnClickInternal))]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> CaptureEndTurnCallback(IEnumerable<CodeInstruction> instructions)
        {
            _callbackPatchReady = false;
            var result = new List<CodeInstruction>();
            int captures = 0;
            MethodInfo capture = typeof(EndTurnController).GetMethod(nameof(CaptureCallback), BindingFlags.Static | BindingFlags.NonPublic);
            foreach (CodeInstruction instruction in instructions)
            {
                // actionDelayed is captured by the game's effect closure. Bind it when assigned,
                // not in HideDelayed, where the original phase may already have been replaced.
                if (instruction.opcode == OpCodes.Stfld && instruction.operand is FieldInfo field &&
                    field.Name == "actionDelayed" && field.FieldType == typeof(Action) &&
                    field.DeclaringType.DeclaringType == typeof(ReadyButton))
                {
                    var loadButton = new CodeInstruction(OpCodes.Ldarg_0);
                    loadButton.MoveLabelsFrom(instruction);
                    loadButton.MoveBlocksFrom(instruction);
                    result.Add(loadButton);
                    result.Add(new CodeInstruction(OpCodes.Call, capture));
                    captures++;
                }
                result.Add(instruction);
            }
            // Fail patch registration on an incompatible game build rather than allow unbound callbacks.
            if (captures != 4) throw new InvalidOperationException("Unexpected ReadyButton end-turn callback layout: " + captures);
            _callbackPatchReady = true;
            return result;
        }
    }
}
