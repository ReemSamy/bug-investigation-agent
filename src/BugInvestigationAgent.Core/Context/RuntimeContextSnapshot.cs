using System.Text.Json;
using BugInvestigationAgent.Core.Authentication;
using BugInvestigationAgent.Core.Workflow;

namespace BugInvestigationAgent.Core.Context;

/// <summary>
/// Checkpoint of an investigation's runtime context, used to suspend a job while it waits for user input (releasing
/// the worker) and resume it later on any worker. The context never holds secrets (credentials/tokens live only in
/// the encrypted job secret store), so the checkpoint is secret-free by construction.
/// Browser state is NOT checkpointed: a resumed job always signs in again in a fresh, isolated browser.
/// </summary>
public static class RuntimeContextSnapshot
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Serialize(RuntimeContext context) => JsonSerializer.Serialize(context, Options);

    public static RuntimeContext Restore(string json, Guid investigationId)
    {
        var ctx = JsonSerializer.Deserialize<RuntimeContext>(json, Options)
                  ?? throw new InvalidOperationException("Invalid investigation checkpoint.");
        ctx.SessionId = investigationId;

        // Re-establish thread-safe collections and case-insensitive custom fields.
        ctx.StatusLog = Wrap(ctx.StatusLog);
        ctx.Evidence = Wrap(ctx.Evidence);
        ctx.BlockedBrowserRequests = Wrap(ctx.BlockedBrowserRequests);
        ctx.CustomFields = new Dictionary<string, string>(ctx.CustomFields, StringComparer.OrdinalIgnoreCase);

        // The browser is gone after a suspension: any in-flight or interactive sign-in must start over.
        if (ctx.AuthenticationStatus is AuthenticationStatus.InProgress or AuthenticationStatus.MfaRequired
            or AuthenticationStatus.ManualInterventionRequired or AuthenticationStatus.Succeeded)
        {
            ctx.AuthenticationStatus = AuthenticationStatus.NotStarted;
            ctx.AuthenticationMessage = null;
        }
        ctx.AuthenticationRecheckRequested = false;
        ctx.UserConfirmedReadyToReproduce = false;
        if (ctx.ReproductionOutcome is ReproductionOutcome.Blocked)
            ctx.ReproductionOutcome = ReproductionOutcome.NotStarted;
        return ctx;
    }

    private static SynchronizedList<T> Wrap<T>(IList<T>? items)
    {
        var list = new SynchronizedList<T>();
        foreach (var i in items ?? Array.Empty<T>())
            list.Add(i);
        return list;
    }
}
