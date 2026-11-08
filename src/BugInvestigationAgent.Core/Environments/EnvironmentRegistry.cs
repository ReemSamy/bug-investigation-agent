using BugInvestigationAgent.Core.Context;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.Core.Environments;

/// <summary>
/// Resolved, immutable view of a registered (or ad-hoc) application environment.
/// </summary>
public sealed class AppEnvironment
{
    public const string AdHocPrefix = "adhoc:";

    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? ApplicationId { get; init; }
    public string? BaseUrl { get; init; }
    public EnvironmentType Type { get; init; }
    public bool Enabled { get; init; }
    public bool IsAdHoc { get; init; }
    /// <summary>Configuration allows a user's explicit confirmation to lift read-only protection (default false).</summary>
    public bool AllowUserConfirmedStateChanges { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    public required LoginFormOptions Login { get; init; }

    public bool IsProduction => Type == EnvironmentType.Production;

    /// <summary>Base URL with any user-info removed — safe for captions, logs and reports.</summary>
    public string? SafeBaseUrl => EnvironmentUrlSanitizer.StripCredentials(BaseUrl);

    public static string AdHocIdFor(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? AdHocPrefix + uri.Host.ToLowerInvariant()
            : AdHocPrefix + url.Trim().ToLowerInvariant();
}

public interface IEnvironmentRegistry
{
    /// <summary>All registered environments (enabled and disabled), ordered by application then display name.</summary>
    IReadOnlyList<AppEnvironment> List();
    AppEnvironment? Get(string environmentId);
    bool AllowAdHocUrl { get; }
    bool TreatAdHocAsProduction { get; }
    LoginFormOptions DefaultLogin { get; }
}

/// <summary>
/// Environment registry backed by configuration (<see cref="EnvironmentRegistryOptions"/>).
/// Uses <see cref="IOptionsMonitor{TOptions}"/> so edits to configuration files are picked up without a restart.
/// </summary>
public sealed class ConfigurationEnvironmentRegistry : IEnvironmentRegistry
{
    private readonly Func<EnvironmentRegistryOptions> _options;

    public ConfigurationEnvironmentRegistry(IOptionsMonitor<EnvironmentRegistryOptions> options)
        : this(() => options.CurrentValue)
    {
    }

    public ConfigurationEnvironmentRegistry(EnvironmentRegistryOptions options)
        : this(() => options)
    {
    }

    private ConfigurationEnvironmentRegistry(Func<EnvironmentRegistryOptions> options) => _options = options;

    public bool AllowAdHocUrl => _options().AllowAdHocUrl;
    public bool TreatAdHocAsProduction => _options().TreatAdHocAsProduction;
    public LoginFormOptions DefaultLogin => _options().DefaultLogin.WithDefaults();

    public IReadOnlyList<AppEnvironment> List()
    {
        var o = _options();
        return o.Environments
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .Select(kv => Map(kv.Key.Trim(), kv.Value, o.DefaultLogin))
            .OrderBy(e => e.ApplicationId ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public AppEnvironment? Get(string environmentId)
    {
        if (string.IsNullOrWhiteSpace(environmentId))
            return null;
        return List().FirstOrDefault(e => string.Equals(e.Id, environmentId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static AppEnvironment Map(string id, EnvironmentDefinitionOptions d, LoginFormOptions defaults) => new()
    {
        Id = id,
        DisplayName = string.IsNullOrWhiteSpace(d.DisplayName) ? id : d.DisplayName.Trim(),
        ApplicationId = string.IsNullOrWhiteSpace(d.ApplicationId) ? null : d.ApplicationId.Trim(),
        BaseUrl = string.IsNullOrWhiteSpace(d.BaseUrl) ? null : d.BaseUrl.Trim(),
        Type = d.Type,
        Enabled = d.Enabled,
        AllowUserConfirmedStateChanges = d.AllowUserConfirmedStateChanges,
        Aliases = d.Aliases.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList(),
        Metadata = new Dictionary<string, string>(d.Metadata, StringComparer.OrdinalIgnoreCase),
        Login = defaults.MergeWith(d.Login).WithDefaults()
    };
}

public static class EnvironmentUrlSanitizer
{
    /// <summary>Removes user-info (user:password@) and query string from a URL for display/evidence.</summary>
    public static string? StripCredentials(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return url.Trim();
        var builder = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
        var text = builder.Uri.GetLeftPart(UriPartial.Path);
        return text.EndsWith('/') && uri.AbsolutePath == "/" && !url.TrimEnd().EndsWith('/') ? text.TrimEnd('/') : text;
    }

    public static bool HasEmbeddedCredentials(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && !string.IsNullOrEmpty(uri.UserInfo);
}
