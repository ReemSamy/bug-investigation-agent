using System.Text.RegularExpressions;
using BugInvestigationAgent.Core.Context;

namespace BugInvestigationAgent.Core.Environments;

public sealed class EnvironmentSuggestion
{
    public string? EnvironmentId { get; init; }
    public string? ApplicationId { get; init; }
    public string? AdHocUrl { get; init; }
    public required string Reason { get; init; }
    public static EnvironmentSuggestion None(string reason) => new() { Reason = reason };
}

public sealed class EnvironmentValidationResult
{
    public bool IsValid { get; init; }
    public string? Error { get; init; }
    public AppEnvironment? Environment { get; init; }

    public static EnvironmentValidationResult Ok(AppEnvironment env) => new() { IsValid = true, Environment = env };
    public static EnvironmentValidationResult Fail(string error, AppEnvironment? env = null) => new() { IsValid = false, Error = error, Environment = env };
}

/// <summary>
/// Environment inference, validation and selection. Keeps environment logic out of the UI and the orchestrator,
/// and separate from credential handling and browser authentication.
/// </summary>
public interface IEnvironmentSelectionService
{
    IEnvironmentRegistry Registry { get; }

    /// <summary>Environments for the relevant application first (all registered environments when the application is unknown).</summary>
    IReadOnlyList<AppEnvironment> ListForApplication(string? applicationId);

    /// <summary>Infers a suggested environment from the ticket context. Never auto-confirms.</summary>
    EnvironmentSuggestion Suggest(RuntimeContext context);

    /// <summary>Records the suggestion on the context (SuggestedEnvironmentId / reason / ApplicationId).</summary>
    EnvironmentSuggestion ApplySuggestion(RuntimeContext context);

    EnvironmentValidationResult Validate(string? environmentId, string? adHocUrl = null);

    /// <summary>Validates and applies a user selection. On failure the context selection is cleared and the error recorded.</summary>
    EnvironmentValidationResult Select(RuntimeContext context, string? environmentId, string? adHocUrl = null);

    /// <summary>Re-resolves the environment currently selected on the context (null when nothing valid is selected).</summary>
    AppEnvironment? ResolveSelected(RuntimeContext context);

    /// <summary>Environment id named in a free-text reply (id, alias or display name). Null if none or ambiguous.</summary>
    string? MatchEnvironmentReply(string? reply);
}

public sealed class EnvironmentSelectionService : IEnvironmentSelectionService
{
    public const string AdHocOptionId = "adhoc";

    public EnvironmentSelectionService(IEnvironmentRegistry registry) => Registry = registry;

    public IEnvironmentRegistry Registry { get; }

    public IReadOnlyList<AppEnvironment> ListForApplication(string? applicationId)
    {
        var all = Registry.List();
        if (string.IsNullOrWhiteSpace(applicationId))
            return all;
        return all
            .OrderBy(e => string.Equals(e.ApplicationId, applicationId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
    }

    public EnvironmentSuggestion Suggest(RuntimeContext context)
    {
        var all = Registry.List();
        var application = ResolveApplication(context, all);
        var candidates = all.Where(e => e.Enabled).ToList();

        // 1) Ticket URL host matches a registered environment's Base URL.
        if (EnvironmentUrlParser.IsLikelyPortalEnvironmentUrl(context.EnvironmentUrl)
            && Uri.TryCreate(context.EnvironmentUrl, UriKind.Absolute, out var ticketUri))
        {
            var byHost = candidates.FirstOrDefault(e =>
                Uri.TryCreate(e.BaseUrl, UriKind.Absolute, out var u)
                && string.Equals(u.Host, ticketUri.Host, StringComparison.OrdinalIgnoreCase));
            if (byHost is not null)
            {
                return new EnvironmentSuggestion
                {
                    EnvironmentId = byHost.Id,
                    ApplicationId = byHost.ApplicationId ?? application,
                    Reason = $"The ticket links {ticketUri.Host}, which is the registered URL of {byHost.DisplayName}."
                };
            }
        }

        // 2) Ticket text names an environment ID or alias (URLs stripped so hosts like dev.azure.com don't match "DEV").
        var corpus = StripUrls(string.Join('\n', new[]
            {
                context.EnvironmentName,
                context.Summary,
                context.Description,
                context.FixVersion,
                string.Join('\n', context.Comments),
                string.Join('\n', context.CustomFields.Where(kv => !kv.Key.StartsWith("jira", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value))
            }.Where(s => !string.IsNullOrWhiteSpace(s))));

        var matches = candidates
            .Where(e => new[] { e.Id }.Concat(e.Aliases).Any(token => ContainsToken(corpus, token)))
            .ToList();
        if (matches.Count > 1 && application is not null)
        {
            var sameApp = matches.Where(e => string.Equals(e.ApplicationId, application, StringComparison.OrdinalIgnoreCase)).ToList();
            if (sameApp.Count > 0)
                matches = sameApp;
        }

        if (matches.Count == 1)
        {
            return new EnvironmentSuggestion
            {
                EnvironmentId = matches[0].Id,
                ApplicationId = matches[0].ApplicationId ?? application,
                Reason = $"The ticket mentions {matches[0].DisplayName}."
            };
        }

        if (matches.Count > 1)
        {
            return new EnvironmentSuggestion
            {
                ApplicationId = application,
                Reason = $"The ticket mentions several environments ({string.Join(", ", matches.Select(m => m.DisplayName))}). Choose the one to investigate."
            };
        }

        var disabledMention = all.Where(e => !e.Enabled)
            .FirstOrDefault(e => new[] { e.Id }.Concat(e.Aliases).Any(token => ContainsToken(corpus, token)));
        if (disabledMention is not null)
        {
            return new EnvironmentSuggestion
            {
                ApplicationId = disabledMention.ApplicationId ?? application,
                Reason = $"The ticket mentions {disabledMention.DisplayName}, but it is disabled in the environment registry. Choose another environment or enable it in configuration."
            };
        }

        // 3) Unregistered portal URL in the ticket → offer it as an ad-hoc environment (legacy behaviour).
        if (Registry.AllowAdHocUrl && EnvironmentUrlParser.IsLikelyPortalEnvironmentUrl(context.EnvironmentUrl))
        {
            return new EnvironmentSuggestion
            {
                EnvironmentId = AdHocOptionId,
                AdHocUrl = context.EnvironmentUrl,
                ApplicationId = application,
                Reason = $"The ticket links {EnvironmentUrlSanitizer.StripCredentials(context.EnvironmentUrl)}, which is not a registered environment."
            };
        }

        return EnvironmentSuggestion.None(all.Count == 0
            ? "No environments are registered yet (EnvironmentRegistry:Environments in configuration)."
            : "The ticket does not identify an environment. Select the environment to investigate.");
    }

    public EnvironmentSuggestion ApplySuggestion(RuntimeContext context)
    {
        var suggestion = Suggest(context);
        context.SuggestedEnvironmentId = suggestion.EnvironmentId;
        context.SuggestedEnvironmentReason = suggestion.Reason;
        if (!string.IsNullOrWhiteSpace(suggestion.ApplicationId))
            context.ApplicationId = suggestion.ApplicationId;
        if (!string.IsNullOrWhiteSpace(suggestion.AdHocUrl))
            context.SuggestedAdHocUrl = suggestion.AdHocUrl;
        return suggestion;
    }

    public EnvironmentValidationResult Validate(string? environmentId, string? adHocUrl = null)
    {
        if (string.IsNullOrWhiteSpace(environmentId))
            return EnvironmentValidationResult.Fail("Select the environment on which the bug should be investigated.");

        var id = environmentId.Trim();
        if (string.Equals(id, AdHocOptionId, StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith(AppEnvironment.AdHocPrefix, StringComparison.OrdinalIgnoreCase))
            return ValidateAdHoc(adHocUrl);

        var env = Registry.Get(id);
        if (env is null)
            return EnvironmentValidationResult.Fail(
                $"Environment '{id}' is not registered. Add it under EnvironmentRegistry:Environments:{id} in configuration, or choose another environment.");
        if (!env.Enabled)
            return EnvironmentValidationResult.Fail(
                $"Environment '{env.DisplayName}' is disabled in the registry. Choose another environment or enable it in configuration.", env);
        if (string.IsNullOrWhiteSpace(env.BaseUrl))
            return EnvironmentValidationResult.Fail(
                $"Environment '{env.DisplayName}' has no Base URL configured (EnvironmentRegistry:Environments:{env.Id}:BaseUrl). Reproduction cannot start until it is set.", env);
        if (EnvironmentUrlSanitizer.HasEmbeddedCredentials(env.BaseUrl))
            return EnvironmentValidationResult.Fail(
                $"The Base URL configured for '{env.DisplayName}' contains embedded credentials. Remove them from configuration; credentials are requested separately.", env);
        if (!IsHttpUrl(env.BaseUrl))
            return EnvironmentValidationResult.Fail(
                $"The Base URL configured for '{env.DisplayName}' is not a valid http(s) URL.", env);

        return EnvironmentValidationResult.Ok(env);
    }

    public EnvironmentValidationResult Select(RuntimeContext context, string? environmentId, string? adHocUrl = null)
    {
        var result = Validate(environmentId, adHocUrl);
        var previous = context.SelectedEnvironmentId;

        if (!result.IsValid || result.Environment is null)
        {
            context.SelectedEnvironmentId = null;
            context.EnvironmentSelectionConfirmed = false;
            context.EnvironmentSelectionError = result.Error;
            return result;
        }

        var env = result.Environment;
        context.SelectedEnvironmentId = env.Id;
        context.SelectedEnvironmentDisplayName = env.DisplayName;
        context.SelectedEnvironmentType = env.Type;
        context.EnvironmentName = env.DisplayName;
        context.EnvironmentUrl = env.BaseUrl;
        if (!string.IsNullOrWhiteSpace(env.ApplicationId))
            context.ApplicationId = env.ApplicationId;
        context.IsReadOnlyEnvironment = env.IsProduction;
        context.EnvironmentSelectionConfirmed = true;
        context.EnvironmentSelectionError = null;

        if (!string.Equals(previous, env.Id, StringComparison.OrdinalIgnoreCase))
        {
            // Never carry credentials, confirmations or an authenticated session across environments.
            context.CredentialProfileId = null;
            context.StateChangingActionsConfirmed = false;
            context.AuthenticationStatus = Authentication.AuthenticationStatus.NotStarted;
            context.AuthenticationMessage = null;
        }

        return result;
    }

    public AppEnvironment? ResolveSelected(RuntimeContext context)
    {
        if (string.IsNullOrWhiteSpace(context.SelectedEnvironmentId))
            return null;
        var id = context.SelectedEnvironmentId!;
        var result = id.StartsWith(AppEnvironment.AdHocPrefix, StringComparison.OrdinalIgnoreCase)
            ? ValidateAdHoc(context.EnvironmentUrl)
            : Validate(id);
        return result.IsValid ? result.Environment : null;
    }

    public string? MatchEnvironmentReply(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return null;
        var text = StripUrls(reply);
        var matches = Registry.List()
            .Where(e => new[] { e.Id, e.DisplayName }.Concat(e.Aliases).Any(t => ContainsToken(text, t)))
            .Select(e => e.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private EnvironmentValidationResult ValidateAdHoc(string? url)
    {
        if (!Registry.AllowAdHocUrl)
            return EnvironmentValidationResult.Fail("Manual environment URLs are disabled. Select a registered environment.");
        if (string.IsNullOrWhiteSpace(url))
            return EnvironmentValidationResult.Fail("Enter the environment URL to investigate.");
        if (EnvironmentUrlSanitizer.HasEmbeddedCredentials(url))
            return EnvironmentValidationResult.Fail("Do not put credentials in the URL. Enter the username and password in the credential fields instead.");

        var normalized = EnvironmentUrlParser.NormalizeUrl(url);
        if (EnvironmentUrlSanitizer.HasEmbeddedCredentials(normalized))
            return EnvironmentValidationResult.Fail("Do not put credentials in the URL. Enter the username and password in the credential fields instead.");
        if (!EnvironmentUrlParser.IsLikelyPortalEnvironmentUrl(normalized) || !IsHttpUrl(normalized))
            return EnvironmentValidationResult.Fail(
                "That URL is not an application environment (Azure DevOps, Jira and pull-request links are not portals). Enter the portal URL, e.g. https://….azurewebsites.net.");

        // Ad-hoc URL matching a registered environment → use the registered definition (its login settings and type).
        var host = new Uri(normalized!).Host;
        var registered = Registry.List().FirstOrDefault(e =>
            Uri.TryCreate(e.BaseUrl, UriKind.Absolute, out var u) && string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase));
        if (registered is not null)
            return Validate(registered.Id);

        var env = new AppEnvironment
        {
            Id = AppEnvironment.AdHocIdFor(normalized!),
            DisplayName = host,
            BaseUrl = normalized,
            Type = Registry.TreatAdHocAsProduction ? EnvironmentType.Production : EnvironmentType.Other,
            Enabled = true,
            IsAdHoc = true,
            Login = Registry.DefaultLogin
        };
        return EnvironmentValidationResult.Ok(env);
    }

    private static string? ResolveApplication(RuntimeContext context, IReadOnlyList<AppEnvironment> all)
    {
        if (!string.IsNullOrWhiteSpace(context.ApplicationId))
            return context.ApplicationId;
        if (string.IsNullOrWhiteSpace(context.SystemName))
            return null;
        return all.Select(e => e.ApplicationId)
            .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) && string.Equals(a, context.SystemName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    internal static bool ContainsToken(string corpus, string token)
    {
        if (string.IsNullOrWhiteSpace(corpus) || string.IsNullOrWhiteSpace(token))
            return false;
        var pattern = @"(?<![A-Za-z0-9])" + Regex.Escape(token.Trim()).Replace(@"\ ", @"[\s_\-]*") + @"(?![A-Za-z0-9])";
        return Regex.IsMatch(corpus, pattern, RegexOptions.IgnoreCase);
    }

    private static string StripUrls(string text) =>
        Regex.Replace(text, @"https?://\S+|\b[\w\-]+(\.[\w\-]+)+\.(net|com|org|io|local|test)\b\S*", " ", RegexOptions.IgnoreCase);

    private static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);
}
