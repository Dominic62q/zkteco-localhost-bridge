namespace Bridge.Models;

/// <summary>Per-app API token (v1 multi-app auth).</summary>
public sealed record ApiToken(string Name, string Token);
