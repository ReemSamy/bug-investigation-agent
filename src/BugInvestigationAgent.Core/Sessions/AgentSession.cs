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

public interface IAgentSessionStore
{
    AgentSession Create();
    AgentSession? Get(Guid id);
    IReadOnlyList<AgentSession> List();
}

public sealed class InMemoryAgentSessionStore : IAgentSessionStore
{
    private readonly Dictionary<Guid, AgentSession> _sessions = new();

    public AgentSession Create()
    {
        var session = new AgentSession();
        session.Context.SessionId = session.Id;
        _sessions[session.Id] = session;
        return session;
    }

    public AgentSession? Get(Guid id) => _sessions.TryGetValue(id, out var s) ? s : null;

    public IReadOnlyList<AgentSession> List() => _sessions.Values.OrderByDescending(s => s.CreatedAt).ToList();
}
