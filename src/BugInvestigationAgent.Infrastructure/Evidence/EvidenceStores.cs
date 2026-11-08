using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using BugInvestigationAgent.Core.Jobs;

namespace BugInvestigationAgent.Infrastructure.Evidence;

internal static class EvidenceFileNames
{
    /// <summary>Only plain file names of evidence types are accepted (no traversal, no arbitrary uploads).</summary>
    public static bool IsAllowed(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && fileName == Path.GetFileName(fileName)
        && !fileName.Contains("..", StringComparison.Ordinal)
        && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && Path.GetExtension(fileName).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".json";
}

/// <summary>Local folder evidence (standalone / single-machine). Files already live under the evidence root.</summary>
public sealed class LocalEvidenceStore : IEvidenceStore
{
    private readonly string _root;

    public LocalEvidenceStore(string root) => _root = Path.GetFullPath(root);

    public Task<string> PublishAsync(Guid jobId, string localDirectory, CancellationToken cancellationToken = default) =>
        Task.FromResult(Path.GetFullPath(localDirectory));

    public Task<Stream?> OpenReadAsync(Guid jobId, string fileName, CancellationToken cancellationToken = default)
    {
        if (!EvidenceFileNames.IsAllowed(fileName))
            return Task.FromResult<Stream?>(null);
        var dir = Path.Combine(_root, jobId.ToString("N"));
        var path = Path.GetFullPath(Path.Combine(dir, fileName));
        return Task.FromResult<Stream?>(path.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && File.Exists(path)
            ? File.OpenRead(path)
            : null);
    }
}

/// <summary>
/// Azure Blob Storage evidence (hosted). Private container; blobs under {jobId}/; access via managed identity only.
/// Evidence survives worker restarts and is readable by the report publisher on any replica.
/// </summary>
public sealed class BlobEvidenceStore : IEvidenceStore
{
    private readonly BlobContainerClient _container;

    public BlobEvidenceStore(BlobContainerClient container) => _container = container;

    public async Task<string> PublishAsync(Guid jobId, string localDirectory, CancellationToken cancellationToken = default)
    {
        await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
        var prefix = jobId.ToString("N") + "/";
        if (Directory.Exists(localDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(localDirectory))
            {
                var name = Path.GetFileName(file);
                if (!EvidenceFileNames.IsAllowed(name))
                    continue;
                await using var stream = File.OpenRead(file);
                await _container.GetBlobClient(prefix + name).UploadAsync(stream, overwrite: true, cancellationToken);
            }
        }

        return $"{_container.Uri}/{prefix}";
    }

    public async Task<Stream?> OpenReadAsync(Guid jobId, string fileName, CancellationToken cancellationToken = default)
    {
        if (!EvidenceFileNames.IsAllowed(fileName))
            return null;
        var blob = _container.GetBlobClient($"{jobId:N}/{fileName}");
        if (!await blob.ExistsAsync(cancellationToken))
            return null;
        var ms = new MemoryStream();
        await blob.DownloadToAsync(ms, cancellationToken);
        ms.Position = 0;
        return ms;
    }
}
