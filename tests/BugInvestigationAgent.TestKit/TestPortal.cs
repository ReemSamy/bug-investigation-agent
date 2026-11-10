using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BugInvestigationAgent.TestKit;

/// <summary>
/// A REAL local web application (Kestrel on 127.0.0.1) standing in for the KMS environments: login form, error
/// message, MFA page, signed-in home with a user menu, a state-changing form (POST) and a fetch DELETE.
/// Each environment lives under its own path (/qa, /rc, /prod) with its own users and session cookie path.
/// Every request is logged with the session's user, so tests can prove isolation from the server's point of view.
/// </summary>
public sealed class TestPortal : IAsyncDisposable
{
    public sealed record Hit(string Env, string Method, string Path, string? User, string? Session);

    private sealed class Session
    {
        public required string Env;
        public required string User;
        public bool MfaPending;
    }

    // env -> user -> (password, requires MFA)
    private static readonly Dictionary<string, Dictionary<string, (string Password, bool Mfa)>> Users = new()
    {
        ["qa"] = new() { ["alice_qa"] = ("qa-secret-A1!", true), ["carol_qa"] = ("qa-secret-C3!", false) },
        ["rc"] = new() { ["bob_rc"] = ("rc-secret-B2!", false) },
        ["prod"] = new() { ["viewer"] = ("prod-secret-V4!", false) }
    };
    public const string MfaCode = "135790";

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    public ConcurrentQueue<Hit> Hits { get; } = new();
    public string BaseUrl { get; private set; } = "";

    private TestPortal(WebApplication app) => _app = app;

    public static async Task<TestPortal> StartAsync(int port = 0)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        var portal = new TestPortal(app);
        portal.Map();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        portal.BaseUrl = address.TrimEnd('/');
        return portal;
    }

    public string EnvUrl(string env) => $"{BaseUrl}/{env}";

    private void Map()
    {
        _app.Use(async (http, next) =>
        {
            var parts = http.Request.Path.Value!.Trim('/').Split('/');
            var env = parts[0];
            var session = Current(http, env);
            Hits.Enqueue(new Hit(env, http.Request.Method, http.Request.Path.Value!, session?.MfaPending == false ? session.User : null,
                http.Request.Cookies[$"sid_{env}"]));
            await next();
        });

        foreach (var env in Users.Keys)
        {
            var e = env;
            _app.MapGet($"/{e}", (HttpContext http) => Home(http, e));
            _app.MapPost($"/{e}/login", async (HttpContext http) =>
            {
                var form = await http.Request.ReadFormAsync();
                var user = form["UserName"].ToString();
                var pass = form["Password"].ToString();
                if (!Users[e].TryGetValue(user, out var u) || u.Password != pass)
                    return Html(LoginPage(e, "Invalid username or password."));
                var sid = Guid.NewGuid().ToString("N");
                _sessions[sid] = new Session { Env = e, User = user, MfaPending = u.Mfa };
                http.Response.Cookies.Append($"sid_{e}", sid, new CookieOptions { Path = "/" + e, HttpOnly = true });
                return Results.Redirect(u.Mfa ? $"/{e}/mfa" : $"/{e}/");
            });
            _app.MapGet($"/{e}/mfa", (HttpContext http) =>
                Current(http, e) is { MfaPending: true } ? Html(MfaPage(e)) : Results.Redirect($"/{e}/"));
            _app.MapPost($"/{e}/mfa", async (HttpContext http) =>
            {
                var s = Current(http, e);
                var form = await http.Request.ReadFormAsync();
                if (s is null) return Results.Redirect($"/{e}/");
                if (form["code"] != MfaCode) return Html(MfaPage(e, "Wrong code."));
                s.MfaPending = false;
                return Results.Redirect($"/{e}/");
            });
            _app.MapGet($"/{e}/incident", (HttpContext http) => Authenticated(http, e) is { } user
                ? Html(Layout(e, user, "<h1>Incident</h1><div role=\"alert\" class=\"toast-error\">Cannot read properties of undefined (reading 'assignee')</div>"))
                : Html(LoginPage(e)));
            // State-changing endpoints: must NEVER be reached on a read-only environment.
            _app.MapPost($"/{e}/save", (HttpContext http) => Authenticated(http, e) is not null ? Html("<p id=\"saved\">Saved</p>") : Results.Unauthorized());
            _app.MapDelete($"/{e}/item/{{id}}", (HttpContext http) => Authenticated(http, e) is not null ? Results.Ok() : Results.Unauthorized());
        }
    }

    private IResult Home(HttpContext http, string env) =>
        Authenticated(http, env) is { } user
            ? Html(Layout(env, user, "<h1>My Assignments</h1>" +
                $"<a id=\"incident\" href=\"/{env}/incident\">Incidents</a>" +
                $"<form method=\"post\" action=\"/{env}/save\"><button id=\"save\" type=\"submit\">Save</button></form>" +
                $"<button id=\"delete\" onclick=\"fetch('/{env}/item/1',{{method:'DELETE'}})\">Delete</button>"))
            : Html(LoginPage(env));

    private Session? Current(HttpContext http, string env) =>
        http.Request.Cookies.TryGetValue($"sid_{env}", out var sid) && _sessions.TryGetValue(sid, out var s) && s.Env == env ? s : null;

    private string? Authenticated(HttpContext http, string env) => Current(http, env) is { MfaPending: false } s ? s.User : null;

    private static string LoginPage(string env, string? error = null) => Page(
        (error is null ? "" : $"<div role=\"alert\" class=\"validation-summary-errors\">{WebUtility.HtmlEncode(error)}</div>") +
        $"<form method=\"post\" action=\"/{env}/login\">" +
        "<input id=\"UserName\" name=\"UserName\" autocomplete=\"username\" />" +
        "<input id=\"Password\" name=\"Password\" type=\"password\" autocomplete=\"current-password\" />" +
        "<button type=\"submit\">Log in</button></form>");

    private static string MfaPage(string env, string? error = null) => Page(
        (error is null ? "" : $"<div role=\"alert\">{error}</div>") +
        $"<form method=\"post\" action=\"/{env}/mfa\"><label>Enter the verification code from your authenticator app</label>" +
        "<input name=\"code\" autocomplete=\"one-time-code\" inputmode=\"numeric\" /><button type=\"submit\">Verify</button></form>");

    private static string Layout(string env, string user, string body) =>
        Page($"<nav id=\"user-menu\">Signed in as {WebUtility.HtmlEncode(user)} ({env})</nav>{body}");

    private const string Style =
        "body{margin:0;font:14px/1.5 'Segoe UI',Roboto,sans-serif;color:#172b4d;background:#f4f5f7}" +
        "header.top{background:#0b4f8a;color:#fff;padding:12px 24px;font-weight:600;font-size:16px}" +
        "main{max-width:880px;margin:24px auto;background:#fff;border:1px solid #dfe1e6;border-radius:8px;padding:24px}" +
        "#user-menu{float:right;font-size:13px;color:#44546f;font-weight:600}" +
        "input{display:block;margin:6px 0 12px;padding:8px;width:280px;border:1px solid #c1c7d0;border-radius:4px}" +
        "button{padding:8px 14px;border:0;border-radius:4px;background:#0b4f8a;color:#fff;cursor:pointer;margin-right:8px}" +
        "a#incident{display:inline-block;margin:8px 0 16px;padding:8px 12px;background:#e9f2ff;border-radius:4px;text-decoration:none}" +
        "[role=alert]{background:#ffebe6;border:1px solid #de350b;color:#bf2600;padding:10px 14px;border-radius:4px;margin:12px 0;font-weight:600}";

    private static string Page(string body) =>
        $"<!doctype html><html><head><title>KMS test portal</title><style>{Style}</style></head><body><header class=\"top\">KMS (local test portal)</header><main>{body}</main></body></html>";

    private static IResult Html(string html) => Results.Content(html, "text/html");

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
