using System.Text.RegularExpressions;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Skills;
using BugInvestigationAgent.Core.Workflow;
using BugInvestigationAgent.Skills.Browser;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace BugInvestigationAgent.Skills.Reproduction;

public sealed class ReproductionSkill : ISkill
{
    private readonly AgentOptions _options;
    private readonly ISecretRedactor _redactor;
    private readonly IPlaywrightSessionHub _sessions;

    public ReproductionSkill(IOptions<AgentOptions> options, ISecretRedactor redactor, IPlaywrightSessionHub sessions)
    {
        _options = options.Value;
        _redactor = redactor;
        _sessions = sessions;
    }

    public string SkillId => SkillIds.Reproduction;
    public string DisplayName => "Reproduction";
    public AgentPermission RequiredPermissions => AgentPermission.UseBrowser;

    public bool CanExecute(RuntimeContext context, IPermissionGate permissions) =>
        permissions.IsAllowed(RequiredPermissions);

    public async Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var ctx = request.Context;
        var isRetest = request.Inputs is not null &&
                       request.Inputs.TryGetValue("mode", out var mode) &&
                       string.Equals(mode, "retest", StringComparison.OrdinalIgnoreCase);

        if (ctx.ReproductionOutcome is ReproductionOutcome.EnvironmentUnavailable or ReproductionOutcome.Error or ReproductionOutcome.Blocked
            && !isRetest)
        {
            request.Progress?.Report($"Skipping step execution due to prior outcome {ctx.ReproductionOutcome}.");
            return SkillResult.Ok("Reproduction skipped — prior technical/environment failure preserved.");
        }

        if (string.IsNullOrWhiteSpace(ctx.EnvironmentUrl))
        {
            ctx.ReproductionOutcome = ReproductionOutcome.Blocked;
            ctx.ReproductionNotes = "Missing environment URL.";
            return SkillResult.Ok(ctx.ReproductionNotes);
        }

        var evidenceDir = Path.Combine(
            Path.IsPathRooted(_options.EvidenceDirectory) ? _options.EvidenceDirectory : Path.Combine(Directory.GetCurrentDirectory(), _options.EvidenceDirectory),
            ctx.SessionId.ToString("N"));
        Directory.CreateDirectory(evidenceDir);

        try
        {
            if (!_sessions.TryGetPage(ctx.SessionId, out var page))
            {
                ctx.ReproductionOutcome = ReproductionOutcome.Blocked;
                ctx.ReproductionNotes =
                    "No active logged-in browser session. Login and reproduction must run in the same browser; reconnect credentials and Continue.";
                return SkillResult.Ok(ctx.ReproductionNotes);
            }

            page.SetDefaultTimeout(_options.Browser.ActionTimeoutMs);
            page.SetDefaultNavigationTimeout(_options.Browser.NavigationTimeoutMs);

            var steps = TicketStepPlanner.ResolveSteps(ctx);
            request.Progress?.Report(
                ctx.CustomFields.ContainsKey("stepsInferredFromTicket")
                    ? $"Jira had no steps — inferred {steps.Count} navigation step(s) from the ticket summary/description."
                    : $"Following {steps.Count} reproduction step(s).");

            var jiraShots = ctx.Attachments.Count(a => !string.IsNullOrWhiteSpace(a.LocalPath));
            if (jiraShots > 0)
            {
                request.Progress?.Report(
                    $"Using {jiraShots} Jira screenshot attachment(s) as visual reference — will open the same control and simulate the same typing/select behavior.");
            }

            var pageErrors = new List<string>();
            void OnPageError(object? _, string err) => pageErrors.Add(err);
            page.PageError += OnPageError;

            try
            {
                // Give home/assignments time to finish loading before module navigation.
                request.Progress?.Report("Waiting for portal UI to finish loading before feature navigation…");
                await BrowserWaits.WaitForPageReadyAsync(page, _options.Browser);

                var stepIndex = 0;
                string? previousFingerprint = await CaptureFingerprintAsync(page);
                var distinctShots = 0;
                var failedNavSteps = 0;

                foreach (var step in steps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stepIndex++;
                    request.Progress?.Report($"Step {stepIndex}/{steps.Count}: {step}");
                    var before = previousFingerprint;
                    var stepOk = true;
                    try
                    {
                        stepOk = await TryFollowStepAsync(page, step, _options.Browser, request.Progress);
                        await BrowserWaits.WaitForPageReadyAsync(page, _options.Browser);
                    }
                    catch (TimeoutException ex)
                    {
                        stepOk = false;
                        request.Progress?.Report($"Step {stepIndex} timed out: {_redactor.Redact(ex.Message)}");
                    }
                    catch (PlaywrightException ex)
                    {
                        stepOk = false;
                        request.Progress?.Report($"Step {stepIndex} UI issue: {_redactor.Redact(ex.Message)}");
                    }

                    var after = await CaptureFingerprintAsync(page);
                    var changed = !string.Equals(before, after, StringComparison.Ordinal);
                    var requiresNav = RequiresPageChange(step);

                    if (requiresNav && !changed)
                    {
                        failedNavSteps++;
                        stepOk = false;
                        request.Progress?.Report(
                            $"Step {stepIndex} FAILED — page did not change (still same screen). Will not treat identical screenshots as progress.");
                    }

                    // Skip duplicate screenshots when the page did not change (except observe steps).
                    if (!changed && distinctShots > 0 && !Regex.IsMatch(step, @"(?i)^observe"))
                    {
                        ctx.Evidence.Add(new EvidenceItem
                        {
                            Kind = "note",
                            Caption = $"Step {stepIndex} skipped duplicate screenshot — UI unchanged after: {step}"
                        });
                        previousFingerprint = after;
                        continue;
                    }

                    var shot = Path.Combine(evidenceDir, $"{(isRetest ? "retest" : "repro")}-step-{stepIndex:00}.png");
                    await page.ScreenshotAsync(new PageScreenshotOptions { Path = shot, FullPage = true });
                    distinctShots++;
                    ctx.Evidence.Add(new EvidenceItem
                    {
                        Kind = "screenshot",
                        Path = shot,
                        Caption = stepOk && (changed || !requiresNav)
                            ? $"Step {stepIndex}: {step}"
                            : $"Step {stepIndex} (FAILED — UI may be unchanged): {step}"
                    });
                    previousFingerprint = after;
                }

                ctx.CustomFields["distinctScreenshotCount"] = distinctShots.ToString();
                ctx.CustomFields["failedNavSteps"] = failedNavSteps.ToString();
            }
            finally
            {
                page.PageError -= OnPageError;
            }

            var content = (await page.ContentAsync()).ToLowerInvariant();
            var bodyText = "";
            try { bodyText = (await page.InnerTextAsync("body")).ToLowerInvariant(); } catch { /* ignore */ }
            var url = page.Url;

            var defect = TicketStepPlanner.DescribeDefect(ctx.Summary, ctx.Description);
            var corpus = $"{ctx.Summary}\n{ctx.Description}";
            var onProposalPage = url.Contains("/Proposal", StringComparison.OrdinalIgnoreCase)
                                 || Regex.IsMatch(bodyText, @"(?i)\bproposal\b") && !Regex.IsMatch(bodyText, @"(?i)my assignments");
            var onCreateForm = url.Contains("/Create", StringComparison.OrdinalIgnoreCase)
                               || content.Contains("addaudit", StringComparison.OrdinalIgnoreCase)
                               || Regex.IsMatch(bodyText, @"(?i)add audit");
            var onIncidentChecklistTab =
                (url.Contains("/Investigator", StringComparison.OrdinalIgnoreCase) ||
                 url.Contains("Incident", StringComparison.OrdinalIgnoreCase)) &&
                (Regex.IsMatch(bodyText, @"(?i)\bchecklist\b") || content.Contains("field-wrapper"));
            var onWrongChecklistAdmin = Regex.IsMatch(bodyText, @"(?i)pre-?moc questionnaires?") ||
                                        url.Contains("PreMoc", StringComparison.OrdinalIgnoreCase);

            var hasJsToastSignal =
                content.Contains("cannot read properties") ||
                content.Contains("cannot read property") ||
                pageErrors.Any(e => e.Contains("Cannot read", StringComparison.OrdinalIgnoreCase));
            var hasConfirmationSignal =
                Regex.IsMatch(bodyText, @"(?i)sent for approval") ||
                content.Contains("sent for approval", StringComparison.OrdinalIgnoreCase);
            var hasStackSignal = content.Contains("stack trace") ||
                                 pageErrors.Any(e => e.Contains("TypeError", StringComparison.OrdinalIgnoreCase));
            var hasEmptyTextResponseSignal =
                Regex.IsMatch(content, @"data-response-type\s*=\s*[""']?4[""']?") &&
                (Regex.IsMatch(content, @"field-wrapper[^>]*>\s*</div>") ||
                 Regex.IsMatch(content, @"class=[""']field-wrapper[""'][^>]*>\s*<"));

            // Stronger DOM check via page evaluate when this defect type is expected.
            if (TicketStepPlanner.LooksLikeChecklistTextResponseDefect(corpus) && !hasEmptyTextResponseSignal)
            {
                try
                {
                    hasEmptyTextResponseSignal = await page.EvaluateAsync<bool>(@"() => {
                      const nodes = [...document.querySelectorAll('.field-wrapper[data-response-type=""4""], .field-wrapper[data-response-type=""4""]')];
                      if (!nodes.length) {
                        const any = [...document.querySelectorAll('[data-response-type=""4""]')];
                        return any.some(n => !n.querySelector('input, textarea, select') && (n.innerText || '').trim().length < 2);
                      }
                      return nodes.some(n => !n.querySelector('input, textarea') && (n.innerText || '').trim().length < 2);
                    }");
                }
                catch { /* ignore */ }
            }

            if (pageErrors.Count > 0)
            {
                ctx.Evidence.Add(new EvidenceItem
                {
                    Kind = "error",
                    Caption = _redactor.Redact(string.Join(" | ", pageErrors.Take(5)))
                });
            }

            bool defectSeen;
            var hasStaleMultiSelectTyping = false;
            if (TicketStepPlanner.LooksLikeMultiSelectAssigneeTypingDefect(corpus))
            {
                try
                {
                    hasStaleMultiSelectTyping = await page.EvaluateAsync<bool>(@"() => {
                      const inputs = [...document.querySelectorAll('.k-multiselect .k-input-inner, .k-multiselect input.k-input, .k-multiselect .k-input')];
                      return inputs.some(el => ((el.value || el.textContent || '').trim().length >= 2));
                    }");
                }
                catch { /* ignore */ }
            }

            if (TicketStepPlanner.LooksLikeChecklistTextResponseDefect(corpus))
            {
                // Must see the empty Text Response control — admin setup alone is NOT reproduction.
                defectSeen = hasEmptyTextResponseSignal && onIncidentChecklistTab && !onWrongChecklistAdmin;
            }
            else if (TicketStepPlanner.LooksLikeMultiSelectAssigneeTypingDefect(corpus))
            {
                defectSeen = hasStaleMultiSelectTyping;
            }
            else if (LooksLikeJsErrorTicket(ctx))
            {
                defectSeen = hasJsToastSignal || hasStackSignal || pageErrors.Count > 0;
            }
            else if (Regex.IsMatch(defect, @"(?i)approval|confirmation"))
            {
                defectSeen = hasConfirmationSignal;
            }
            else
            {
                // Do not treat random page TypeError strings as reproduction for unrelated tickets.
                defectSeen = hasJsToastSignal || hasConfirmationSignal;
            }

            var reachedFeature = TicketStepPlanner.LooksLikeChecklistTextResponseDefect(corpus)
                ? onIncidentChecklistTab && !onWrongChecklistAdmin
                : TicketStepPlanner.LooksLikeMultiSelectAssigneeTypingDefect(corpus)
                    ? await DetectAssigneeMultiSelectPresentAsync(page)
                    : TicketStepPlanner.LooksLikeProposalFlow(corpus)
                        ? onProposalPage
                        : onCreateForm || onProposalPage || onIncidentChecklistTab;

            if (onWrongChecklistAdmin && TicketStepPlanner.LooksLikeChecklistTextResponseDefect(corpus))
            {
                ctx.Evidence.Add(new EvidenceItem
                {
                    Kind = "note",
                    Caption = "Navigation landed on Pre-MOC Questionnaires — ticket requires Administration → Checklist → Incident (Investigator Checklists), then Incident Checklist tab."
                });
            }

            if (defectSeen && !isRetest)
            {
                ctx.ReproductionOutcome = ReproductionOutcome.Reproduced;
                ctx.ReproductionNotes = $"Reproduced {ctx.JiraTicketKey}: observed defect signal for '{defect}' (see Evidence).";
            }
            else if (isRetest && !defectSeen)
            {
                ctx.ReproductionOutcome = ReproductionOutcome.NotReproduced;
                ctx.ReproductionNotes = $"Retest for {ctx.JiraTicketKey} did not show: {defect}.";
            }
            else if (isRetest && defectSeen)
            {
                ctx.ReproductionOutcome = ReproductionOutcome.Reproduced;
                ctx.ReproductionNotes = $"Retest for {ctx.JiraTicketKey} still shows: {defect}.";
            }
            else
            {
                ctx.ReproductionOutcome = ReproductionOutcome.Ambiguous;
                var distinct = ctx.CustomFields.TryGetValue("distinctScreenshotCount", out var d) ? d : "?";
                var failed = ctx.CustomFields.TryGetValue("failedNavSteps", out var f) ? f : "0";
                ctx.ReproductionNotes = reachedFeature
                    ? $"Investigated {ctx.JiraTicketKey} and reached a feature page, but never clearly saw: {defect}. Distinct screenshots={distinct}, failed navigation steps={failed}."
                    : $"Investigated {ctx.JiraTicketKey}, but did not finish the full ticket path (stopped before the defect screen). Distinct screenshots={distinct}, failed navigation steps={failed}. Still need: {defect}.";
            }

            return SkillResult.Ok($"Reproduction classified as {ctx.ReproductionOutcome} for {ctx.JiraTicketKey}.");
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
                                             || ex.Message.Contains("browser", StringComparison.OrdinalIgnoreCase))
        {
            ctx.ReproductionOutcome = ReproductionOutcome.Error;
            ctx.ReproductionNotes = "Browser runtime unavailable. Install Playwright browsers (pwsh bin/Debug/net8.0/playwright.ps1 install).";
            return SkillResult.Ok(ctx.ReproductionNotes);
        }
        catch (Exception ex)
        {
            ctx.ReproductionOutcome = ReproductionOutcome.Error;
            ctx.ReproductionNotes = _redactor.Redact(ex.Message);
            return SkillResult.Ok("Reproduction ended with technical ERROR (not NOT_REPRODUCED).");
        }
    }

    private static bool RequiresPageChange(string step) =>
        // Only steps that must navigate away from the current screen.
        Regex.IsMatch(step, @"(?i)^open module\s+['""]|^open administration|create new|open the first|open '[^']+' from the (?:module|application|current area)|open '[^']+' inside administration|open '[^']+' \(next navigation")
        && !Regex.IsMatch(step, @"(?i)^observe|^leave required|^confirm the|^open modules menu|multi-select|type a partial|select a matching|reopen the assignees");

    private static async Task<string> CaptureFingerprintAsync(IPage page)
    {
        var url = page.Url ?? "";
        var title = await page.TitleAsync();
        // Prefer URL + title; body text only as a short supplement so menu overlays don't dominate.
        string crumb = "";
        try
        {
            crumb = await page.EvaluateAsync<string>(@"() => {
                const h = document.querySelector('h1,h2,.page-title,.breadcrumb,.kms-page-title');
                return (location.pathname + '|' + (h?.innerText || '') + '|' + (document.title || '')).slice(0, 240);
              }");
        }
        catch
        {
            crumb = $"{url}|{title}";
        }

        return $"{url}|{title}|{crumb}";
    }

    private static bool LooksLikeJsErrorTicket(RuntimeContext ctx) =>
        Regex.IsMatch($"{ctx.Summary}\n{ctx.Description}", @"(?i)cannot read properties|js error toast|raw js|undefined \(reading");

    private static async Task<bool> TryFollowStepAsync(IPage page, string step, BrowserTimingOptions timing, IProgress<string>? progress)
    {
        var text = step.Trim();

        if (Regex.IsMatch(text, @"(?i)^open modules menu"))
        {
            return await ClickBestAsync(page, new[] { "Modules", "Module" }, timing);
        }

        if (Regex.IsMatch(text, @"(?i)^open administration"))
        {
            progress?.Report("Opening Administration (as named in the ticket — not inventing a Modules feature)…");
            // Administration may appear under Modules list OR as its own control — try both.
            var clicked = await ClickBestAsync(page, new[] { "Administration", "Admin" }, timing);
            if (!clicked)
            {
                await ClickBestAsync(page, new[] { "Modules", "Module" }, timing);
                await page.WaitForTimeoutAsync(timing.PostActionDelayMs);
                clicked = await ClickBestAsync(page, new[] { "Administration", "Admin" }, timing);
            }

            return clicked;
        }

        if (Regex.IsMatch(text, @"(?i)investigator checklists|checklist \(incident\)|not pre-moc"))
        {
            progress?.Report("Opening Investigator Checklists (Incident) — avoiding Pre-MOC…");
            var clicked = await ClickBestAsync(
                page,
                new[] { "Investigator Checklists", "Incident Checklists", "Checklist" },
                timing);
            // If we landed on Pre-MOC, try again with a more specific label.
            var body = "";
            try { body = await page.InnerTextAsync("body"); } catch { /* ignore */ }
            if (Regex.IsMatch(body, @"(?i)pre-?moc"))
            {
                progress?.Report("Avoiding Pre-MOC — retrying Investigator Checklists…");
                clicked = await ClickBestAsync(page, new[] { "Investigator Checklists", "Incident" }, timing);
            }

            return clicked;
        }

        if (Regex.IsMatch(text, @"(?i)incident checklist type|open incident checklist"))
        {
            return await ClickBestAsync(
                page,
                new[] { "Incident", "Investigator Checklists", "Investigator" },
                timing);
        }

        if (Regex.IsMatch(text, @"(?i)create new draft|select an existing draft|create/?select"))
        {
            progress?.Report("Selecting/creating a Draft checklist…");
            var clicked = await ClickBestAsync(
                page,
                new[] { "Create New Draft", "Create new draft", "Select" },
                timing,
                preferSidePanel: false);
            return clicked;
        }

        if (Regex.IsMatch(text, @"(?i)add question"))
        {
            return await ClickBestAsync(page, new[] { "Add Question", "Add question", "Add" }, timing);
        }

        if (Regex.IsMatch(text, @"(?i)response type\s*=\s*text response|set response type"))
        {
            progress?.Report("Setting Response Type = Text Response…");
            // Try combobox / select labeled Response Type, then option Text Response.
            try
            {
                var combo = page.GetByLabel("Response Type", new PageGetByLabelOptions { Exact = false });
                if (await combo.CountAsync() > 0)
                {
                    await combo.First.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                    await page.WaitForTimeoutAsync(400);
                }
            }
            catch (PlaywrightException) { /* fall through */ }

            return await ClickBestAsync(page, new[] { "Text Response", "Text response", "Free Text" }, timing);
        }

        if (Regex.IsMatch(text, @"(?i)promote the checklist|promote"))
        {
            return await ClickBestAsync(page, new[] { "Promote", "Promote Checklist" }, timing);
        }

        if (Regex.IsMatch(text, @"(?i)create or open an incident|matching the checklist"))
        {
            progress?.Report("Opening Investigator / Incident for checklist verification…");
            await ClickBestAsync(page, new[] { "Modules", "Module" }, timing);
            await page.WaitForTimeoutAsync(timing.PostActionDelayMs);
            var opened = await ClickBestAsync(page, new[] { "Investigator", "Incident" }, timing);
            if (!opened)
                return false;
            // Open first incident row if a list is showing.
            return await OpenFirstGridRecordAsync(page, timing, progress) || opened;
        }

        if (Regex.IsMatch(text, @"(?i)incident checklist tab|checklist tab where answers"))
        {
            progress?.Report("Opening Incident Checklist tab…");
            return await ClickBestAsync(page, new[] { "Checklist", "Checklists" }, timing);
        }

        if (Regex.IsMatch(text, @"(?i)assignees multi-select|select assignees|groups multi-select|multi-select control on the dialog|people picker"))
        {
            progress?.Report("Opening multi-select control…");
            return await OpenAssigneeMultiSelectAsync(page, timing, progress);
        }

        if (Regex.IsMatch(text, @"(?i)person-plus|edit groups|change assignments|first (active )?persons? row|add to an assignment"))
        {
            progress?.Report("Opening Persons row action (person-plus / Change Assignments)…");
            return await ClickPersonsRowActionAsync(page, timing, progress);
        }

        if (Regex.IsMatch(text, @"(?i)type a partial name into the multi-select"))
        {
            progress?.Report("Typing into multi-select filter…");
            return await TypeIntoMultiSelectFilterAsync(page, "adm", timing, progress);
        }

        if (Regex.IsMatch(text, @"(?i)select a matching assignee option"))
        {
            progress?.Report("Selecting an assignee option from multi-select…");
            return await SelectFirstMultiSelectOptionAsync(page, timing, progress);
        }

        if (Regex.IsMatch(text, @"(?i)reopen the assignees multi-select|close or blur the multi-select"))
        {
            progress?.Report("Closing then reopening Assignees multi-select…");
            await page.Keyboard.PressAsync("Escape");
            await page.WaitForTimeoutAsync(400);
            await page.Mouse.ClickAsync(8, 8);
            await page.WaitForTimeoutAsync(500);
            return await OpenAssigneeMultiSelectAsync(page, timing, progress);
        }

        var moduleMatch = Regex.Match(text, @"^(?i)open module\s+['""]?(.+?)['""]?\s*$");
        if (moduleMatch.Success)
        {
            var module = moduleMatch.Groups[1].Value.Trim();
            // Only open Modules menu when the ticket/step is actually a Modules-area item.
            await ClickBestAsync(page, new[] { "Modules", "Module" }, timing);
            await page.WaitForTimeoutAsync(timing.PostActionDelayMs);
            return await ClickBestAsync(page, new[] { module }, timing);
        }

        var insideArea = Regex.Match(text, @"^(?i)open\s+['""](.+?)['""]\s+inside\s+administration");
        if (insideArea.Success)
            return await ClickBestAsync(page, ExpandLabels(insideArea.Groups[1].Value), timing);

        var ticketNamed = Regex.Match(text, @"^(?i)open\s+['""](.+?)['""]\s+from the application UI");
        if (ticketNamed.Success)
            return await ClickBestAsync(page, ExpandLabels(ticketNamed.Groups[1].Value), timing);

        var nextNav = Regex.Match(text, @"^(?i)open\s+['""](.+?)['""]\s+\(next navigation");
        if (nextNav.Success)
            return await ClickBestAsync(page, ExpandLabels(nextNav.Groups[1].Value), timing);

        if (Regex.IsMatch(text, @"(?i)open the first audit|open the first .*record|grid link"))
        {
            progress?.Report("Opening first audit/proposal row from the grid…");
            return await OpenFirstGridRecordAsync(page, timing, progress);
        }

        if (Regex.IsMatch(text, @"(?i)confirm the proposal form|confirm the add/create form"))
        {
            var url = page.Url;
            var ok = text.Contains("Proposal", StringComparison.OrdinalIgnoreCase)
                ? url.Contains("/Proposal", StringComparison.OrdinalIgnoreCase)
                : url.Contains("/Create", StringComparison.OrdinalIgnoreCase) || url.Contains("Create", StringComparison.OrdinalIgnoreCase);
            progress?.Report(ok
                ? $"Confirmed feature page URL: {url}"
                : $"Still not on expected form page (URL={url}).");
            return ok;
        }

        if (Regex.IsMatch(text, @"(?i)create new|add audit|open add|open create|record options"))
        {
            progress?.Report("Looking for Create New in Record Options…");
            var clicked = await ClickBestAsync(
                page,
                new[] { "Create New", "Create new", "Add Audit", "New Audit" },
                timing,
                preferSidePanel: true);
            progress?.Report(clicked ? "Opened Create New." : "Could not find Create New.");
            return clicked;
        }

        if (Regex.IsMatch(text, @"(?i)click save|plain save|save or submit"))
        {
            // Prefer exact Save on the form toolbar — avoid Modules menu noise.
            var clicked = await ClickBestAsync(page, new[] { "Save" }, timing, preferSidePanel: false);
            if (!clicked)
                clicked = await ClickBestAsync(page, new[] { "Submit", "Create", "OK" }, timing);
            return clicked;
        }

        if (Regex.IsMatch(text, @"(?i)leave required fields empty|invalid|trigger validation"))
        {
            await page.WaitForTimeoutAsync(Math.Max(800, timing.PostActionDelayMs / 2));
            return true;
        }

        if (Regex.IsMatch(text, @"(?i)observe whether"))
        {
            // Do not stop on a list page — for multi-select typing bugs, finish open/type/select/reopen.
            if (Regex.IsMatch(text, @"(?i)multi-select|assignee|previous typ|leaves previous"))
            {
                progress?.Report("Observe step: finishing multi-select interaction (do not stop on Persons list)…");
                if (!await DetectAssigneeMultiSelectPresentAsync(page))
                    await ClickPersonsRowActionAsync(page, timing, progress);
                await TypeIntoMultiSelectFilterAsync(page, "adm", timing, progress);
                await SelectFirstMultiSelectOptionAsync(page, timing, progress);
                await page.Keyboard.PressAsync("Escape");
                await page.WaitForTimeoutAsync(400);
                await OpenAssigneeMultiSelectAsync(page, timing, progress);
            }
            await page.WaitForTimeoutAsync(Math.Max(2500, timing.PostActionDelayMs));
            return true;
        }

        var openQuoted = Regex.Match(text, @"^(?i)open\s+['""](.+?)['""]");
        if (openQuoted.Success)
        {
            var label = openQuoted.Groups[1].Value;
            // "Proposal" is not a top-level button — open via grid record instead.
            if (label.Equals("Proposal", StringComparison.OrdinalIgnoreCase))
                return await OpenFirstGridRecordAsync(page, timing, progress);
            return await ClickBestAsync(page, ExpandLabels(label), timing);
        }

        var clickMatch = Regex.Match(text, @"(?i)\b(?:click|open|select|navigate to)\s+['""]?(.+?)['""]?\s*$");
        if (clickMatch.Success)
            return await ClickBestAsync(page, ExpandLabels(clickMatch.Groups[1].Value), timing);

        return await ClickBestAsync(page, ExpandLabels(text), timing);
    }

    private static async Task<bool> OpenFirstGridRecordAsync(IPage page, BrowserTimingOptions timing, IProgress<string>? progress)
    {
        // Auditor list rows link to /Auditor/Proposal/Index/{id}
        var linkCandidates = new[]
        {
            page.Locator("a.link-button"),
            page.Locator("table a[href*='Proposal']"),
            page.Locator("a[href*='/Auditor/Proposal']"),
            page.Locator("a[href*='Proposal/Index']"),
            page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { NameRegex = new Regex(@"AUD-|WR-|[A-Z]{2,}-\d+") })
        };

        foreach (var links in linkCandidates)
        {
            try
            {
                var count = await links.CountAsync();
                for (var i = 0; i < Math.Min(count, 8); i++)
                {
                    var link = links.Nth(i);
                    if (!await link.IsVisibleAsync())
                        continue;
                    var href = await link.GetAttributeAsync("href") ?? "";
                    progress?.Report($"Clicking grid record link ({href})…");
                    await link.ClickAsync(new LocatorClickOptions { Timeout = Math.Min(timing.ActionTimeoutMs, 15_000) });
                    await BrowserWaits.WaitForPageReadyAsync(page, timing);
                    return true;
                }
            }
            catch (PlaywrightException)
            {
                // try next selector family
            }
        }

        progress?.Report("No clickable audit/proposal grid row found.");
        return false;
    }

    private static async Task<bool> ClickPersonsRowActionAsync(IPage page, BrowserTimingOptions timing, IProgress<string>? progress)
    {
        // Prefer person-plus (edit Groups — opens MultiSelect). Fallback: Change Assignments.
        var selectors = new[]
        {
            ".editGroupsButtonColumn:not(.k-state-disabled)",
            "a.editGroupsButtonColumn",
            ".bi-person-plus",
            ".changeAssignmentsButtonColumn:not(.k-state-disabled)",
            "a.changeAssignmentsButtonColumn"
        };

        foreach (var sel in selectors)
        {
            try
            {
                var loc = page.Locator(sel).First;
                if (await loc.CountAsync() == 0 || !await loc.IsVisibleAsync())
                    continue;
                await loc.ClickAsync(new LocatorClickOptions { Timeout = Math.Min(timing.ActionTimeoutMs, 10_000) });
                await BrowserWaits.WaitForPageReadyAsync(page, timing);
                progress?.Report($"Clicked Persons row action ({sel}).");
                return true;
            }
            catch (PlaywrightException)
            {
                // try next
            }
        }

        progress?.Report("Could not click Persons person-plus / Change Assignments — trying generic icon buttons…");
        return await ClickBestAsync(page, new[] { "Change Assignments", "Groups", "Edit Groups" }, timing);
    }

    private static async Task<bool> DetectAssigneeMultiSelectPresentAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>(@"() => {
              const body = (document.body?.innerText || '').toLowerCase();
              if (body.includes('select assignees') || body.includes('filter assignees') || body.includes('groups'))
                return !!document.querySelector('.k-multiselect, #FilterAssignees, [data-role=""multiselect""]');
              return !!document.querySelector('#FilterAssignees, .k-multiselect, [data-role=""multiselect""]');
            }");
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> OpenAssigneeMultiSelectAsync(IPage page, BrowserTimingOptions timing, IProgress<string>? progress)
    {
        // Prefer dedicated FilterAssignees control on Recurring Activities scheduler.
        try
        {
            var filter = page.Locator("#FilterAssignees").First;
            if (await filter.CountAsync() > 0 && await filter.IsVisibleAsync())
            {
                await filter.ClickAsync(new LocatorClickOptions { Timeout = Math.Min(timing.ActionTimeoutMs, 10_000) });
                await page.WaitForTimeoutAsync(400);
                return true;
            }
        }
        catch (PlaywrightException) { /* fall through */ }

        var opened = await ClickBestAsync(
            page,
            new[] { "Select Assignees", "Assignees", "Assignee", "Add Team Members", "Team Members" },
            timing);
        if (!opened)
        {
            // Click the visible Kendo multi-select input area.
            try
            {
                var ms = page.Locator(".k-multiselect .k-input-inner, .k-multiselect .k-input, .k-multiselect").First;
                if (await ms.CountAsync() > 0)
                {
                    await ms.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                    opened = true;
                }
            }
            catch (PlaywrightException) { /* ignore */ }
        }

        progress?.Report(opened ? "Assignees multi-select focused." : "Could not open Assignees multi-select.");
        return opened;
    }

    private static async Task<bool> TypeIntoMultiSelectFilterAsync(
        IPage page, string text, BrowserTimingOptions timing, IProgress<string>? progress)
    {
        await OpenAssigneeMultiSelectAsync(page, timing, progress);
        try
        {
            var input = page.Locator(
                "#FilterAssignees-list + .k-list-container input, .k-multiselect .k-input-inner, .k-multiselect input.k-input, .k-animation-container input, .k-list-filter input").First;
            if (await input.CountAsync() > 0)
            {
                await input.FillAsync("");
                await input.PressSequentiallyAsync(text, new LocatorPressSequentiallyOptions { Delay = 40 });
            }
            else
            {
                await page.Keyboard.TypeAsync(text, new KeyboardTypeOptions { Delay = 40 });
            }

            await page.WaitForTimeoutAsync(700);
            progress?.Report($"Typed filter text '{text}' into multi-select.");
            return true;
        }
        catch (PlaywrightException ex)
        {
            progress?.Report("Could not type into multi-select filter: " + ex.Message);
            return false;
        }
    }

    private static async Task<bool> SelectFirstMultiSelectOptionAsync(
        IPage page, BrowserTimingOptions timing, IProgress<string>? progress)
    {
        try
        {
            var options = page.Locator(
                ".k-animation-container .k-list-item, .k-list-container .k-item, ul[id*='FilterAssignees'] li, .k-list .k-item");
            var count = await options.CountAsync();
            for (var i = 0; i < Math.Min(count, 10); i++)
            {
                var opt = options.Nth(i);
                if (!await opt.IsVisibleAsync())
                    continue;
                await opt.ClickAsync(new LocatorClickOptions { Timeout = Math.Min(timing.ActionTimeoutMs, 8_000) });
                await page.WaitForTimeoutAsync(500);
                progress?.Report("Selected a multi-select option.");
                return true;
            }
        }
        catch (PlaywrightException ex)
        {
            progress?.Report("Could not select multi-select option: " + ex.Message);
        }

        // Fallback: Enter on first filtered result.
        try
        {
            await page.Keyboard.PressAsync("ArrowDown");
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForTimeoutAsync(400);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> ExpandLabels(string raw)
    {
        var label = raw.Trim().Trim('\'', '"');
        yield return label;
        if (label.StartsWith("Add ", StringComparison.OrdinalIgnoreCase))
            yield return label["Add ".Length..].Trim();
        if (label.StartsWith("Create ", StringComparison.OrdinalIgnoreCase))
            yield return label["Create ".Length..].Trim();
        if (label.StartsWith("New ", StringComparison.OrdinalIgnoreCase))
            yield return label["New ".Length..].Trim();
        var parts = label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            yield return parts[^1];
    }

    private static Task<bool> ClickBestAsync(IPage page, IEnumerable<string> labels, BrowserTimingOptions timing) =>
        ClickBestAsync(page, labels, timing, preferSidePanel: false);

    /// <summary>
    /// Fast UI click: probe visible controls quickly. Never waits the full action timeout per missing label
    /// (that was freezing Step 3 on "Create New" for minutes).
    /// </summary>
    private static async Task<bool> ClickBestAsync(
        IPage page,
        IEnumerable<string> labels,
        BrowserTimingOptions timing,
        bool preferSidePanel)
    {
        var probeMs = Math.Clamp(timing.ProbeTimeoutMs, 800, 5_000);
        var labelList = labels.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Pass 1: instant visible scan (no long waits).
        foreach (var label in labelList)
        {
            if (await TryClickVisibleLabelAsync(page, label, timing.ActionTimeoutMs, preferSidePanel, waitForMs: 0))
                return true;
        }

        // Pass 2: short probes only — total bounded roughly by labels * probeMs, not actionTimeout * labels * roles.
        foreach (var label in labelList)
        {
            if (await TryClickVisibleLabelAsync(page, label, timing.ActionTimeoutMs, preferSidePanel, waitForMs: probeMs))
                return true;
        }

        return false;
    }

    private static async Task<bool> TryClickVisibleLabelAsync(
        IPage page,
        string label,
        int clickTimeoutMs,
        bool preferSidePanel,
        int waitForMs)
    {
        var roots = preferSidePanel
            ? new[]
            {
                page.Locator("#sidepanel"),
                page.Locator(".sidepanel"),
                page.Locator("[id*='RecordOptions' i]"),
                page.Locator("text=Record Options").Locator("xpath=ancestor::*[self::aside or self::div][1]"),
                page.Locator("body")
            }
            : new[] { page.Locator("body") };

        foreach (var root in roots)
        {
            ILocator[] candidates =
            [
                root.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = label, Exact = false }),
                root.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = label, Exact = false }),
                root.GetByRole(AriaRole.Menuitem, new LocatorGetByRoleOptions { Name = label, Exact = false }),
                root.GetByText(label, new LocatorGetByTextOptions { Exact = false })
            ];

            foreach (var loc in candidates)
            {
                try
                {
                    if (waitForMs > 0)
                    {
                        try
                        {
                            await loc.First.WaitForAsync(new LocatorWaitForOptions
                            {
                                State = WaitForSelectorState.Visible,
                                Timeout = waitForMs
                            });
                        }
                        catch (TimeoutException)
                        {
                            continue;
                        }
                        catch (PlaywrightException)
                        {
                            continue;
                        }
                    }

                    var count = await loc.CountAsync();
                    for (var i = 0; i < Math.Min(count, 6); i++)
                    {
                        var item = loc.Nth(i);
                        if (!await item.IsVisibleAsync())
                            continue;

                        await item.ClickAsync(new LocatorClickOptions { Timeout = Math.Min(clickTimeoutMs, 15_000) });
                        return true;
                    }
                }
                catch (PlaywrightException)
                {
                    // try next
                }
            }
        }

        return false;
    }
}
