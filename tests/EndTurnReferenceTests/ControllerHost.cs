namespace GloomhavenPartyAI;

// The reference runner tests callback gating without loading the live automation/UI controller.
internal static class AutomationController
{
    internal static bool Ready = true;
    internal static bool IsScenarioReady() => Ready;
}
