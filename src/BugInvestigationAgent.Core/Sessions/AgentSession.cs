using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Workflow;

namespace BugInvestigationAgent.Core.Sessions;

public sealed class AgentSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public AgentState State { get; set; } = AgentState.Idle;
    public RuntimeContext Context { get; set; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LastError { get; set; }
    public TaskCompletionSource<bool>? ApprovalWaiter { get; set; }
    public TaskCompletionSource<bool>? MissingInfoWaiter { get; set; }

    public event Action? Changed;

    public void SetState(AgentState state, string? logMessage = null)
    {
        State = state;
        UpdatedAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(logMessage))
            Context.StatusLog.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} [{AgentStateDisplay.Label(state)}] {logMessage}");
        Changed?.Invoke();
    }

    public void NotifyChanged()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
        Changed?.Invoke();
    }
}

/// <summary>
/// Live (in-process) runtime state of investigations being executed by THIS process.
/// Durable state for team usage lives in the investigation job store; this only holds the running pipeline.
/// </summary>
public interface IAgentSessionStore
{
    AgentSession Create();
    /// <summary>Creates a session whose id equals a durable job id (hosted mode).</summary>
    AgentSession Create(Guid id);
    AgentSession? Get(Guid id);
    IReadOnlyList<AgentSession> List();
    void Remove(Guid id);
}

public sealed class InMemoryAgentSessionStore : IAgentSessionStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, AgentSession> _sessions = new();

    public AgentSession Create() => Create(Guid.NewGuid());

    public AgentSession Create(Guid id)
    {
        var session = new AgentSession { Id = id };
        session.Context.SessionId = session.Id;
        if (!_sessions.TryAdd(session.Id, session))
            throw new InvalidOperationException($"Session {id} already exists.");
        return session;
    }

    public AgentSession? Get(Guid id) => _sessions.TryGetValue(id, out var s) ? s : null;

    public IReadOnlyList<AgentSession> List() => _sessions.Values.OrderByDescending(s => s.CreatedAt).ToList();

    public void Remove(Guid id) => _sessions.TryRemove(id, out _);
}
