namespace Bridge.Services;

/// <summary>
/// Cross-operation capture gate: enrolment and verification share one reader,
/// so a session of either kind blocks the other (BRIDGE_BUSY).
/// </summary>
public sealed class CaptureGate
{
    private readonly object _g = new();
    private string? _holder;

    public bool TryAcquire(string holder)
    {
        lock (_g)
        {
            if (_holder is not null) return false;
            _holder = holder;
            return true;
        }
    }

    /// <summary>
    /// Kiosk UX: a second tap landing while the reader is momentarily held
    /// waits a few seconds for the current scan instead of failing outright.
    /// Returns false only if the reader stays stuck.
    /// </summary>
    public bool WaitAcquire(string holder, int timeoutMs)
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (true)
        {
            lock (_g)
            {
                if (_holder is null)
                {
                    _holder = holder;
                    return true;
                }
            }
            if (Environment.TickCount - deadline >= 0) return false;
            Thread.Sleep(100);
        }
    }

    public void Release(string holder)
    {
        lock (_g)
        {
            if (_holder == holder) _holder = null;
        }
    }

    public bool Held
    {
        get { lock (_g) return _holder is not null; }
    }
}
