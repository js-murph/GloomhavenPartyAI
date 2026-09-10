using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ScenarioRuleLibrary;
using UnityEngine;

namespace GloomhavenPartyAI
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.jsm.gloomhaven.partyai";
        public const string Name = "Gloomhaven Party AI";
        public const string Version = "0.5.1";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> ModEnabled;
        internal static ConfigEntry<string> HumanCharacter;
        internal static ConfigEntry<bool> AutomateDamage;
        internal static ConfigEntry<bool> PreventLethalDamage;
        internal static ConfigEntry<float> DecisionDelay;
        internal static ConfigEntry<bool> LogDecisions;
        internal static ConfigEntry<bool> AutomateItems;
        internal static ConfigEntry<bool> AutomateShortRests;
        internal static ConfigEntry<bool> DeveloperMode;

        private Harmony _harmony;
        private float _nextPoll;

        private void Awake()
        {
            Log = Logger;
            ModEnabled = Config.Bind("General", "Enabled", true,
                "Enable party automation. Automation is always disabled in online games.");
            HumanCharacter = Config.Bind("Party", "HumanCharacter", string.Empty,
                "Character name or class ID to control manually. If empty, the first mercenary seen in the scenario is used.");
            AutomateDamage = Config.Bind("Decisions", "AutomateDamage", true,
                "Automatically resolve damage prompts for automated mercenaries.");
            PreventLethalDamage = Config.Bind("Decisions", "PreventLethalDamage", true,
                "Lose cards to prevent lethal damage when possible.");
            DecisionDelay = Config.Bind("General", "DecisionDelaySeconds", 0.15f,
                "Small real-time delay before automated UI decisions.");
            LogDecisions = Config.Bind("General", "LogDecisions", true,
                "Write card, rest, action, targeting, and damage decisions to the BepInEx log.");
            AutomateItems = Config.Bind("Decisions", "AutomateItems", true,
                "Evaluate supported healing, movement, and attack items for automated mercenaries. Complex item choices remain manual.");
            AutomateShortRests = Config.Bind("Decisions", "AutomateShortRests", true,
                "Use normal random-loss short rests when an automated mercenary needs cards under threat. Improved short rests remain manual.");
            DeveloperMode = Config.Bind("Diagnostics", "DeveloperMode", false,
                "Write offline diagnostic JSONL files under BepInEx/PartyAI/diagnostics (up to five 5 MiB files). No uploads. Does not enable automation.");

            AutomationController.Attach(this);
            DeveloperDiagnostics.Initialize(DeveloperMode, AutomationController.AutomationState);
            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(AutomationPatches));
            _harmony.PatchAll(typeof(ShortRestPlanner));
            _harmony.PatchAll(typeof(EndTurnController));
            Logger.LogInfo(Name + " v" + Version + " loaded (offline-only tactical mode).");
        }

        private void Update()
        {
            if (Time.realtimeSinceStartup < _nextPoll) return;
            _nextPoll = Time.realtimeSinceStartup + 0.5f;
            DeveloperDiagnostics.ObserveState();
            var result = ScenarioManager.Scenario?.CurrentScenarioResult;
            if (result == SEventActorFinishedScenario.EScenarioResult.Win ||
                result == SEventActorFinishedScenario.EScenarioResult.Lose ||
                result == SEventActorFinishedScenario.EScenarioResult.Resign)
                DeveloperDiagnostics.EndSession("scenario_result");
            AutomationController.Reconcile();
            AutomationToggleUi.RefreshAll();
        }

        private void OnDestroy()
        {
            DeveloperDiagnostics.Shutdown();
            AutomationController.Reset();
            _harmony?.UnpatchSelf();
        }
    }
}
