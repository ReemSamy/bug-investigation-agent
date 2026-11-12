using System.Text.Json;
using System.Text.Json.Serialization;
using AI.BugInvestigationTool.Configuration;
using AI.BugInvestigationTool.Models;
using Microsoft.Extensions.Options;

namespace AI.BugInvestigationTool.Storage;

/// <summary>
/// Lightweight persistence: one folder per investigation under the evidence root containing
/// investigation.json, report.md and screenshots/attachments. No database, no credentials.
/// </summary>
public sealed class InvestigationStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _root;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public InvestigationStore(IOptions<ToolOptions> options)
    {
        _root = ResolveRoot(options.Value.EvidenceRoot);
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// A relative evidence root resolves against the solution folder (found by walking up from the binaries),
    /// so the CLI and the web UI share one Evidence folder regardless of the working directory.
    /// </summary>
    internal static string ResolveRoot(string configured)
    {
        if (Path.IsPathRooted(configured)) return Path.GetFullPath(configured);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AI.BugInvestigationTool.sln")))
                return Path.Combine(dir.FullName, configured);
        return Path.GetFullPath(configured);
    }

    public string Root => _root;

    public static string NewId(InvestigationTarget target)
    {
        var name = target.Kind == TargetKind.JiraIssue ? target.JiraKey! : $"PR-{target.PullRequestId}";
        return $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{name}-{Guid.NewGuid().ToString("N")[..4]}";
    }

    /// <summary>Folder of an investigation. The id is validated so it can never escape the evidence root.</summary>
    public string FolderOf(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Invalid investigation id.", nameof(id));
        return Path.Combine(_root, id);
    }

    public async Task SaveAsync(Investigation investigation, CancellationToken ct = default)
    {
        var folder = FolderOf(investigation.Id);
        Directory.CreateDirectory(folder);
        var json = JsonSerializer.Serialize(investigation, Json);
        await _lock.WaitAsync(ct);
        try
        {
            var tmp = Path.Combine(folder, "investigation.json.tmp");
            await File.WriteAllTextAsync(tmp, json, ct);
            File.Move(tmp, Path.Combine(folder, "investigation.json"), overwrite: true);
        }
        finally { _lock.Release(); }
    }

    public async Task<Investigation?> LoadAsync(string id, CancellationToken ct = default)
    {
        var file = Path.Combine(FolderOf(id), "investigation.json");
        if (!File.Exists(file)) return null;
        await using var s = File.OpenRead(file);
        return await JsonSerializer.DeserializeAsync<Investigation>(s, Json, ct);
    }

    public async Task<List<Investigation>> ListRecentAsync(int max = 25, CancellationToken ct = default)
    {
        var result = new List<Investigation>();
        foreach (var dir in Directory.EnumerateDirectories(_root).OrderByDescending(Path.GetFileName).Take(max))
        {
            try
            {
                var inv = await LoadAsync(Path.GetFileName(dir), ct);
                if (inv is not null) result.Add(inv);
            }
            catch (JsonException) { /* skip corrupt folders */ }
        }
        return result;
    }

    public Task WriteTextAsync(string id, string fileName, string content, CancellationToken ct = default) =>
        File.WriteAllTextAsync(Path.Combine(FolderOf(id), fileName), content, ct);

    /// <summary>Resolves an evidence file path, refusing anything outside the investigation folder.</summary>
    public string? ResolveEvidenceFile(string id, string relative)
    {
        var folder = FolderOf(id);
        var full = Path.GetFullPath(Path.Combine(folder, relative));
        return full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }
}
