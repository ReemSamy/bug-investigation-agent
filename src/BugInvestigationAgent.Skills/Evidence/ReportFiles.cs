using System.Text.Json;
using BugInvestigationAgent.Core.Reporting;

namespace BugInvestigationAgent.Skills.Evidence;

/// <summary>
/// The structured investigation report on disk: <c>report.json</c> (machine-readable) and <c>report.md</c>
/// (human-readable) in the investigation's evidence folder, next to its screenshots. Files survive app restarts,
/// which is what makes past investigations retrievable in the local app.
/// </summary>
public static class ReportFiles
{
    public const string JsonFileName = "report.json";
    public const string MarkdownFileName = "report.md";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Write(string investigationDirectory, InvestigationReport report)
    {
        Directory.CreateDirectory(investigationDirectory);
        File.WriteAllText(Path.Combine(investigationDirectory, JsonFileName), JsonSerializer.Serialize(report, Json));
        File.WriteAllText(Path.Combine(investigationDirectory, MarkdownFileName), InvestigationReportBuilder.ToMarkdown(report));
    }

    /// <summary>Saved reports under the evidence root, newest first. Unreadable files are skipped, not fatal.</summary>
    public static IReadOnlyList<(string Folder, InvestigationReport Report)> List(string evidenceRoot, int max = 200)
    {
        if (!Directory.Exists(evidenceRoot))
            return Array.Empty<(string, InvestigationReport)>();

        var result = new List<(string, InvestigationReport)>();
        foreach (var dir in Directory.EnumerateDirectories(evidenceRoot))
        {
            var path = Path.Combine(dir, JsonFileName);
            if (!File.Exists(path))
                continue;
            try
            {
                var report = InvestigationReport.FromJson(File.ReadAllText(path));
                if (report is not null)
                    result.Add((Path.GetFileName(dir), report));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A partially written or corrupt report must not break the list.
            }
        }

        return result.OrderByDescending(r => r.Item2.GeneratedAt).Take(max).ToList();
    }

    /// <summary>"VKMS-5102_report.md" for a report file, derived from the saved report (falls back to the folder name).</summary>
    public static string DownloadName(string reportFilePath)
    {
        var ext = Path.GetExtension(reportFilePath);
        var dir = Path.GetDirectoryName(reportFilePath) ?? "";
        string key = Path.GetFileName(dir);
        try
        {
            var report = InvestigationReport.FromJson(File.ReadAllText(Path.Combine(dir, JsonFileName)));
            if (!string.IsNullOrWhiteSpace(report?.IssueKey))
                key = report.IssueKey;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        var safe = string.Concat(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return $"{safe}_report{ext}";
    }
}
