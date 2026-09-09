using libzkfpcsharp;

namespace Bridge.Services;

/// <summary>
/// Sole owner of all vendor SDK calls. Everything else talks to the reader
/// through this service; nothing outside may reference libzkfpcsharp.
/// Reference implementation: ZKFinger Standard SDK 5.3.0.33, C#/Demo2/Form1.cs.
/// </summary>
public sealed class ZkFingerService
{
    private readonly object _gate = new();
    /// <summary>True while a capture session holds the device: skip USB re-enumeration.</summary>
    public volatile bool CaptureActive;
    private bool _sdkInitAttempted;
    private int _sdkInitRet = zkfperrdef.ZKFP_ERR_OK;
    private int _cachedCount = -1;
    private long _cachedAtTicks;
    // Re-enumeration period. The vendor SDK enumerates USB at Init time and
    // GetDeviceCount then returns the cached value, so a hot-unplug is only
    // visible after Terminate+Init. No hotplug event API exists in libzkfp.h.
    private static readonly TimeSpan ReenumPeriod = TimeSpan.FromSeconds(5);

    public const string DeviceModel = "SLK20R";

    /// <summary>Initialise the SDK once. Safe to call repeatedly.</summary>
    public int EnsureSdk()
    {
        lock (_gate)
        {
            if (_sdkInitAttempted) return _sdkInitRet;
            _sdkInitAttempted = true;
            try
            {
                _sdkInitRet = zkfp2.Init();
            }
            catch (DllNotFoundException)
            {
                _sdkInitRet = zkfperrdef.ZKFP_ERR_INITLIB;
            }
            catch
            {
                _sdkInitRet = zkfperrdef.ZKFP_ERR_FAIL;
            }
            return _sdkInitRet;
        }
    }

    public bool SdkOk => _sdkInitAttempted && _sdkInitRet == zkfperrdef.ZKFP_ERR_OK;
    public int SdkRetCode { get { lock (_gate) return _sdkInitRet; } }

    /// <summary>
    /// Fast cached count for endpoints. Never touches the SDK, never blocks:
    /// a background poller refreshes the cache. -1 = not yet polled.
    /// </summary>
    public int GetLiveDeviceCount()
    {
        lock (_gate) return _cachedCount;
    }

    /// <summary>
    /// Refresh the cache (Terminate+Init). Runs only on the background poller
    /// thread: a wedged native call can never stall an HTTP endpoint. Timed
    /// in logs so a slow/hung driver is visible.
    /// </summary>
    public void RefreshCache(ILogger log)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int count;
        lock (_gate)
        {
            if (!CaptureActive && _devHandle != IntPtr.Zero &&
                DateTime.UtcNow.Ticks - _devLastUsedTicks > DevIdleLimit.Ticks)
            {
                // Warm window over: close, then re-enumerate fresh below.
                try { zkfp2.CloseDevice(_devHandle); } catch { /* local log only */ }
                _devHandle = IntPtr.Zero;
            }
            if (CaptureActive || _devHandle != IntPtr.Zero)
            {
                // A session holds (or recently held) the device: never
                // Terminate under a live handle; just read the count.
                try { count = zkfp2.GetDeviceCount(); }
                catch { count = _cachedCount; }
                _cachedCount = count;
                _cachedAtTicks = DateTime.UtcNow.Ticks;
                return;
            }
            count = ReenumerateLocked();
            _cachedCount = count;
            _cachedAtTicks = DateTime.UtcNow.Ticks;
        }
        sw.Stop();
        log.LogInformation("USB re-enumeration: count={Count} in {Ms}ms", count, sw.ElapsedMilliseconds);
    }

    private int ReenumerateLocked()
    {
        try
        {
            if (_sdkInitAttempted)
            {
                try { zkfp2.Terminate(); } catch { /* local log only */ }
                _sdkInitAttempted = false;
            }
            _sdkInitAttempted = true;
            _sdkInitRet = zkfp2.Init();
            if (_sdkInitRet != zkfperrdef.ZKFP_ERR_OK) return -1;
            return zkfp2.GetDeviceCount();
        }
        catch (DllNotFoundException)
        {
            _sdkInitRet = zkfperrdef.ZKFP_ERR_INITLIB;
            return -1;
        }
        catch
        {
            _sdkInitRet = zkfperrdef.ZKFP_ERR_FAIL;
            return -1;
        }
    }

    /// <summary>Count devices without crashing when unplugged.</summary>
    public int GetDeviceCountSafe() => GetLiveDeviceCount();

    // ---- Persistent device handle ----
    // Opening the reader costs ~2-3s (USB open + sensor exposure
    // calibration), so captures share one open handle instead of
    // open -> close per scan. The handle is dropped when idle, on
    // repeated native failures (unplug mid-session), and before any
    // SDK Terminate, so a stale handle can never wedge the bridge.
    private IntPtr _devHandle = IntPtr.Zero;
    private long _devLastUsedTicks;
    private static readonly TimeSpan DevIdleLimit = TimeSpan.FromSeconds(60);

    /// <summary>Open once, then reuse. Returns Zero when unplugged.</summary>
    public IntPtr AcquireDevice()
    {
        lock (_gate)
        {
            if (_devHandle != IntPtr.Zero) return _devHandle;
            IntPtr dev;
            try { dev = zkfp2.OpenDevice(0); }
            catch { dev = IntPtr.Zero; }
            if (dev != IntPtr.Zero)
            {
                _devHandle = dev;
                _devLastUsedTicks = DateTime.UtcNow.Ticks;
            }
            return dev;
        }
    }

    public void NoteDeviceUsed()
    {
        lock (_gate) { _devLastUsedTicks = DateTime.UtcNow.Ticks; }
    }

    /// <summary>Close and forget the shared handle. Safe to call anytime.</summary>
    public void DropDevice()
    {
        lock (_gate)
        {
            if (_devHandle != IntPtr.Zero)
            {
                try { zkfp2.CloseDevice(_devHandle); } catch { /* local log only */ }
                _devHandle = IntPtr.Zero;
            }
        }
    }

    // ---- Device / matching primitives (thin, never throw) ----

    public IntPtr OpenDevice(int index)
    {
        try { return zkfp2.OpenDevice(index); }
        catch { return IntPtr.Zero; }
    }

    public void CloseDevice(IntPtr dev)
    {
        try { zkfp2.CloseDevice(dev); } catch { /* local log only */ }
    }

    public IntPtr DbInit()
    {
        try { return zkfp2.DBInit(); }
        catch { return IntPtr.Zero; }
    }

    public void DbFree(IntPtr db)
    {
        try { zkfp2.DBFree(db); } catch { /* local log only */ }
    }

    public (int Width, int Height) GetImageSize(IntPtr dev)
    {
        try
        {
            var buf = new byte[4];
            int size = 4, w = 0, h = 0;
            zkfp2.GetParameters(dev, 1, buf, ref size);
            zkfp2.ByteArray2Int(buf, ref w);
            size = 4;
            zkfp2.GetParameters(dev, 2, buf, ref size);
            zkfp2.ByteArray2Int(buf, ref h);
            return w > 0 && h > 0 ? (w, h) : (256, 288);
        }
        catch { return (256, 288); }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_devHandle != IntPtr.Zero)
            {
                try { zkfp2.CloseDevice(_devHandle); } catch { /* local log only */ }
                _devHandle = IntPtr.Zero;
            }
            if (!_sdkInitAttempted) return;
            try { zkfp2.Terminate(); } catch { /* local log only */ }
            _sdkInitAttempted = false;
            _sdkInitRet = zkfperrdef.ZKFP_ERR_OK;
        }
    }
}
