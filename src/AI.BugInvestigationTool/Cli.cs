using System.Text.Json;
using AI.BugInvestigationTool.Configuration;
using AI.BugInvestigationTool.Models;
using AI.BugInvestigationTool.Services.Workflow;
using AI.BugInvestigationTool.Storage;

namespace AI.BugInvestigationTool;

/// <summary>
/// Lightweight command-line entry point used by the Claude Code slash command. It calls the same
/// <see cref="InvestigationEngine"/> as the web UI. Exit codes: 0 completed, 2 needs input, 1 failed/usage error, 130 cancelled.
/// </summary>
public static class Cli
{
    public const string Usage = """
        AI Bug Investigation Tool — CLI

          investigate <jira-or-pr-url> [--info key=value]... [--json]
              Run an investigation. --info supplies missing information (e.g. expectedBehavior, reproductionSteps,
              environmentUrl, noLoginRequired=true, uiClarification). Reproduction steps: separate lines with '\n'.
          resume <investigation-id> [--info key=value]... [--json]
              Continue an investigation that was waiting for input.
          show <investigation-id> [--json]
              Print a saved report.
          list
              List recent investigations.
          transitions <investigation-id>
              List the Jira transitions available for the ticket (read-only).
          publish-jira <investigation-id> [--comment] [--attach-screenshots] [--transition "<name>"]
              APPROVAL REQUIRED. Posts the drafted comment and/or changes the Jira status.
          publish-pr <investigation-id>
              APPROVAL REQUIRED. Posts the review summary as one PR comment thread.

        Credentials come from environment variables (ANTHROPIC_API_KEY, BIT_JIRA_EMAIL, BIT_JIRA_API_TOKEN, BIT_JIRA_BASE_URL,
        BIT_ADO_ORGANIZATION, BIT_ADO_PROJECT, BIT_ADO_PAT, BIT_ENV_URL, BIT_ENV_USERNAME, BIT_ENV_PASSWORD, BIT_ALLOWED_HOSTS)
        or `dotnet user-secrets` keys "Credentials:<Name>". They are never written to disk by this tool.
        """;

    public static readonly HashSet<string> Commands = ["investigate", "resume", "show", "list", "transitions", "publish-jira", "publish-pr", "help", "--help"];

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, IConfiguration config)
    {
        var command = args[0].ToLowerInvariant();
        if (command is "help" or "--help") { Console.WriteLine(Usage); return 0; }

        var engine = services.GetRequiredService<InvestigationEngine>();
        var store = services.GetRequiredService<InvestigationStore>();
        var creds = Credentials.FromConfiguration(config);
        var json = args.Contains("--json");
        var info = ParseInfo(args);
        if (info is null) return 1;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        engine.OnProgress = (_, p) => Console.Error.WriteLine($"[{p.At:HH:mm:ss}] {p.Step}: {p.Message}");

        try
        {
            switch (command)
            {
                case "investigate":
                {
                    var url = Positional(args);
                    if (url is null) { Console.Error.WriteLine("Missing URL.\n" + Usage); return 1; }
                    return Print(await engine.InvestigateAsync(url, creds, info, cts.Token), json);
                }
                case "resume":
                {
                    var id = Positional(args);
                    if (id is null) { Console.Error.WriteLine("Missing investigation id."); return 1; }
                    return Print(await engine.RunAsync(id, creds, info, cts.Token), json);
                }
                case "show":
                {
                    var inv = await store.LoadAsync(Positional(args) ?? "", cts.Token);
                    if (inv is null) { Console.Error.WriteLine("Investigation not found."); return 1; }
                    return Print(inv, json);
                }
                case "list":
                    foreach (var inv in await store.ListRecentAsync(30, cts.Token))
                        Console.WriteLine($"{inv.Id}\t{inv.Step}\t{(inv.Report is null ? "" : inv.Target.Kind == TargetKind.JiraIssue ? ReportFormatter.OutcomeLabel(inv.Report.Outcome) : inv.Report.PrVerdict)}\t{inv.Target.Url}");
                    return 0;
                case "transitions":
                    foreach (var t in await engine.GetJiraTransitionsAsync(Positional(args) ?? "", creds, cts.Token))
                        Console.WriteLine($"{t.Id}\t{t.Name}\t→ {t.ToStatus}");
                    return 0;
                case "publish-jira":
                {
                    var transition = Option(args, "--transition");
                    var request = new JiraPublishRequest(args.Contains("--comment"), null, args.Contains("--attach-screenshots"), transition);
                    if (!request.PostComment && transition is null) { Console.Error.WriteLine("Nothing to publish: pass --comment and/or --transition."); return 1; }
                    var inv = await engine.PublishJiraAsync(Positional(args) ?? "", creds, request, cts.Token);
                    foreach (var p in inv.Publishing.TakeLast(3)) Console.WriteLine($"{p.Action}: {(p.Success ? "OK" : "FAILED")} — {p.Detail}");
                    return inv.Publishing.TakeLast(3).All(p => p.Success) ? 0 : 1;
                }
                case "publish-pr":
                {
                    var inv = await engine.PublishPullRequestCommentAsync(Positional(args) ?? "", creds, null, cts.Token);
                    var p = inv.Publishing[^1];
                    Console.WriteLine($"{p.Action}: {(p.Success ? "OK" : "FAILED")} — {p.Detail}");
                    return p.Success ? 0 : 1;
                }
            }
        }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 1; }
        catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 1; }
        catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }

        Console.Error.WriteLine(Usage);
        return 1;
    }

    private static int Print(Investigation inv, bool json)
    {
        if (json) Console.WriteLine(JsonSerializer.Serialize(inv, InvestigationStore.Json));
        else Console.WriteLine(ReportFormatter.ToMarkdown(inv));
        return inv.Step switch
        {
            InvestigationStep.Completed => 0,
            InvestigationStep.WaitingForInput => 2,
            InvestigationStep.Cancelled => 130,
            _ => 1,
        };
    }

    internal static Dictionary<string, string>? ParseInfo(string[] args)
    {
        var info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] != "--info") continue;
            if (i + 1 >= args.Length || !args[i + 1].Contains('='))
            {
                Console.Error.WriteLine("--info expects key=value");
                return null;
            }
            var kv = args[++i];
            var eq = kv.IndexOf('=');
            info[kv[..eq].Trim()] = kv[(eq + 1)..].Replace("\\n", "\n");
        }
        return info;
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>First argument after the command that is not an option or an option value.</summary>
    internal static string? Positional(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--info" or "--transition") { i++; continue; }
            if (args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            return args[i];
        }
        return null;
    }
}
