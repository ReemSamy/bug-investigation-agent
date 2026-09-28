namespace BugInvestigationAgent.Core.Context;

/// <summary>
/// LLM-interpreted investigation intent. Raw Jira fields remain the source of truth.
/// </summary>
public sealed class InvestigationIntent
{
    public string? ActualProblem { get; set; }
    public string? ExpectedBehavior { get; set; }
    public string? ActualBehavior { get; set; }
    public string? AffectedModule { get; set; }
    public string? BugCategory { get; set; }
    public string? ReproductionIntent { get; set; }
    public IList<string> Keywords { get; set; } = new List<string>();
    public IList<string> RelevantEntities { get; set; } = new List<string>();
    public IList<string> MissingInformation { get; set; } = new List<string>();
    public IList<string> InvestigationHints { get; set; } = new List<string>();
    public string? RawLlmResponse { get; set; }
    public bool GeneratedByLlm { get; set; }
}

/// <summary>How similar-issue ranking was produced.</summary>
public static class SimilarIssueRankingModes
{
    public const string LlmSemantic = "LlmSemantic";
    public const string KeywordJqlFallback = "KeywordJqlFallback";
    public const string None = "None";
}

/// <summary>Historical Jira issue ranked as potentially relevant.</summary>
public sealed class SimilarIssueCandidate
{
    public string Key { get; set; } = "";
    public string? Summary { get; set; }
    public string? Status { get; set; }
    public string? DescriptionExcerpt { get; set; }
    /// <summary>Human-readable explanation of why this bug is (or is not strongly) similar.</summary>
    public string? WhyRelevant { get; set; }
    /// <summary>0–1 similarity score (semantic when LLM ranked; capped keyword overlap otherwise).</summary>
    public double RelevanceScore { get; set; }
    /// <summary>High / Medium / Low / Unrelated — derived from score and LLM flags.</summary>
    public string SimilarityLevel { get; set; } = "Low";
    public bool SameUnderlyingProblem { get; set; }
    public bool SameFunctionalArea { get; set; }
    public bool SimilarSymptoms { get; set; }
    public bool SimilarReproductionFlow { get; set; }
    /// <summary>True when ranking came from LLM semantic comparison.</summary>
    public bool RankedByLlm { get; set; }
    /// <summary>Why candidate reproduction steps may apply (never auto-executed).</summary>
    public string? CandidateStepsApplicability { get; set; }
    public IList<string> CandidateSteps { get; set; } = new List<string>();
    public string? Url { get; set; }
}

/// <summary>Chunk of product documentation retrieved for the current investigation.</summary>
public sealed class RetrievedDocumentChunk
{
    public string SourcePath { get; set; } = "";
    public string? Title { get; set; }
    public string Text { get; set; } = "";
    public double Score { get; set; }
    public string? ModuleHint { get; set; }
}

/// <summary>
/// Cross-stage investigation bundle flowing through the pipeline.
/// Existing RuntimeContext Jira/repro/fix fields remain authoritative for those domains.
/// </summary>
public sealed class InvestigationBundle
{
    public InvestigationIntent? Intent { get; set; }
    public IList<SimilarIssueCandidate> SimilarIssues { get; set; } = new List<SimilarIssueCandidate>();
    /// <summary><see cref="SimilarIssueRankingModes"/> value shown in the UI.</summary>
    public string SimilarIssuesRankingMode { get; set; } = SimilarIssueRankingModes.None;
    /// <summary>Short banner explaining ranking quality (semantic vs keyword fallback).</summary>
    public string? SimilarIssuesRankingNote { get; set; }
    public IList<string> CandidateHistoricalSteps { get; set; } = new List<string>();
    public bool CandidateStepsValidatedAgainstTicket { get; set; }
    public double CandidateStepsConfidence { get; set; }
    public string? CandidateStepsSourceKey { get; set; }
    public IList<string> AdoptedHistoricalSteps { get; set; } = new List<string>();
    public IList<RetrievedDocumentChunk> KmsDocuments { get; set; } = new List<RetrievedDocumentChunk>();
    public string? BrowserObservationsSummary { get; set; }
    public string? EvidenceReasoning { get; set; }
    public string? PotentialImpact { get; set; }

    public void Clear()
    {
        Intent = null;
        SimilarIssues.Clear();
        SimilarIssuesRankingMode = SimilarIssueRankingModes.None;
        SimilarIssuesRankingNote = null;
        CandidateHistoricalSteps.Clear();
        CandidateStepsValidatedAgainstTicket = false;
        CandidateStepsConfidence = 0;
        CandidateStepsSourceKey = null;
        AdoptedHistoricalSteps.Clear();
        KmsDocuments.Clear();
        BrowserObservationsSummary = null;
        EvidenceReasoning = null;
        PotentialImpact = null;
    }
}
