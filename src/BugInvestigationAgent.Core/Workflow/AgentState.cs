namespace BugInvestigationAgent.Core.Workflow;

public enum AgentState
{
    Idle = 0,
    ReadingJira,
    UnderstandingTicket,
    SearchingHistory,
    RetrievingDocs,
    ExtractingContext,
    AwaitingMissingInfo,
    ReadyToReproduce,
    ConnectingEnvironment,
    Authenticating,
    Reproducing,
    Investigating,
    FixProposed,
    AwaitingApproval,
    ApplyingFix,
    Validating,
    Retesting,
    Completed,
    Blocked,
    Failed
}

public enum ReproductionOutcome
{
    NotStarted = 0,
    Reproduced,
    NotReproduced,
    Blocked,
    EnvironmentUnavailable,
    Ambiguous,
    Error
}

public static class AgentStateDisplay
{
    public static string Label(AgentState state) => state switch
    {
        AgentState.Idle => "Idle",
        AgentState.ReadingJira => "Reading Jira",
        AgentState.UnderstandingTicket => "Understanding ticket",
        AgentState.SearchingHistory => "Searching similar bugs",
        AgentState.RetrievingDocs => "Retrieving KMS docs",
        AgentState.ExtractingContext => "Extracting environment",
        AgentState.AwaitingMissingInfo => "Missing information",
        AgentState.ReadyToReproduce => "Ready to reproduce",
        AgentState.ConnectingEnvironment => "Connecting to environment",
        AgentState.Authenticating => "Signing in to environment",
        AgentState.Reproducing => "Reproducing bug",
        AgentState.Investigating => "Investigating code",
        AgentState.FixProposed => "Fix proposed",
        AgentState.AwaitingApproval => "Waiting for approval",
        AgentState.ApplyingFix => "Applying fix",
        AgentState.Validating => "Validating",
        AgentState.Retesting => "Re-testing",
        AgentState.Completed => "Completed",
        AgentState.Blocked => "Blocked",
        AgentState.Failed => "Failed",
        _ => state.ToString()
    };
}
