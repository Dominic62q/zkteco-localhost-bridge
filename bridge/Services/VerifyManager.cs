using Bridge.Data;
using Bridge.Models;
using Bridge.Services;
using libzkfpcsharp;

namespace Bridge.Services;

/// <summary>
/// One-to-one verification: one live capture matched (DBMatch) against the
/// selected user's stored template. The SDK decides; React only displays.
/// </summary>
public sealed class VerifyManager
{
    private readonly object _gate = new();
    private readonly ZkFingerService _svc;
    private readonly Store _store;
    private readonly CaptureGate _cpGate;
    private readonly ILogger<VerifyManager> _log;
    private VerifySession? _active;
    private CancellationTokenSource? _cts;

    public VerifyManager(ZkFingerService svc, Store store, CaptureGate cpGate, ILogger<VerifyManager> log)
    {
        _svc = svc;
        _store = store;
        _cpGate = cpGate;
        _log = log;
    }

    public VerifySession Start(string userId)
    {
        var user = _store.GetUser(userId) ?? throw new KeyNotFoundException();
        var tpl = _store.LoadTemplate(userId) ?? throw new InvalidOperationException("NO_TEMPLATE");
        byte[] plain;
        try
        {
            plain = TemplateProtection.Unprotect(tpl.CipherBlob);
        }
        catch
        {
            throw new InvalidOperationException("NO_TEMPLATE");
        }
        lock (_gate)
        {
            if (_active is { IsTerminal: false })
                throw new BridgeBusyException();
            if (_svc.GetLiveDeviceCount() == 0)
                throw new DeviceNotFoundException();
            if (!_cpGate.TryAcquire("verify"))
                throw new BridgeBusyException();
            var s = new VerifySession
            {
                UserId = userId,
                Message = $"Place {user.FullName}'s enrolled finger on the reader.",
            };
            _active = s;
            _cts = new CancellationTokenSource();
            _svc.CaptureActive = true;
            var token = _cts.Token;
            Task.Run(() => RunSession(s, plain, token));
            return s;
        }
    }

    public VerifySession? Get(string id)
    {
        lock (_gate) return _active?.SessionId == id ? _active : null;
    }

    public VerifySession? Cancel(string id)
    {
        VerifySession? s;
        lock (_gate)
        {
            s = _active?.SessionId == id ? _active : null;
            if (s is null || s.IsTerminal) return s;
            s.Status = VerifyStatus.Cancelled;
            s.Message = "Verification cancelled.";
            _cts?.Cancel();
        }
        _store.Audit("verify_cancelled", s.UserId, false, null, null);
        return s;
    }

    private void RunSession(VerifySession s, byte[] stored, CancellationToken token)
    {
        IntPtr dev = IntPtr.Zero;
        IntPtr db = IntPtr.Zero;
        try
        {
            dev = _svc.OpenDevice(0);
            if (dev == IntPtr.Zero) { Fail(s, "DEVICE_OPEN_FAILED", "Could not open the reader."); return; }
            db = _svc.DbInit();
            if (db == IntPtr.Zero) { Fail(s, "DATABASE_ERROR", "Could not initialise matching."); return; }
            var (w, h) = _svc.GetImageSize(dev);
            var img = new byte[w * h];
            var cap = new byte[2048];

            while (true)
            {
                if (token.IsCancellationRequested || s.Status == VerifyStatus.Cancelled) return;
                if (DateTimeOffset.UtcNow >= s.ExpiresAt)
                {
                    lock (_gate)
                    {
                        if (!s.IsTerminal)
                        {
                            s.Status = VerifyStatus.TimedOut;
                            s.ErrorCode = "CAPTURE_TIMEOUT";
                            s.Message = "Timed out waiting for finger.";
                        }
                    }
                    _store.Audit("verify_timed_out", s.UserId, false, null, "CAPTURE_TIMEOUT");
                    return;
                }
                int cb = 2048;
                int ret;
                try
                {
                    ret = zkfp2.AcquireFingerprint(dev, img, cap, ref cb);
                }
                catch
                {
                    Thread.Sleep(200);
                    continue;
                }
                if (ret != zkfperrdef.ZKFP_ERR_OK)
                {
                    Thread.Sleep(200);
                    continue;
                }
                int score;
                lock (_gate)
                {
                    if (s.IsTerminal) return;
                    s.Status = VerifyStatus.Processing;
                    s.Message = "Matching…";
                    score = zkfp2.DBMatch(db, cap, stored);
                }
                var matched = score > 0;
                lock (_gate)
                {
                    s.Status = VerifyStatus.Completed;
                    s.Matched = matched;
                    s.Score = score;
                    s.ErrorCode = null;
                    s.Message = matched ? "Match confirmed." : "No match — different finger.";
                }
                _store.Audit(matched ? "verify_matched" : "verify_no_match",
                    s.UserId, matched, score, null);
                return;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Verify worker crashed");
            Fail(s, "UNEXPECTED_ERROR", "Unexpected error during verification.");
        }
        finally
        {
            Array.Clear(stored);
            if (db != IntPtr.Zero) _svc.DbFree(db);
            if (dev != IntPtr.Zero) _svc.CloseDevice(dev);
            _svc.CaptureActive = false;
            _cpGate.Release("verify");
        }
    }

    private void Fail(VerifySession s, string code, string message)
    {
        lock (_gate)
        {
            if (!s.IsTerminal)
            {
                s.Status = VerifyStatus.Failed;
                s.ErrorCode = code;
                s.Message = message;
            }
        }
        _store.Audit("verify_failed", s.UserId, false, null, code);
    }
}
