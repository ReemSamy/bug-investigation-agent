using System.Text.Json;
using System.Text.Json.Nodes;
using BugInvestigationAgent.Core.Jobs;

namespace BugInvestigationAgent.Api.A2A;

/// <summary>A2A 1.0 JSON-RPC shapes used by Jira's rovo:agentConnector (Forge remote agents).</summary>
public static class A2ATaskStates
{
    public const string Submitted = "TASK_STATE_SUBMITTED";
    public const string Working = "TASK_STATE_WORKING";
    public const string InputRequired = "TASK_STATE_INPUT_REQUIRED";
    public const string AuthRequired = "TASK_STATE_AUTH_REQUIRED";
    public const string Completed = "TASK_STATE_COMPLETED";
    public const string Failed = "TASK_STATE_FAILED";
    public const string Canceled = "TASK_STATE_CANCELED";
    public const string Rejected = "TASK_STATE_REJECTED";

    public static string For(InvestigationJobStatus status) => status switch
    {
        InvestigationJobStatus.Queued => Submitted,
        InvestigationJobStatus.Running => Working,
        InvestigationJobStatus.AwaitingUserInput => InputRequired,
        InvestigationJobStatus.Completed => Completed,
        InvestigationJobStatus.Cancelled => Canceled,
        _ => Failed // Failed, TimedOut
    };
}

public static class A2AErrors
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int TaskNotFound = -32001;
    public const int TaskNotCancelable = -32002;
    public const int UnsupportedOperation = -32004;

    public static string Reason(int code) => code switch
    {
        TaskNotFound => "TASK_NOT_FOUND",
        TaskNotCancelable => "TASK_NOT_CANCELABLE",
        UnsupportedOperation => "UNSUPPORTED_OPERATION",
        MethodNotFound => "METHOD_NOT_FOUND",
        _ => "INVALID_REQUEST"
    };
}

public static class A2AJson
{
    public static JsonObject Result(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result
    };

    public static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
            ["data"] = new JsonArray
            {
                new JsonObject
                {
                    ["@type"] = "type.googleapis.com/google.rpc.ErrorInfo",
                    ["reason"] = A2AErrors.Reason(code),
                    ["domain"] = "a2a-protocol.org"
                }
            }
        }
    };

    /// <summary>One A2A task object for a job (the only shape Jira needs to render progress).</summary>
    public static JsonObject Task(string taskId, string contextId, string state, string text) => new()
    {
        ["task"] = new JsonObject
        {
            ["id"] = taskId,
            ["contextId"] = contextId,
            ["status"] = new JsonObject
            {
                ["state"] = state,
                ["message"] = new JsonObject
                {
                    ["role"] = "ROLE_AGENT",
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = text } },
                    ["messageId"] = Guid.NewGuid().ToString(),
                    ["taskId"] = taskId,
                    ["contextId"] = contextId
                },
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
            }
        }
    };
}

/// <summary>Parsed SendMessage params (untrusted input — identities are re-derived from validated tokens).</summary>
public sealed class IncomingMessage
{
    public string? ContextId { get; init; }
    public string? TaskId { get; init; }
    public string Text { get; init; } = "";
    public string? UserAccountId { get; init; }
    public string? InvocationType { get; init; }
    public string? IssueId { get; init; }
    public string? IssueKey { get; init; }

    public static IncomingMessage Parse(JsonElement @params)
    {
        if (!@params.TryGetProperty("message", out var message))
            return new IncomingMessage();

        var texts = new List<string>();
        string? userAccountId = null, invocationType = null, issueId = null, issueKey = null;
        if (message.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    texts.Add(t.GetString()!);
                if (part.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    userAccountId ??= Str(data, "userAccountId");
                    invocationType ??= Str(data, "invocationType");
                    if (data.TryGetProperty("issue", out var issue) && issue.ValueKind == JsonValueKind.Object)
                    {
                        issueId ??= Str(issue, "id");
                        if (issue.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object)
                            issueKey ??= Str(fields, "key");
                        issueKey ??= Str(issue, "key");
                    }
                }
            }
        }

        return new IncomingMessage
        {
            ContextId = Str(message, "contextId"),
            TaskId = Str(message, "taskId"),
            Text = string.Join("\n", texts).Trim(),
            UserAccountId = userAccountId,
            InvocationType = invocationType,
            IssueId = issueId,
            IssueKey = issueKey
        };
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
