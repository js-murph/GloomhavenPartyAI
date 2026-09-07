using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace GloomhavenPartyAI
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.jsm.gloomhaven.partyai";
        public const string Name = "Gloomhaven Party AI";
        public const string Version = "0.3.2";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> ModEnabled;
        internal static ConfigEntry<string> HumanCharacter;
        internal static ConfigEntry<bool> AutomateDamage;
        internal static ConfigEntry<bool> PreventLethalDamage;
        internal static ConfigEntry<float> DecisionDelay;
        internal static ConfigEntry<bool> LogDecisions;

        private Harmony _harmony;

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

            AutomationController.Attach(this);
            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(AutomationPatches));
            Logger.LogInfo(Name + " v" + Version + " loaded (offline-only tactical mode).");
        }

        private void OnDestroy()
        {
            AutomationController.Reset();
            _harmony?.UnpatchSelf();
        }
    }
}
