using ScenarioRuleLibrary;

namespace GloomhavenPartyAI;

// The real planner is linked unchanged. Only its logging sink is replaced; no engine policy is stubbed.
internal static class DeveloperDiagnostics
{
    internal static bool Enabled => false;

    internal static void Record(string kind, CActor actor, string detail) =>
        throw new InvalidOperationException("Diagnostics must remain disabled in isolated reference tests.");
}
