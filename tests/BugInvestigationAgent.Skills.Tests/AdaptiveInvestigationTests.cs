using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Skills.Jira;

namespace BugInvestigationAgent.Skills.Tests;

public class TicketUnderstandingSkillTests
{
    [Test]
    public void ParseIntent_reads_structured_json()
    {
        const string json = """
            {
              "actualProblem": "Text Response input missing",
              "expectedBehavior": "textarea shown",
              "actualBehavior": "empty field-wrapper",
              "affectedModule": "Investigator Checklists",
              "bugCategory": "UI rendering",
              "reproductionIntent": "Open checklist tab and observe response type 4",
              "keywords": ["Text Response", "Checklist", "field-wrapper"],
              "relevantEntities": ["Incident", "Add Question"],
              "missingInformation": ["exact environment build"],
              "investigationHints": ["Check response type renderer for type 4"]
            }
            """;

        var intent = TicketUnderstandingSkill.ParseIntent(json);

        Assert.That(intent.ActualProblem, Does.Contain("Text Response"));
        Assert.That(intent.AffectedModule, Is.EqualTo("Investigator Checklists"));
        Assert.That(intent.Keywords, Does.Contain("Checklist"));
        Assert.That(intent.InvestigationHints.Count, Is.EqualTo(1));
    }
}

public class HistoricalJiraJqlBuilderTests
{
    [Test]
    public void Builds_jql_from_intent_keywords_and_project()
    {
        var ctx = new RuntimeContext { JiraTicketKey = "VKMS-4167", Summary = "checklist text response" };
        var intent = new InvestigationIntent
        {
            AffectedModule = "Checklist",
            Keywords = { "Text", "Response", "field-wrapper" }
        };

        var jql = HistoricalJiraJqlBuilder.Build(ctx, intent);

        Assert.That(jql, Does.Contain("project = VKMS"));
        Assert.That(jql, Does.Contain("key != VKMS-4167"));
        Assert.That(jql, Does.Contain("Checklist").Or.Contain("Text").Or.Contain("Response"));
    }

    [Test]
    public void Jql_excludes_release_noise_tokens_from_summary()
    {
        var ctx = new RuntimeContext
        {
            JiraTicketKey = "VKMS-4241",
            Summary = "V14 - Beta - Multi select assignee screen leaves previous typing"
        };

        var jql = HistoricalJiraJqlBuilder.Build(ctx, intent: null);

        Assert.That(jql, Does.Not.Contain("text ~ \"V14\"").IgnoreCase);
        Assert.That(jql, Does.Not.Contain("text ~ \"Beta\"").IgnoreCase);
        Assert.That(jql, Does.Contain("assignee").Or.Contain("typing").Or.Contain("select").Or.Contain("Multi"));
    }
}

public class BugSimilarityTermsTests
{
    [Test]
    public void Keyword_overlap_ranks_same_problem_higher_than_unrelated_beta_sibling()
    {
        var ctx = new RuntimeContext
        {
            JiraTicketKey = "VKMS-4241",
            Summary = "V14 - Beta - Multi select assignee screen leaves previous typing"
        };
        var terms = BugSimilarityTerms.Extract(ctx, intent: null);

        var similar = BugSimilarityTerms.KeywordOverlapScore(
            terms,
            "Previous typed value remains after reopening the assignee selector",
            "multi select control keeps stale typing",
            null);

        var unrelated = BugSimilarityTerms.KeywordOverlapScore(
            terms,
            "V14 - Beta - Attachments - When adding attachment file, it overwrites the Name",
            "attachment name overwritten",
            null);

        Assert.That(similar, Is.GreaterThan(unrelated));
        Assert.That(unrelated, Is.LessThan(0.4));
    }
}

public class HistoricalJiraRankingTests
{
    [Test]
    public void ApplyRanking_maps_llm_json_to_similar_issues()
    {
        var ctx = new RuntimeContext();
        var hits = new List<JiraSearchHit>
        {
            new()
            {
                Key = "VKMS-3000",
                Summary = "Checklist text bug",
                Description = "empty control",
                ReproductionSteps = { "Open Administration", "Open Checklist" },
                Url = "https://example/browse/VKMS-3000"
            }
        };

        const string llm = """
            {
              "ranked": [
                {
                  "ticketKey": "VKMS-3000",
                  "similarity": "High",
                  "score": 0.91,
                  "reason": "Same checklist text response symptom",
                  "sameUnderlyingProblem": true,
                  "sameFunctionalArea": true,
                  "similarSymptoms": true,
                  "similarReproductionFlow": false,
                  "candidateReproductionSteps": ["Go to Administration", "Open Incident checklist"],
                  "candidateStepsApplicability": "Same admin checklist path; validate UI labels."
                }
              ]
            }
            """;

        HistoricalJiraSearchSkill.ApplyRanking(ctx, hits, llm);

        Assert.That(ctx.Investigation.SimilarIssues.Count, Is.EqualTo(1));
        Assert.That(ctx.Investigation.SimilarIssues[0].Key, Is.EqualTo("VKMS-3000"));
        Assert.That(ctx.Investigation.SimilarIssues[0].RelevanceScore, Is.EqualTo(0.91).Within(0.001));
        Assert.That(ctx.Investigation.SimilarIssues[0].SameUnderlyingProblem, Is.True);
        Assert.That(ctx.Investigation.SimilarIssues[0].SimilarityLevel, Is.EqualTo("High"));
        Assert.That(ctx.Investigation.SimilarIssues[0].CandidateSteps.Count, Is.EqualTo(2));
        Assert.That(ctx.Investigation.SimilarIssues[0].RankedByLlm, Is.True);
    }

    [Test]
    public void ApplyRanking_excludes_low_score_without_same_problem()
    {
        var ctx = new RuntimeContext();
        var hits = new List<JiraSearchHit>
        {
            new() { Key = "VKMS-4238", Summary = "Attachments overwrite Name" },
            new() { Key = "VKMS-9999", Summary = "Assignee typing persists" }
        };

        const string llm = """
            {
              "ranked": [
                {
                  "ticketKey": "VKMS-4238",
                  "similarity": "Low",
                  "score": 0.2,
                  "reason": "Only same V14 Beta epic",
                  "sameUnderlyingProblem": false,
                  "sameFunctionalArea": false
                },
                {
                  "ticketKey": "VKMS-9999",
                  "similarity": "High",
                  "score": 0.88,
                  "reason": "Same stale assignee input behavior",
                  "sameUnderlyingProblem": true,
                  "similarSymptoms": true
                }
              ]
            }
            """;

        HistoricalJiraSearchSkill.ApplyRanking(ctx, hits, llm, minSemanticScore: 0.55);

        Assert.That(ctx.Investigation.SimilarIssues.Count, Is.EqualTo(1));
        Assert.That(ctx.Investigation.SimilarIssues[0].Key, Is.EqualTo("VKMS-9999"));
    }
}

public class HistoricalReproductionReuseTests
{
    [Test]
    public void ApplyValidation_adopts_steps_when_confidence_high()
    {
        var ctx = new RuntimeContext();
        ctx.Investigation.CandidateHistoricalSteps.Add("Open Administration");
        const string llm = """
            {
              "compatible": true,
              "confidence": 0.85,
              "adoptedSteps": ["Open Administration", "Open Investigator Checklists"],
              "rationale": "Same admin checklist path"
            }
            """;

        HistoricalReproductionReuseSkill.ApplyValidation(ctx, llm, minConfidence: 0.7);

        Assert.That(ctx.Investigation.CandidateStepsValidatedAgainstTicket, Is.True);
        Assert.That(ctx.Investigation.AdoptedHistoricalSteps.Count, Is.EqualTo(2));
    }

    [Test]
    public void ApplyValidation_rejects_low_confidence()
    {
        var ctx = new RuntimeContext();
        const string llm = """
            { "compatible": true, "confidence": 0.2, "adoptedSteps": ["x"], "rationale": "weak" }
            """;

        HistoricalReproductionReuseSkill.ApplyValidation(ctx, llm, minConfidence: 0.7);

        Assert.That(ctx.Investigation.CandidateStepsValidatedAgainstTicket, Is.False);
        Assert.That(ctx.Investigation.AdoptedHistoricalSteps.Count, Is.EqualTo(0));
    }
}

public class TicketStepPlannerHistoricalAdoptionTests
{
    [Test]
    public void ResolveSteps_uses_validated_historical_steps_when_jira_has_none()
    {
        var ctx = new RuntimeContext
        {
            Summary = "Some admin checklist issue",
            Description = "No numbered steps here"
        };
        ctx.Investigation.CandidateStepsValidatedAgainstTicket = true;
        ctx.Investigation.CandidateStepsSourceKey = "VKMS-3000";
        ctx.Investigation.AdoptedHistoricalSteps.Add("Open Administration (app Administration area)");
        ctx.Investigation.AdoptedHistoricalSteps.Add("Open 'Investigator Checklists' / Checklist (Incident) inside Administration — not Pre-MOC");

        var steps = BugInvestigationAgent.Skills.Reproduction.TicketStepPlanner.ResolveSteps(ctx);

        Assert.That(ctx.CustomFields.ContainsKey("stepsFromHistoricalValidated"), Is.True);
        Assert.That(steps.Any(s => s.Contains("Administration", StringComparison.OrdinalIgnoreCase)), Is.True);
    }
}
