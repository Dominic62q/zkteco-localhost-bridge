using Bridge.Services;
using libzkfpcsharp;

namespace Bridge.Services;

/// <summary>
/// App-agnostic one-shot operations for any authorized web app. No users, no
/// storage, no business concepts: templates enter and leave as base64, the
/// bridge only captures, merges, and matches. Shares the capture gate, so at
/// most one device operation runs at a time across v0 and v1.
/// </summary>
public sealed class CaptureService
{
    private readonly ZkFingerService _svc;
    private readonly CaptureGate _gate;
    private readonly ILogger<CaptureService> _log;

    public CaptureService(ZkFingerService svc, CaptureGate gate, ILogger<CaptureService> log)
    {
        _svc = svc;
        _gate = gate;
        _log = log;
    }

    /// <summary>
    /// Capture one live template. Blocks up to timeoutSeconds. The device
    /// stays open across scans (warm handle); only the first touch after
    /// idle pays the USB open + sensor calibration cost.
    /// </summary>
    public (byte[] Template, int Size) Capture(int timeoutSeconds)
    {
        timeoutSeconds = Math.Clamp(timeoutSeconds, 5, 120);
        if (!_gate.WaitAcquire("v1-capture", 8000))
            throw new BridgeBusyException();
        _svc.CaptureActive = true;
        try
        {
            if (_svc.GetLiveDeviceCount() == 0)
                throw new DeviceNotFoundException();
            IntPtr dev = _svc.AcquireDevice();
            if (dev == IntPtr.Zero)
                throw new InvalidOperationException("DEVICE_OPEN_FAILED");
            var (w, h) = _svc.GetImageSize(dev);
            var img = new byte[w * h];
            var cap = new byte[2048];
            var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
            int failures = 0;
            while (DateTimeOffset.UtcNow < deadline)
            {
                int cb = 2048;
                int ret;
                try
                {
                    ret = zkfp2.AcquireFingerprint(dev, img, cap, ref cb);
                }
                catch
                {
                    ret = -1;
                }
                if (ret != zkfperrdef.ZKFP_ERR_OK || cb <= 0)
                {
                    // No finger present most of the time; but a long failure
                    // streak means the cached handle died (unplug) — drop it
                    // and reopen so the next scan starts clean.
                    if (++failures >= 25)
                    {
                        failures = 0;
                        _svc.DropDevice();
                        dev = _svc.AcquireDevice();
                        if (dev == IntPtr.Zero)
                        {
                            Thread.Sleep(500);
                            continue;
                        }
                    }
                    else
                    {
                        Thread.Sleep(200);
                    }
                    continue;
                }
                var exact = new byte[cb];
                Buffer.BlockCopy(cap, 0, exact, 0, cb);
                return (exact, cb);
            }
            throw new TimeoutException("CAPTURE_TIMEOUT");
        }
        finally
        {
            _svc.NoteDeviceUsed();
            _svc.CaptureActive = false;
            _gate.Release("v1-capture");
        }
    }

    /// <summary>Match one live capture against a caller-supplied template.</summary>
    public (bool Matched, int Score) Verify(byte[] reference, int timeoutSeconds)
    {
        var (live, _) = Capture(timeoutSeconds);
        IntPtr db = IntPtr.Zero;
        try
        {
            db = _svc.DbInit();
            if (db == IntPtr.Zero)
                throw new InvalidOperationException("DATABASE_ERROR");
            var score = zkfp2.DBMatch(db, live, reference);
            return (score > 0, score);
        }
        finally
        {
            Array.Clear(live);
            if (db != IntPtr.Zero) _svc.DbFree(db);
        }
    }

    /// <summary>Merge three enrolment captures into one registration template.</summary>
    public byte[] Merge(byte[][] parts)
    {
        if (parts.Length != 3 || parts.Any(p => p is null || p.Length == 0))
            throw new ArgumentException("Exactly three captures are required.");
        if (!_gate.TryAcquire("v1-merge"))
            throw new BridgeBusyException();
        IntPtr db = IntPtr.Zero;
        try
        {
            db = _svc.DbInit();
            if (db == IntPtr.Zero)
                throw new InvalidOperationException("DATABASE_ERROR");
            var reg = new byte[2048];
            int cbReg = 0;
            var ret = zkfp2.DBMerge(db, parts[0], parts[1], parts[2], reg, ref cbReg);
            if (ret != zkfperrdef.ZKFP_ERR_OK || cbReg <= 0)
                throw new InvalidOperationException("CAPTURE_FAILED");
            var exact = new byte[cbReg];
            Buffer.BlockCopy(reg, 0, exact, 0, cbReg);
            return exact;
        }
        finally
        {
            if (db != IntPtr.Zero) _svc.DbFree(db);
            _gate.Release("v1-merge");
        }
    }
    /// <summary>
    /// 1:N identification: one live capture searched over caller-supplied
    /// candidates. Returns the matching candidate id and score, or no match.
    /// </summary>
    public (string? MatchId, int Score) Identify(
        IReadOnlyList<(string Id, byte[] Template)> candidates, int timeoutSeconds)
    {
        var (live, _) = Capture(timeoutSeconds);
        IntPtr db = IntPtr.Zero;
        try
        {
            db = _svc.DbInit();
            if (db == IntPtr.Zero)
                throw new InvalidOperationException("DATABASE_ERROR");
            var fids = new Dictionary<int, string>();
            int fid = 1;
            foreach (var (id, tpl) in candidates)
            {
                if (zkfp2.DBAdd(db, fid, tpl) == zkfperrdef.ZKFP_ERR_OK)
                    fids[fid] = id;
                fid++;
            }
            int found = 0, score = 0;
            var ret = zkfp2.DBIdentify(db, live, ref found, ref score);
            if (ret == zkfperrdef.ZKFP_ERR_OK && fids.TryGetValue(found, out var matchId))
                return (matchId, score);
            return (null, score);
        }
        finally
        {
            Array.Clear(live);
            if (db != IntPtr.Zero) _svc.DbFree(db);
        }
    }
}
