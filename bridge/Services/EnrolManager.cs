using Bridge.Data;
using Bridge.Models;
using Bridge.Services;
using libzkfpcsharp;

namespace Bridge.Services;

public sealed class BridgeBusyException : Exception;
public sealed class DeviceNotFoundException : Exception;

/// <summary>
/// One enrolment session at a time. Capture runs on a worker task mirroring
/// Demo2's DoCapture/DefWndProc flow: duplicate check (DBIdentify), same-finger
/// check (DBMatch), 3 captures, DBMerge, DBAdd, encrypted persist.
/// </summary>
public sealed class EnrolManager
{
    private readonly object _gate = new();
    private readonly ZkFingerService _svc;
    private readonly Store _store;
    private readonly CaptureGate _cpGate;
    private readonly ILogger<EnrolManager> _log;
    private EnrolSession? _active;
    private CancellationTokenSource? _cts;

    public EnrolManager(ZkFingerService svc, Store store, CaptureGate cpGate, ILogger<EnrolManager> log)
    {
        _svc = svc;
        _store = store;
        _cpGate = cpGate;
        _log = log;
    }

    public bool HasActive
    {
        get { lock (_gate) return _active is { IsTerminal: false }; }
    }

    public EnrolSession Start(string fullName, string? externalId)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            throw new ArgumentException("Full name is required.", nameof(fullName));
        lock (_gate)
        {
            if (_active is { IsTerminal: false })
                throw new BridgeBusyException();
            if (_svc.GetLiveDeviceCount() == 0)
                throw new DeviceNotFoundException();
            if (!_cpGate.TryAcquire("enrol"))
                throw new BridgeBusyException();
            var s = new EnrolSession
            {
                FullName = fullName.Trim(),
                ExternalId = string.IsNullOrWhiteSpace(externalId) ? null : externalId.Trim(),
                UserId = Guid.NewGuid().ToString("N"),
                Message = "Place your finger on the reader.",
            };
            _active = s;
            _cts = new CancellationTokenSource();
            _svc.CaptureActive = true;
            var token = _cts.Token;
            Task.Run(() => RunSession(s, token));
            return s;
        }
    }

    public EnrolSession? Get(string id)
    {
        lock (_gate) return _active?.SessionId == id ? _active : null;
    }

    public EnrolSession? Cancel(string id)
    {
        EnrolSession? s;
        lock (_gate)
        {
            s = _active?.SessionId == id ? _active : null;
            if (s is null || s.IsTerminal) return s;
            s.Status = EnrolStatus.Cancelled;
            s.Message = "Enrolment cancelled.";
            _cts?.Cancel();
        }
        _store.Audit("enrol_cancelled", s.UserId, false, null, null);
        return s;
    }

    private void RunSession(EnrolSession s, CancellationToken token)
    {
        IntPtr dev = IntPtr.Zero;
        IntPtr db = IntPtr.Zero;
        try
        {
            dev = _svc.OpenDevice(0);
            if (dev == IntPtr.Zero) { Fail(s, "DEVICE_OPEN_FAILED", "Could not open the reader."); return; }
            db = _svc.DbInit();
            if (db == IntPtr.Zero) { Fail(s, "DATABASE_ERROR", "Could not initialise matching."); return; }
            Preload(db);
            var (w, h) = _svc.GetImageSize(dev);
            var img = new byte[w * h];
            var cap = new byte[2048];

            while (true)
            {
                if (token.IsCancellationRequested || s.Status == EnrolStatus.Cancelled) return;
                if (DateTimeOffset.UtcNow >= s.ExpiresAt)
                {
                    lock (_gate)
                    {
                        if (!s.IsTerminal)
                        {
                            s.Status = EnrolStatus.TimedOut;
                            s.ErrorCode = "CAPTURE_TIMEOUT";
                            s.Message = "Timed out waiting for finger. Please start again.";
                        }
                    }
                    _store.Audit("enrol_timed_out", s.UserId, false, null, "CAPTURE_TIMEOUT");
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
                lock (_gate)
                {
                    if (s.IsTerminal) return;
                    int fid = 0, score = 0;
                    if (zkfp2.DBIdentify(db, cap, ref fid, ref score) == zkfperrdef.ZKFP_ERR_OK)
                    {
                        s.Status = EnrolStatus.ScanReceived;
                        s.ErrorCode = "DUPLICATE_FINGERPRINT";
                        s.Message = "This finger is already registered. Please use a different finger.";
                        continue;
                    }
                    if (s.CompletedScans > 0 &&
                        zkfp2.DBMatch(db, cap, s.RegTmps[s.CompletedScans - 1]) <= 0)
                    {
                        s.Status = EnrolStatus.ScanReceived;
                        s.ErrorCode = "SAME_FINGER_REQUIRED";
                        s.Message = "Please press the same finger 3 times for the enrollment.";
                        continue;
                    }
                    Buffer.BlockCopy(cap, 0, s.RegTmps[s.CompletedScans], 0, cb);
                    s.CompletedScans++;
                    s.ErrorCode = null;
                    if (s.CompletedScans >= s.RequiredScans)
                    {
                        s.Status = EnrolStatus.Processing;
                        s.Message = "Processing enrolment…";
                        break;
                    }
                    s.Status = EnrolStatus.RemoveAndPlaceAgain;
                    s.Message = $"Scan received ({s.CompletedScans} of 3). Remove your finger and place the same finger again.";
                }
            }

            // Merge 3 -> 1, add to SDK DB, persist encrypted.
            var reg = new byte[2048];
            int cbReg = 0;
            int mret;
            lock (_gate)
            {
                mret = zkfp2.DBMerge(db, s.RegTmps[0], s.RegTmps[1], s.RegTmps[2], reg, ref cbReg);
            }
            if (mret != zkfperrdef.ZKFP_ERR_OK || cbReg <= 0)
            { Fail(s, "CAPTURE_FAILED", "Could not merge the three scans. Please start again."); return; }
            int addRet;
            lock (_gate)
            {
                addRet = zkfp2.DBAdd(db, Fid.For(s.UserId!), reg);
            }
            if (addRet != zkfperrdef.ZKFP_ERR_OK)
            { Fail(s, "CAPTURE_FAILED", "Could not store the fingerprint. Please start again."); return; }
            var cipher = TemplateProtection.Protect(reg, cbReg);
            try
            {
                _store.SaveEnrolment(s.UserId!, s.FullName, s.ExternalId, cipher, TemplateProtection.Algorithm);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "SQLite save failed");
                Fail(s, "DATABASE_ERROR", "Could not save enrolment."); return;
            }
            lock (_gate)
            {
                s.Status = EnrolStatus.Completed;
                s.Message = "Enrolment complete.";
                s.ErrorCode = null;
            }
            _store.Audit("enrol_completed", s.UserId, true, null, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Enrol worker crashed");
            Fail(s, "UNEXPECTED_ERROR", "Unexpected error during enrolment.");
        }
        finally
        {
            if (db != IntPtr.Zero) _svc.DbFree(db);
            if (dev != IntPtr.Zero) _svc.CloseDevice(dev);
            _svc.CaptureActive = false;
            _cpGate.Release("enrol");
        }
    }

    /// <summary>Load existing templates so duplicate fingers are rejected (M5 preload).</summary>
    private void Preload(IntPtr db)
    {
        foreach (var t in _store.LoadAllTemplates())
        {
            try
            {
                var plain = TemplateProtection.Unprotect(t.CipherBlob);
                zkfp2.DBAdd(db, Fid.For(t.UserId), plain);
                Array.Clear(plain);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Skipping unloadable template");
            }
        }
    }

    private void Fail(EnrolSession s, string code, string message)
    {
        lock (_gate)
        {
            if (!s.IsTerminal)
            {
                s.Status = EnrolStatus.Failed;
                s.ErrorCode = code;
                s.Message = message;
            }
        }
        _store.Audit("enrol_failed", s.UserId, false, null, code);
    }
}

/// <summary>Stable int fid per user (spec schema has no fid column).</summary>
public static class Fid
{
    public static int For(string userId)
    {
        uint h = 2166136261;
        foreach (var c in userId)
        {
            h ^= c;
            h *= 16777619;
        }
        return (int)(h & 0x7FFFFFFF) is var v && v == 0 ? 1 : v;
    }
}
