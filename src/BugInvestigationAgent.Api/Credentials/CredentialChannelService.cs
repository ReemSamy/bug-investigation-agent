using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Core.Jobs;
using BugInvestigationAgent.Infrastructure.Forge;
using BugInvestigationAgent.Infrastructure.Hosting;

namespace BugInvestigationAgent.Api.Credentials;

public sealed class PendingInputItem
{
    public required Guid JobId { get; init; }
    public required string IssueKey { get; init; }
    public required string Kind { get; init; }
    public string? EnvironmentId { get; init; }
    public string? EnvironmentName { get; init; }
    public string? Prompt { get; init; }
    /// <summary>One-time ticket (10 minutes) authorizing ONE direct submission for this job by this user.</summary>
    public required string Ticket { get; init; }
}

public sealed class CredentialSubmission
{
    public string? EnvironmentId { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool Remember { get; set; }
}

public sealed class MfaSubmission
{
    public string? Code { get; set; }
}

public enum SubmissionResult { Accepted, Unauthorized, NotAwaiting, Invalid }

/// <summary>
/// Credentials and MFA codes never travel through Rovo chat or Forge functions. The Forge personal settings page
/// (rendered for the signed-in user) asks this service — via a FIT-authenticated call — for the caller's pending
/// requests and receives one-time, job-bound tickets. The page then posts the secret DIRECTLY from the user's
/// browser to the backend over TLS with that ticket. Tickets are stored only as SHA-256 hashes, expire after
/// 10 minutes and are single-use.
/// </summary>
public sealed class CredentialChannelService
{
    private static readonly TimeSpan TicketTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CredentialTtl = TimeSpan.FromMinutes(30);

    private readonly IInvestigationJobStore _jobs;
    private readonly IJobSecretStore _secrets;
    private readonly IAuditLog _audit;
    private readonly IEnvironmentSelectionService _environments;

    public CredentialChannelService(IInvestigationJobStore jobs, IJobSecretStore secrets, IAuditLog audit, IEnvironmentSelectionService environments)
    {
        _jobs = jobs;
        _secrets = secrets;
        _audit = audit;
        _environments = environments;
    }

    public async Task<IReadOnlyList<PendingInputItem>> ListPendingAsync(ForgeInvocation fit, CancellationToken ct)
    {
        if (fit.PrincipalAccountId is null)
            return Array.Empty<PendingInputItem>();

        var items = new List<PendingInputItem>();
        foreach (var job in await _jobs.ListAwaitingInputForAccountAsync(fit.InstallationId, fit.PrincipalAccountId, ct))
        {
            if (job.AwaitingInputKind is not (AwaitingInputKinds.Credentials or AwaitingInputKinds.MfaCode))
                continue;
            var envId = job.EnvironmentId;
            var ticket = Base64Url(RandomNumberGenerator.GetBytes(32));
            await _secrets.SetAsync(job.Id, JobSecretKinds.SubmissionTicket, Hash(ticket), DateTimeOffset.UtcNow.Add(TicketTtl), ct);
            items.Add(new PendingInputItem
            {
                JobId = job.Id,
                IssueKey = job.IssueKey,
                Kind = job.AwaitingInputKind!,
                EnvironmentId = envId,
                EnvironmentName = envId is null ? null : _environments.Registry.Get(envId)?.DisplayName ?? envId,
                Prompt = job.AwaitingInputPrompt,
                Ticket = ticket
            });
        }
        return items;
    }

    public async Task<SubmissionResult> SubmitCredentialsAsync(Guid jobId, string? ticket, CredentialSubmission body, CancellationToken ct)
    {
        var job = await AuthorizeAsync(jobId, ticket, AwaitingInputKinds.Credentials, ct);
        if (job.Result != SubmissionResult.Accepted)
            return job.Result;

        var envId = job.Job!.EnvironmentId;
        if (string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrEmpty(body.Password) || envId is null ||
            !string.Equals(body.EnvironmentId, envId, StringComparison.OrdinalIgnoreCase))
            return SubmissionResult.Invalid; // credentials are bound to the environment the job is waiting for

        var payload = JsonSerializer.Serialize(new SubmittedCredentials
        {
            Username = body.Username.Trim(), Password = body.Password, Remember = body.Remember
        });
        await _secrets.SetAsync(jobId, JobSecretKinds.EnvironmentCredentials(envId), payload, DateTimeOffset.UtcNow.Add(CredentialTtl), ct);
        body.Password = null;
        await _jobs.UpdateAsync(jobId, j => j.StatusMessage = $"Credentials for {envId} received securely.", ct);
        await _audit.WriteAsync(new AuditEntry
        {
            TenantId = job.Job.TenantId, AccountId = job.Job.InvokingAccountId, JobId = jobId,
            Action = AuditActions.CredentialsSubmitted,
            Detail = $"Credentials for {envId} submitted via the secure settings page{(body.Remember ? " (remember requested)" : "")}."
        }, ct);
        return SubmissionResult.Accepted;
    }

    public async Task<SubmissionResult> SubmitMfaCodeAsync(Guid jobId, string? ticket, MfaSubmission body, CancellationToken ct)
    {
        var job = await AuthorizeAsync(jobId, ticket, AwaitingInputKinds.MfaCode, ct);
        if (job.Result != SubmissionResult.Accepted)
            return job.Result;
        var code = body.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 12)
            return SubmissionResult.Invalid;

        await _secrets.SetAsync(jobId, JobSecretKinds.MfaCode, code, DateTimeOffset.UtcNow.AddMinutes(5), ct);
        await _audit.WriteAsync(new AuditEntry
        {
            TenantId = job.Job!.TenantId, AccountId = job.Job.InvokingAccountId, JobId = jobId,
            Action = AuditActions.AuthenticationAttempt, Detail = "Verification code submitted via the secure settings page."
        }, ct);
        return SubmissionResult.Accepted;
    }

    private async Task<(SubmissionResult Result, InvestigationJob? Job)> AuthorizeAsync(Guid jobId, string? ticket, string kind, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            return (SubmissionResult.Unauthorized, null);

        // Single use: the stored hash is consumed whether or not it matches.
        var expected = await _secrets.TakeAsync(jobId, JobSecretKinds.SubmissionTicket, ct);
        if (expected is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(Hash(ticket))))
            return (SubmissionResult.Unauthorized, null);

        var job = await _jobs.GetAsync(jobId, ct);
        if (job is null || job.Status != InvestigationJobStatus.AwaitingUserInput || job.AwaitingInputKind != kind)
            return (SubmissionResult.NotAwaiting, job);
        return (SubmissionResult.Accepted, job);
    }

    internal static string Hash(string ticket) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
