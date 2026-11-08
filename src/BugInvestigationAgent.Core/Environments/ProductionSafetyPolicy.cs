using System.Text.RegularExpressions;
using BugInvestigationAgent.Core.Context;

namespace BugInvestigationAgent.Core.Environments;

/// <summary>
/// Read-only guard for Production (and other read-only) environments.
/// Steps that may create, modify, submit or delete data are skipped unless the user explicitly confirmed them.
/// The classification is conservative: when in doubt a step is treated as state-changing.
/// </summary>
public static class ProductionSafetyPolicy
{
    // Observation-only phrasing wins even if a verb below appears ("Observe that Save shows an error").
    private static readonly Regex ObservationOnly = new(
        @"^\s*(observe|verify|check|confirm that|review|look|wait|note|see|expect|notice|inspect|scroll)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StateChanging = new(
        @"\b(create|add|save|submit|delete|remove|promote|approve|reject|send|publish|import|upload|assign|unassign|" +
        @"close (the )?(incident|record|ticket|case|audit)|complete|sign off|deploy|archive|restore|update|edit|change|enable|disable|" +
        @"reset|confirm|execute|run|start|stop|cancel|transfer|pay|purchase|order|book|register|invite|grant|revoke|" +
        @"set response type|response type\s*=|leave required|apply|post|finali[sz]e|lock|unlock|merge|move|copy|duplicate|clone)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsPotentiallyStateChanging(string? step)
    {
        if (string.IsNullOrWhiteSpace(step))
            return false;
        if (ObservationOnly.IsMatch(step))
            return false;
        return StateChanging.IsMatch(step);
    }

    /// <summary>True when the step must be skipped on this context's environment.</summary>
    public static bool MustSkip(RuntimeContext context, string? step) =>
        context.IsReadOnlyEnvironment
        && !context.StateChangingActionsConfirmed
        && IsPotentiallyStateChanging(step);
}
