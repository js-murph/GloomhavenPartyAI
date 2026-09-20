using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HarmonyLib;
using ScenarioRuleLibrary;
using UnityEngine.UI;

namespace GloomhavenPartyAI
{
    internal static class AutomationPatches
    {
        [HarmonyPatch(typeof(Choreographer), "ProcessMessage")]
        [HarmonyPrefix]
        private static void ObserveMessagePrefix(CMessageData message)
        {
            // Capture error notifications before the original handler opens its blocking dialog.
            if (ScenarioRuleClient.s_MainThread == Thread.CurrentThread)
            {
                AutomationController.ObserveFault(message);
                DeveloperDiagnostics.ObserveMessage(message);
            }
        }

        [HarmonyPatch(typeof(Choreographer), nameof(Choreographer.LogScenarioResult))]
        [HarmonyPrefix]
        private static void ScenarioResultPrefix(SEventActorFinishedScenario.EScenarioResult result)
        {
            if (ScenarioRuleClient.s_MainThread == Thread.CurrentThread)
                DeveloperDiagnostics.ObserveScenarioResult(result);
        }

        [HarmonyPatch(typeof(Choreographer), "ProcessMessage")]
        [HarmonyPostfix]
        private static void ProcessMessagePostfix(Choreographer __instance, CMessageData message)
        {
            if (ScenarioRuleClient.s_MainThread != Thread.CurrentThread || message == null ||
                !AutomationController.IsScenarioReady())
            {
                return;
            }
            try
            {
                AutomationController.HandleMessage(message);
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError("Party AI message handling failed: " + exception);
            }
        }

        [HarmonyPatch(typeof(GameState), nameof(GameState.Stop))]
        [HarmonyPrefix]
        private static void StopPrefix()
        {
            AutomationController.Reset();
        }

        [HarmonyPatch(typeof(ScenarioRuleClient), nameof(ScenarioRuleClient.Start))]
        [HarmonyPrefix]
        private static void ScenarioStartPrefix()
        {
            DeveloperDiagnostics.EndSession("scenario_start");
            AutomationController.Reset();
        }

        [HarmonyPatch(typeof(ScenarioRuleClient), nameof(ScenarioRuleClient.Stop), new Type[] { })]
        [HarmonyPrefix]
        private static void ScenarioClientStopPrefix()
        {
            AutomationController.Reset();
        }

        [HarmonyPatch(typeof(InitiativeTrackPlayerBehaviour), nameof(InitiativeTrackPlayerBehaviour.RefreshPlayerController))]
        [HarmonyPostfix]
        private static void RefreshPlayerControllerPostfix(InitiativeTrackPlayerBehaviour __instance)
        {
            AutomationToggleUi.Refresh(__instance);
        }

        [HarmonyPatch(typeof(InitiativeTrackActorBehaviour), nameof(InitiativeTrackActorBehaviour.EnableNavigation))]
        [HarmonyPostfix]
        private static void EnableNavigationPostfix(InitiativeTrackActorBehaviour __instance,
            Selectable left, Selectable right)
        {
            AutomationToggleUi.EnableNavigation(__instance, left, right);
        }

        [HarmonyPatch(typeof(InitiativeTrackActorBehaviour), nameof(InitiativeTrackActorBehaviour.DisableNavigation))]
        [HarmonyPostfix]
        private static void DisableNavigationPostfix(InitiativeTrackActorBehaviour __instance)
        {
            AutomationToggleUi.DisableNavigation(__instance);
        }

        [HarmonyPatch(typeof(CAbilityAttack), nameof(CAbilityAttack.Perform))]
        [HarmonyPrefix]
        private static bool AttackPerformPrefix(CAbilityAttack __instance, ref bool __result)
        {
            if (!TacticalPlanner.IsSupportedAttack(__instance) ||
                __instance.State != CAbilityAttack.EAttackState.PreActorIsAttacking ||
                !AutomationController.ConsumeCommittedAttack(__instance))
            {
                return true;
            }

            if (__instance.ActorsToTarget.Count > 0)
            {
                return true;
            }

            List<CActor> valid = TacticalPlanner.RankAttackTargets(__instance.TargetingActor,
                __instance, __instance.ValidActorsInRange);
            if (valid.Count == 0)
                valid = __instance.ValidActorsInRange.Where(actor => actor != null && !actor.IsDead).Take(1).ToList();

            if (valid.Count == 0)
            {
                AutomationController.Decision(__instance.TargetingActor.Class.ID + " has no target and skips its attack.");
                PhaseManager.NextStep();
                __result = true;
                return false;
            }

            int targetCount = __instance.AllTargets
                ? valid.Count
                : Math.Max(1, Math.Min(__instance.NumberTargetsRemaining, valid.Count));
            __instance.TilesSelected.Clear();
            foreach (CActor target in valid.Take(targetCount))
            {
                __instance.ActorsToTarget.Add(target);
                __instance.TilesSelected.Add(ScenarioManager.Tiles[target.ArrayIndex.X, target.ArrayIndex.Y]);
            }

            AutomationController.Decision(__instance.TargetingActor.Class.ID + " attacks " +
                string.Join(", ", __instance.ActorsToTarget.Select(a => a.Class.ID).ToArray()) + ".");
            return true;
        }

    }
}
