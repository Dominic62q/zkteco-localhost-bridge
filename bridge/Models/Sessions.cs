namespace Bridge.Models;

/// <summary>Enrolment session states (spec section 5.4).</summary>
public static class EnrolStatus
{
    public const string WaitingForFinger = "waiting_for_finger";
    public const string ScanReceived = "scan_received";
    public const string RemoveAndPlaceAgain = "remove_and_place_again";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string TimedOut = "timed_out";
}

public sealed class EnrolSession
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string FullName { get; init; } = "";
    public string? ExternalId { get; init; }
    public int RequiredScans { get; } = 3;
    public int CompletedScans;
    public string Status = EnrolStatus.WaitingForFinger;
    public string? Message;
    public string? ErrorCode;
    public string? UserId;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; } = DateTimeOffset.UtcNow.AddSeconds(30);

    // Working buffers, mirroring Demo2 (RegTmps[3], 2048-byte templates).
    public readonly byte[][] RegTmps = { new byte[2048], new byte[2048], new byte[2048] };
    public bool IsTerminal =>
        Status is EnrolStatus.Completed or EnrolStatus.Failed
            or EnrolStatus.Cancelled or EnrolStatus.TimedOut;
}

/// <summary>Verification session states (spec section 5.7).</summary>
public static class VerifyStatus
{
    public const string WaitingForFinger = "waiting_for_finger";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string TimedOut = "timed_out";
}

public sealed class VerifySession
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string UserId { get; init; } = "";
    public string Status = VerifyStatus.WaitingForFinger;
    public bool? Matched;
    public int? Score;
    public string? Message;
    public string? ErrorCode;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; } = DateTimeOffset.UtcNow.AddSeconds(30);

    public bool IsTerminal =>
        Status is VerifyStatus.Completed or VerifyStatus.Failed
            or VerifyStatus.Cancelled or VerifyStatus.TimedOut;
}
