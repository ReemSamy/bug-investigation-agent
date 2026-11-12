using System.Text.Json;
using System.Text.RegularExpressions;
using BugInvestigationAgent.Core.Abstractions;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Workflow;
using BugInvestigationAgent.Skills.Browser;
using BugInvestigationAgent.Skills.Common;
using BugInvestigationAgent.Skills.Evidence;
using Microsoft.Playwright;

namespace BugInvestigationAgent.Skills.Reproduction;

/// <summary>
/// Follows the ticket's steps in the signed-in browser with the LLM deciding each action: it sees the page's interactive
/// elements and visible text, picks ONE action, the action is executed and checked, and the loop repeats until the LLM
/// concludes (reproduced / not reproduced / needs a person / not enough information) or the action budget runs out.
///
/// Safety, enforced in code (not by the prompt): same-site navigation only; password fields are never shown or filled;
/// actions that save, submit, delete, send or approve data run only when the environment allows test-data changes and
/// never on Production; every action is screenshotted. Credentials are never part of the prompt.
/// </summary>
public sealed class LlmReproductionDriver
{
    public const string AllowTestDataKey = "AllowTestDataChanges";

    private static readonly Regex DataChanging = new(
        @"(?i)\b(save|submit|delete|remove|approve|reject|send|publish|import|upload|confirm|apply|complete|archive|close\s+record|finali[sz]e)\b",
        RegexOptions.Compiled);

    private const string SnapshotScript = """
        () => {
          const visible = e => { const r = e.getBoundingClientRect(); const s = getComputedStyle(e);
            return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
          document.querySelectorAll('[data-bia-idx]').forEach(e => e.removeAttribute('data-bia-idx'));
          const sel = 'a,button,input,select,textarea,[role=button],[role=link],[role=menuitem],[role=tab],[role=option],[role=checkbox],[onclick]';
          const items = [];
          for (const e of document.querySelectorAll(sel)) {
            if (items.length >= 70) break;
            if (!visible(e)) continue;
            const type = (e.getAttribute('type') || '').toLowerCase();
            if (type === 'password' || type === 'hidden' || type === 'file') continue;
            const idx = items.length;
            e.setAttribute('data-bia-idx', String(idx));
            const name = (e.getAttribute('aria-label') || e.innerText || e.getAttribute('title') || e.getAttribute('placeholder')
                          || (e.tagName === 'INPUT' && ['button','submit'].includes(type) ? e.value : '') || e.getAttribute('name') || '')
                          .replace(/\s+/g, ' ').trim().slice(0, 50);
            const tag = e.tagName.toLowerCase();
            const href = tag === 'a' ? (e.getAttribute('href') || '') : '';
            const options = tag === 'select' ? [...e.options].slice(0, 12).map(o => o.text.trim()).join(' | ') : '';
            items.push(`[${idx}] ${tag}${type ? ':' + type : ''}${e.getAttribute('role') ? ' role=' + e.getAttribute('role') : ''} "${name}"`
                       + (href && !href.startsWith('javascript') ? ` href=${href.slice(0, 80)}` : '')
                       + (options ? ` options: ${options}` : ''));
          }
          const notices = [...document.querySelectorAll('[role=alert],[role=dialog],.toast,.alert,.k-notification,.modal.show,.swal2-popup,.notification')]
            .filter(visible).map(e => e.innerText.replace(/\s+/g, ' ').trim()).filter(t => t).slice(0, 5);
          const text = (document.body ? document.body.innerText : '').replace(/[ \t]+/g, ' ').replace(/\n{2,}/g, '\n').slice(0, 1200);
          return JSON.stringify({ url: location.pathname + location.search, title: document.title, items, notices, text });
        }
        """;

    private readonly IAgentRuntime _llm;
    private readonly AgentOptions _options;
    private readonly ISecretRedactor _redactor;

    public LlmReproductionDriver(IAgentRuntime llm, AgentOptions options, ISecretRedactor redactor)
    {
        _llm = llm;
        _options = options;
        _redactor = redactor;
    }

    public sealed record Outcome(ReproductionOutcome Result, string Conclusion, int Actions, bool NeedsPerson, bool InsufficientInformation);

    public async Task<Outcome> RunAsync(IPage page, RuntimeContext ctx, AppEnvironment? env, IReadOnlyList<string> ticketSteps,
        LoginFormOptions? login, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var allowData = !ctx.IsReadOnlyEnvironment && env is not null &&
                        env.Metadata.TryGetValue(AllowTestDataKey, out var allow) && bool.TryParse(allow, out var b) && b;
        var baseUri = new Uri(ctx.EnvironmentUrl!);
        var maxActions = Math.Clamp(_options.LlmReproduction.MaxActions, 3, 60);
        var history = new List<string>();
        string? lastAction = null;
        var repeats = 0;

        progress?.Report($"LLM-guided reproduction: following the ticket steps (up to {maxActions} actions; " +
                         (allowData ? "saving test data is ALLOWED on this environment)." : "it stops before any save/submit/delete)."));

        for (var turn = 1; turn <= maxActions; turn++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await page.EvaluateAsync<string>(SnapshotScript);
            var (decision, raw) = await DecideAsync(ctx, ticketSteps, history, snapshot, allowData, turn, maxActions, cancellationToken);
            if (decision is null)
            {
                progress?.Report("The LLM reply was not a usable action — asking once more. Reply started with: " + Excerpt(raw));
                (decision, raw) = await DecideAsync(ctx, ticketSteps, history, snapshot, allowData, turn, maxActions, cancellationToken);
            }
            if (decision is null)
                return new(ReproductionOutcome.Ambiguous,
                    $"The LLM did not return a usable action (after {turn - 1} action(s)). Its reply started with: {Excerpt(raw)}", turn - 1, false, false);

            var action = decision.Action;
            var describe = Describe(decision);
            progress?.Report($"Action {turn}: {describe}" + (string.IsNullOrWhiteSpace(decision.Thought) ? "" : $" — {_redactor.Redact(decision.Thought)}"));

            if (action == "finish")
            {
                await EvidenceScreenshots.CaptureAsync(page, _options, ctx, "repro-llm-final",
                    $"Final state — {Short(decision.Observation, 160)}",
                    decision.Result == "reproduced" ? EvidenceCategories.FailureState : EvidenceCategories.Workflow, login);
                return Finish(decision, turn - 1);
            }

            // Loop guard: the same action three times in a row means the agent is stuck.
            repeats = describe == lastAction ? repeats + 1 : 0;
            lastAction = describe;
            if (repeats >= 2)
                return new(ReproductionOutcome.Ambiguous, $"Stopped: the same action was repeated without progress ({describe}).", turn, false, false);

            string result;
            try
            {
                result = await ExecuteAsync(page, decision, baseUri, allowData, cancellationToken);
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                result = "FAILED: " + ex.Message.Split('\n')[0];
            }

            if (result.StartsWith("REFUSED-DATA", StringComparison.Ordinal))
            {
                await EvidenceScreenshots.CaptureAsync(page, _options, ctx, $"repro-llm-{turn:00}-stop",
                    $"Stopped before a data-changing action: {describe}", EvidenceCategories.Workflow, login);
                return new(ReproductionOutcome.Ambiguous,
                    $"The next step changes data ({describe}). Saving test data is not allowed on this environment, so a person must do it.",
                    turn, true, false);
            }

            await BrowserWaits.WaitForPageReadyAsync(page, _options.Browser);
            await EvidenceScreenshots.CaptureAsync(page, _options, ctx, $"repro-llm-{turn:00}",
                $"Action {turn}: {_redactor.Redact(describe)} → {Short(result, 80)}", EvidenceCategories.Workflow, login);
            history.Add($"{turn}. {describe} → {result}");
        }

        return new(ReproductionOutcome.Ambiguous, $"Stopped after {maxActions} actions without a conclusion.", maxActions, false, false);
    }

    private sealed record Decision(string Action, int? Element, string? Value, string? Url, string? Key, string? Result, string? Observation, string? Thought);

    private async Task<(Decision? Decision, string Raw)> DecideAsync(RuntimeContext ctx, IReadOnlyList<string> steps, List<string> history, string snapshot,
        bool allowData, int turn, int maxActions, CancellationToken cancellationToken)
    {
        var system = """
            You are a QA engineer reproducing ONE reported bug in a web application, in a NON-production test environment, through
            the browser. You already are signed in. Each turn you get the ticket, the actions done so far and the current page
            (interactive elements numbered [n], visible notices and text). Reply with ONE next action as JSON only, no prose:
            {"thought": "...", "action": "click|fill|select|press|navigate|finish", "element": n, "value": "...", "url": "/path", "key": "Enter",
             "result": "reproduced|not_reproduced|needs_person|insufficient_info", "observation": "what you saw"}
            Rules:
            - Follow the ticket's steps in order. Skip steps about logging in (already done).
            - "navigate" only to paths of this same site (e.g. a URL given in the ticket). "press" only Enter, Escape or Tab.
            - For fill use obviously fake test values (e.g. "BIA test"). Never type credentials or real personal data.
            - finish with "reproduced" ONLY if the defect described in the ticket is visible now (quote it in observation).
            - finish with "not_reproduced" ONLY after doing all steps, when the expected/correct behaviour is clearly visible.
            - finish with "needs_person" when a step needs e-mail, waiting, external systems or data you cannot create.
            - finish with "insufficient_info" when the ticket does not say enough to reproduce it (say what is missing).
            """ + (allowData
                ? "\n- Saving/submitting test records IS allowed on this environment when a step requires it."
                : "\n- Saving, submitting, deleting, sending or approving is NOT allowed: if the next step requires it, finish with needs_person.");

        var user = $"""
            Ticket {ctx.JiraTicketKey}: {_redactor.Redact(ctx.Summary)}
            Description:
            {_redactor.Redact(Short(ctx.Description, 1500))}
            Steps to reproduce:
            {string.Join("\n", steps.Select((s, i) => $"{i + 1}. {_redactor.Redact(s)}"))}
            Expected: {_redactor.Redact(ctx.ExpectedResult ?? "(not stated)")}
            Actual (the defect): {_redactor.Redact(ctx.ActualResult ?? "(not stated — use the summary)")}

            Actions so far ({turn - 1} of max {maxActions}):
            {(history.Count == 0 ? "(none)" : string.Join("\n", history.TakeLast(10)))}

            Current page (JSON):
            {_redactor.Redact(snapshot)}
            """;

        var reply = await _llm.CompleteAsync(new AgentTurnRequest
        {
            SystemPrompt = system,
            Messages = [new AgentMessage { Role = "user", Content = user }],
            MaxTokens = 400,
            JsonOnly = true
        }, cancellationToken);

        using var json = LlmHelpers.TryParseJson(reply.Content);
        if (json is null)
            return (null, reply.Content);
        var r = json.RootElement;
        int? element = !r.TryGetProperty("element", out var el) ? null
            : el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n) ? n
            : el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString()?.Trim('[', ']', ' '), out var sn) ? sn
            : null;
        return (new Decision(
            (LlmHelpers.ReadString(r, "action") ?? "").Trim().ToLowerInvariant(), element,
            LlmHelpers.ReadString(r, "value"), LlmHelpers.ReadString(r, "url"), LlmHelpers.ReadString(r, "key"),
            LlmHelpers.ReadString(r, "result")?.Trim().ToLowerInvariant(), LlmHelpers.ReadString(r, "observation"), LlmHelpers.ReadString(r, "thought")), reply.Content);
    }

    private static async Task<string> ExecuteAsync(IPage page, Decision d, Uri baseUri, bool allowData, CancellationToken cancellationToken)
    {
        switch (d.Action)
        {
            case "navigate":
            {
                if (string.IsNullOrWhiteSpace(d.Url) || !Uri.TryCreate(baseUri, d.Url, out var target) ||
                    !string.Equals(target.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase))
                    return "REFUSED: navigation outside the environment's site is not allowed.";
                await page.GotoAsync(target.ToString());
                return $"opened {target.AbsolutePath}";
            }
            case "press":
            {
                var key = d.Key ?? d.Value ?? "";
                if (key is not ("Enter" or "Escape" or "Tab"))
                    return "REFUSED: only Enter, Escape or Tab may be pressed.";
                if (key == "Enter" && !allowData)
                {
                    // Enter can submit a form: only allowed when saving test data is allowed.
                    var focusedIsFormField = await page.EvaluateAsync<bool>("() => !!document.activeElement && !!document.activeElement.form");
                    if (focusedIsFormField)
                        return "REFUSED-DATA: Enter in a form may submit it.";
                }
                await page.Keyboard.PressAsync(key);
                return $"pressed {key}";
            }
            case "click":
            case "fill":
            case "select":
            {
                if (d.Element is not int idx)
                    return "FAILED: no element number given.";
                var loc = page.Locator($"[data-bia-idx=\"{idx}\"]").First;
                if (await loc.CountAsync() == 0)
                    return $"FAILED: element [{idx}] is no longer on the page.";
                var name = (await loc.EvaluateAsync<string>(
                    "e => (e.getAttribute('aria-label') || e.innerText || e.getAttribute('title') || e.value || '').trim()")) ?? "";
                var type = (await loc.GetAttributeAsync("type") ?? "").ToLowerInvariant();
                if (type == "password")
                    return "REFUSED: password fields are never used.";
                if (d.Action == "click")
                {
                    if (!allowData && (DataChanging.IsMatch(name) || type == "submit"))
                        return "REFUSED-DATA";
                    await loc.ClickAsync(new LocatorClickOptions { Timeout = 10_000 });
                    return $"clicked \"{Short(name, 50)}\"";
                }
                if (d.Action == "fill")
                {
                    await loc.FillAsync(d.Value ?? "", new LocatorFillOptions { Timeout = 10_000 });
                    return $"typed \"{Short(d.Value, 40)}\"";
                }
                await loc.SelectOptionAsync(new SelectOptionValue { Label = d.Value ?? "" }, new LocatorSelectOptionOptions { Timeout = 10_000 });
                return $"selected \"{Short(d.Value, 40)}\"";
            }
            default:
                return $"FAILED: unknown action '{d.Action}'.";
        }
    }

    private Outcome Finish(Decision d, int actions)
    {
        var observation = _redactor.Redact(string.IsNullOrWhiteSpace(d.Observation) ? "(no observation given)" : d.Observation.Trim());
        return d.Result switch
        {
            "reproduced" => new(ReproductionOutcome.Reproduced, "The defect appeared: " + observation, actions, false, false),
            "not_reproduced" => new(ReproductionOutcome.NotReproduced, "All steps done; the defect did not appear: " + observation, actions, false, false),
            "needs_person" => new(ReproductionOutcome.Ambiguous, "A step needs a person: " + observation, actions, true, false),
            "insufficient_info" => new(ReproductionOutcome.Ambiguous, "Not enough information in the ticket: " + observation, actions, false, true),
            _ => new(ReproductionOutcome.Ambiguous, "No clear conclusion: " + observation, actions, false, false)
        };
    }

    private static string Describe(Decision d) => d.Action switch
    {
        "click" => $"click [{d.Element}]",
        "fill" => $"fill [{d.Element}] with \"{Short(d.Value, 40)}\"",
        "select" => $"select \"{Short(d.Value, 40)}\" in [{d.Element}]",
        "navigate" => $"open {d.Url}",
        "press" => $"press {d.Key ?? d.Value}",
        "finish" => $"finish: {d.Result}",
        _ => d.Action
    };

    private string Excerpt(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? "(empty reply)" : "\"" + _redactor.Redact(Short(raw.ReplaceLineEndings(" "), 160)) + "\"";

    private static string Short(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? "" : s.Length <= max ? s.Trim() : s[..(max - 1)].Trim() + "…";
}
