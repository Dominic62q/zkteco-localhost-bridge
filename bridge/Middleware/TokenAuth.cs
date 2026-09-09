using System.Security.Cryptography;
using Bridge.Models;

namespace Bridge.Middleware;

/// <summary>
/// Per-app tokens for shared-bridge use. Tokens live in configuration
/// (appsettings.json `Bridge:ApiTokens`, generated per install).
/// No tokens configured = open mode (single-app dev/test).
/// `/api/health` and `/` stay open: they expose no biometric data.
/// </summary>
public sealed class TokenAuth
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _config;
    private readonly ILogger<TokenAuth> _log;

    public TokenAuth(RequestDelegate next, IConfiguration config, ILogger<TokenAuth> log)
    {
        _next = next;
        _config = config;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (HttpMethods.IsOptions(ctx.Request.Method) ||
            path == "/" || path == "/api/health" ||
            !(path.StartsWith("/api/", StringComparison.Ordinal)))
        {
            await _next(ctx);
            return;
        }


        var tokens = _config.GetSection("Bridge:ApiTokens").Get<List<ApiToken>>() ?? new();
        if (tokens.Count == 0)
        {
            await _next(ctx); // open mode
            return;
        }

        var presented = ctx.Request.Headers["X-Bridge-Token"].FirstOrDefault() ?? "";
        var match = tokens.FirstOrDefault(t =>
            t.Token.Length == presented.Length && CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(t.Token),
                System.Text.Encoding.UTF8.GetBytes(presented)));
        if (match is null)
        {
            _log.LogWarning("Rejected {Method} {Path}: bad or missing token", ctx.Request.Method, path);
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new
            {
                errorCode = "UNAUTHORIZED",
                message = "Valid X-Bridge-Token required. Pair this app in the bridge installer.",
            });
            return;
        }
        ctx.Items["BridgeApp"] = match.Name;
        await _next(ctx);
    }
}
