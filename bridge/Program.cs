using Bridge.Data;
using Bridge.Middleware;
using Bridge.Models;
using Bridge.Services;
using libzkfpcsharp;
using Microsoft.AspNetCore.Cors.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Loopback only. Never 0.0.0.0 (spec section 5).
builder.WebHost.UseUrls("http://127.0.0.1:5050");
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(15);
    o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddRequestTimeouts(o =>
{
    o.DefaultPolicy = new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutPolicy { Timeout = TimeSpan.FromSeconds(15) };
});

builder.Services.AddSingleton<ZkFingerService>();
builder.Services.AddSingleton<CaptureGate>();
builder.Services.AddHostedService<DevicePoller>();
builder.Services.AddSingleton(sp =>
{
    var env = sp.GetRequiredService<IHostEnvironment>();
    var cfg = sp.GetRequiredService<IConfiguration>();
    return new Store(Db.Resolve(env.ContentRootPath, cfg), sp.GetRequiredService<ILogger<Store>>());
});
var allowedOrigins = builder.Configuration.GetSection("Bridge:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://127.0.0.1:5173", "http://localhost:5173" };
var cors = new CorsPolicyBuilder().AllowAnyMethod().AllowAnyHeader();
if (allowedOrigins.Contains("*"))
    cors.AllowAnyOrigin(); // loopback-only service: any local page may ask, but API tokens still gate biometric calls
else
    cors.WithOrigins(allowedOrigins);
builder.Services.AddCors(o => o.AddPolicy("frontend", cors.Build()));
builder.Logging.AddConsole();
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

var app = builder.Build();
app.UseRequestTimeouts();
app.UseMiddleware<TokenAuth>();
app.UseCors("frontend");

var svc = app.Services.GetRequiredService<ZkFingerService>();
var dbPath = Db.Resolve(app.Environment.ContentRootPath, app.Configuration);
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("bridge");
var store = app.Services.GetRequiredService<Store>();
var gate = app.Services.GetRequiredService<CaptureGate>();
var enrol = new EnrolManager(svc, store, gate, app.Services.GetRequiredService<ILogger<EnrolManager>>());
var verify = new VerifyManager(svc, store, gate, app.Services.GetRequiredService<ILogger<VerifyManager>>());
var capture = new CaptureService(svc, gate, app.Services.GetRequiredService<ILogger<CaptureService>>());
try
{
    Db.EnsureSchema(dbPath, log);
}
catch (Exception ex)
{
    // Never leak native details; keep it short for the API, full text in local log.
    log.LogError(ex, "SQLite init failed");
}
// GET / — human hint so opening the bare address explains itself.
app.MapGet("/", () => Results.Json(new
{
    bridge = "ok",
    message = "ZKTeco fingerprint bridge. UI: http://127.0.0.1:5173",
    endpoints = new[] { "/api/health", "/api/fingerprint/status" },
}));


// GET /api/health
app.MapGet("/api/health", () =>
{
    var sdkRet = svc.SdkRetCode;
    var sdk = svc.SdkOk ? "ok" : "error";
    var count = svc.GetDeviceCountSafe();
    var dbOk = Db.ProbeOk(dbPath);
    return Results.Json(new
    {
        bridge = "ok",
        sdk,
        sdkRet,
        database = dbOk ? "ok" : "error",
        deviceDetected = count > 0,
        deviceName = count > 0 ? ZkFingerService.DeviceModel : null,
        deviceCount = Math.Max(count, 0),
    });
});

// GET /api/fingerprint/status — must not crash when unplugged.
app.MapGet("/api/fingerprint/status", () =>
{
    var count = svc.GetDeviceCountSafe();
    var connected = count > 0;
    string? lastError = null;
    if (count < 0) lastError = connected ? null : "SDK_NOT_FOUND";
    return Results.Json(new
    {
        connected,
        model = connected ? ZkFingerService.DeviceModel : null,
        deviceCount = Math.Max(count, 0),
        busy = gate.Held,
        lastError,
    });
});

// ---- Enrolment (M3) ----

// POST /api/fingerprint/enrol/start
app.MapPost("/api/fingerprint/enrol/start", (EnrolStartReq req) =>
{
    if (req is null || string.IsNullOrWhiteSpace(req.FullName))
        return Results.Json(new { errorCode = "VALIDATION_ERROR", message = "Full name is required." }, statusCode: 400);
    try
    {
        var s = enrol.Start(req.FullName, req.ExternalId);
        return Results.Json(EnrolDto(s));
    }
    catch (BridgeBusyException)
    {
        return Results.Json(new { errorCode = "BRIDGE_BUSY", message = "Another capture session is active." }, statusCode: 409);
    }
    catch (DeviceNotFoundException)
    {
        return Results.Json(new { errorCode = "DEVICE_NOT_FOUND", message = "No fingerprint reader detected." }, statusCode: 503);
    }
});

// GET /api/fingerprint/enrol/{sessionId}
app.MapGet("/api/fingerprint/enrol/{sessionId}", (string sessionId) =>
{
    var s = enrol.Get(sessionId);
    return s is null
        ? Results.Json(new { errorCode = "SESSION_NOT_FOUND", message = "Unknown enrolment session." }, statusCode: 404)
        : Results.Json(EnrolDto(s));
});

// POST /api/fingerprint/enrol/{sessionId}/cancel
app.MapPost("/api/fingerprint/enrol/{sessionId}/cancel", (string sessionId) =>
{
    var s = enrol.Cancel(sessionId);
    return s is null
        ? Results.Json(new { errorCode = "SESSION_NOT_FOUND", message = "Unknown enrolment session." }, statusCode: 404)
        : Results.Json(EnrolDto(s));
});

// ---- Verification (M4) ----

// POST /api/fingerprint/verify/start
app.MapPost("/api/fingerprint/verify/start", (VerifyStartReq req) =>
{
    if (req is null || string.IsNullOrWhiteSpace(req.UserId))
        return Results.Json(new { errorCode = "VALIDATION_ERROR", message = "User selection is required." }, statusCode: 400);
    try
    {
        var s = verify.Start(req.UserId);
        return Results.Json(VerifyDto(s));
    }
    catch (KeyNotFoundException)
    {
        return Results.Json(new { errorCode = "USER_NOT_FOUND", message = "Unknown user." }, statusCode: 404);
    }
    catch (InvalidOperationException ex) when (ex.Message == "NO_TEMPLATE")
    {
        return Results.Json(new { errorCode = "NO_TEMPLATE", message = "No fingerprint stored for this user." }, statusCode: 404);
    }
    catch (BridgeBusyException)
    {
        return Results.Json(new { errorCode = "BRIDGE_BUSY", message = "Another capture session is active." }, statusCode: 409);
    }
    catch (DeviceNotFoundException)
    {
        return Results.Json(new { errorCode = "DEVICE_NOT_FOUND", message = "No fingerprint reader detected." }, statusCode: 503);
    }
});

// GET /api/fingerprint/verify/{sessionId}
app.MapGet("/api/fingerprint/verify/{sessionId}", (string sessionId) =>
{
    var s = verify.Get(sessionId);
    return s is null
        ? Results.Json(new { errorCode = "SESSION_NOT_FOUND", message = "Unknown verification session." }, statusCode: 404)
        : Results.Json(VerifyDto(s));
});

// POST /api/fingerprint/verify/{sessionId}/cancel
app.MapPost("/api/fingerprint/verify/{sessionId}/cancel", (string sessionId) =>
{
    var s = verify.Cancel(sessionId);
    return s is null
        ? Results.Json(new { errorCode = "SESSION_NOT_FOUND", message = "Unknown verification session." }, statusCode: 404)
        : Results.Json(VerifyDto(s));
});

static object VerifyDto(VerifySession s) => new
{
    s.SessionId,
    operation = "verify",
    s.Status,
    s.Matched,
    s.Score,
    s.UserId,
    s.Message,
    s.ErrorCode,
    expiresAt = s.ExpiresAt,
    secondsLeft = Math.Max(0, (int)(s.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds),
};

// ---- Users ----

// GET /api/users
app.MapGet("/api/users", () => Results.Json(store.ListUsers().Select(u => new
{
    id = u.Id,
    fullName = u.FullName,
    externalId = u.ExternalId,
    createdAt = u.CreatedAt,
})));

// GET /api/users/{userId}
app.MapGet("/api/users/{userId}", (string userId) =>
{
    var u = store.GetUser(userId);
    return u is null
        ? Results.Json(new { errorCode = "USER_NOT_FOUND", message = "Unknown user." }, statusCode: 404)
        : Results.Json(new { id = u.Id, fullName = u.FullName, externalId = u.ExternalId, createdAt = u.CreatedAt });
});

// DELETE /api/users/{userId} — removes user and template together.
app.MapDelete("/api/users/{userId}", (string userId) =>
{
    if (!store.DeleteUser(userId))
        return Results.Json(new { errorCode = "USER_NOT_FOUND", message = "Unknown user." }, statusCode: 404);
    store.Audit("user_deleted", userId, true, null, null);
    return Results.Json(new { deleted = true, userId });
});

// ---- Generic v1 API (any authorized web app; no users, no storage) ----

// GET /api/v1/device/status
app.MapGet("/api/v1/device/status", () =>
{
    var count = svc.GetDeviceCountSafe();
    return Results.Json(new
    {
        connected = count > 0,
        model = count > 0 ? ZkFingerService.DeviceModel : null,
        deviceCount = Math.Max(count, 0),
        busy = gate.Held,
    });
});

// POST /api/v1/capture — one live template, base64 out. App stores it.
app.MapPost("/api/v1/capture", (V1CaptureReq? req) =>
{
    try
    {
        var (tpl, _) = capture.Capture(req is null ? 30 : req.TimeoutSeconds);
        return Results.Json(new
        {
            templateBase64 = Convert.ToBase64String(tpl),
            algorithm = "zkfp2",
        });
    }
    catch (BridgeBusyException)
    {
        return Results.Json(new { errorCode = "BRIDGE_BUSY", message = "Another capture session is active." }, statusCode: 409);
    }
    catch (DeviceNotFoundException)
    {
        return Results.Json(new { errorCode = "DEVICE_NOT_FOUND", message = "No fingerprint reader detected." }, statusCode: 503);
    }
    catch (TimeoutException)
    {
        return Results.Json(new { errorCode = "CAPTURE_TIMEOUT", message = "Timed out waiting for finger." }, statusCode: 408);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { errorCode = ex.Message, message = "Capture failed." }, statusCode: 500);
    }
}).DisableRequestTimeout();

// POST /api/v1/merge — merge three captures (base64 in) into one template.
app.MapPost("/api/v1/merge", (V1MergeReq? req) =>
{
    if (req?.Templates is not { Length: 3 })
        return Results.Json(new { errorCode = "VALIDATION_ERROR", message = "Exactly three capture templates are required." }, statusCode: 400);
    try
    {
        var parts = req.Templates.Select(t =>
        {
            try { return Convert.FromBase64String(t); }
            catch { throw new ArgumentException("BAD_TEMPLATE"); }
        }).ToArray();
        var merged = capture.Merge(parts);
        return Results.Json(new
        {
            templateBase64 = Convert.ToBase64String(merged),
            algorithm = "zkfp2",
        });
    }
    catch (ArgumentException ex) when (ex.Message == "BAD_TEMPLATE")
    {
        return Results.Json(new { errorCode = "BAD_TEMPLATE", message = "A supplied template is not valid base64." }, statusCode: 400);
    }
    catch (ArgumentException ex)
    {
        return Results.Json(new { errorCode = "VALIDATION_ERROR", message = ex.Message }, statusCode: 400);
    }
    catch (BridgeBusyException)
    {
        return Results.Json(new { errorCode = "BRIDGE_BUSY", message = "Another capture session is active." }, statusCode: 409);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { errorCode = ex.Message, message = "Merge failed." }, statusCode: 500);
    }
});

// POST /api/v1/verify — live capture matched against a caller-supplied template.
app.MapPost("/api/v1/verify", (V1VerifyReq? req) =>
{
    if (string.IsNullOrWhiteSpace(req?.TemplateBase64))
        return Results.Json(new { errorCode = "VALIDATION_ERROR", message = "A reference template is required." }, statusCode: 400);
    byte[] reference;
    try
    {
        reference = Convert.FromBase64String(req.TemplateBase64);
    }
    catch
    {
        return Results.Json(new { errorCode = "BAD_TEMPLATE", message = "Reference template is not valid base64." }, statusCode: 400);
    }
    try
    {
        var (matched, score) = capture.Verify(reference, req is null ? 30 : req.TimeoutSeconds);
        return Results.Json(new { matched, score });
    }
    catch (BridgeBusyException)
    {
        return Results.Json(new { errorCode = "BRIDGE_BUSY", message = "Another capture session is active." }, statusCode: 409);
    }
    catch (DeviceNotFoundException)
    {
        return Results.Json(new { errorCode = "DEVICE_NOT_FOUND", message = "No fingerprint reader detected." }, statusCode: 503);
    }
    catch (TimeoutException)
    {
        return Results.Json(new { errorCode = "CAPTURE_TIMEOUT", message = "Timed out waiting for finger." }, statusCode: 408);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { errorCode = ex.Message, message = "Verification failed." }, statusCode: 500);
    }
}).DisableRequestTimeout();

// POST /api/v1/identify — one live capture searched over caller-supplied
// candidates [{id, templateBase64}]. Returns {matched, matchId, score}.
app.MapPost("/api/v1/identify", (V1IdentifyReq? req) =>
{
    if (req?.Candidates is not { Length: >= 1 })
        return Results.Json(new { errorCode = "VALIDATION_ERROR", message = "At least one candidate template is required." }, statusCode: 400);
    if (req.Candidates.Length > 500)
        return Results.Json(new { errorCode = "VALIDATION_ERROR", message = "At most 500 candidates per call." }, statusCode: 400);
    List<(string Id, byte[] Template)> cands = new();
    foreach (var c in req.Candidates)
    {
        if (string.IsNullOrWhiteSpace(c?.Id) || string.IsNullOrWhiteSpace(c?.TemplateBase64))
            return Results.Json(new { errorCode = "VALIDATION_ERROR", message = "Every candidate needs an id and a template." }, statusCode: 400);
        try
        {
            cands.Add((c.Id, Convert.FromBase64String(c.TemplateBase64)));
        }
        catch
        {
            return Results.Json(new { errorCode = "BAD_TEMPLATE", message = $"Candidate '{c.Id}' is not valid base64." }, statusCode: 400);
        }
    }
    try
    {
        var (matchId, score) = capture.Identify(cands, req.TimeoutSeconds);
        return Results.Json(new { matched = matchId is not null, matchId, score });
    }
    catch (BridgeBusyException)
    {
        return Results.Json(new { errorCode = "BRIDGE_BUSY", message = "Another capture session is active." }, statusCode: 409);
    }
    catch (DeviceNotFoundException)
    {
        return Results.Json(new { errorCode = "DEVICE_NOT_FOUND", message = "No fingerprint reader detected." }, statusCode: 503);
    }
    catch (TimeoutException)
    {
        return Results.Json(new { errorCode = "CAPTURE_TIMEOUT", message = "Timed out waiting for finger." }, statusCode: 408);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { errorCode = ex.Message, message = "Identification failed." }, statusCode: 500);
    }
}).DisableRequestTimeout();

// POST /api/v1/identify-template — match an already-captured template
// {templateBase64} over caller-supplied candidates [{id, templateBase64}].
// No finger press, no device use (duplicate checks at enrolment).
// Returns {matched, matchId, score}.
app.MapPost("/api/v1/identify-template", (V1IdentifyTemplateReq? req) =>
{
    if (string.IsNullOrWhiteSpace(req?.TemplateBase64))
        return Results.Json(new { errorCode = "VALIDATION_ERROR", message = "A template to match is required." }, statusCode: 400);
    byte[] template;
    try
    {
        template = Convert.FromBase64String(req.TemplateBase64);
    }
    catch
    {
        return Results.Json(new { errorCode = "BAD_TEMPLATE", message = "Template is not valid base64." }, statusCode: 400);
    }
    if (template.Length == 0)
        return Results.Json(new { errorCode = "BAD_TEMPLATE", message = "Template is empty." }, statusCode: 400);
    if (req?.Candidates is not { Length: >= 1 })
        return Results.Json(new { errorCode = "VALIDATION_ERROR", message = "At least one candidate template is required." }, statusCode: 400);
    if (req.Candidates.Length > 500)
        return Results.Json(new { errorCode = "VALIDATION_ERROR", message = "At most 500 candidates per call." }, statusCode: 400);
    List<(string Id, byte[] Template)> cands = new();
    foreach (var c in req.Candidates)
    {
        if (string.IsNullOrWhiteSpace(c?.Id) || string.IsNullOrWhiteSpace(c?.TemplateBase64))
            return Results.Json(new { errorCode = "VALIDATION_ERROR", message = "Every candidate needs an id and a template." }, statusCode: 400);
        try
        {
            cands.Add((c.Id, Convert.FromBase64String(c.TemplateBase64)));
        }
        catch
        {
            return Results.Json(new { errorCode = "BAD_TEMPLATE", message = $"Candidate '{c.Id}' is not valid base64." }, statusCode: 400);
        }
    }
    try
    {
        var (matchId, score) = capture.IdentifyTemplate(template, cands);
        return Results.Json(new { matched = matchId is not null, matchId, score });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { errorCode = ex.Message, message = "Identification failed." }, statusCode: 500);
    }
}).DisableRequestTimeout();

static object EnrolDto(EnrolSession s) => new
{
    s.SessionId,
    operation = "enrol",
    s.Status,
    s.RequiredScans,
    s.CompletedScans,
    s.Message,
    s.ErrorCode,
    s.UserId,
    expiresAt = s.ExpiresAt,
    secondsLeft = Math.Max(0, (int)(s.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds),
};


app.Lifetime.ApplicationStopping.Register(() =>
{
    try { svc.Release(); } catch { /* shutting down */ }
});

app.Run();

sealed record EnrolStartReq(string? FullName, string? ExternalId);
sealed record VerifyStartReq(string? UserId);
sealed record V1Candidate(string? Id, string? TemplateBase64);
sealed record V1CaptureReq(int TimeoutSeconds = 30);
sealed record V1MergeReq(string[]? Templates);
sealed record V1VerifyReq(string? TemplateBase64, int TimeoutSeconds = 30);
sealed record V1IdentifyReq(V1Candidate[]? Candidates, int TimeoutSeconds = 30);
sealed record V1IdentifyTemplateReq(string? TemplateBase64, V1Candidate[]? Candidates);
