using Bridge.Services;

namespace Bridge.Services;

/// <summary>
/// Refreshes the USB device cache off the request path: even a hung native
/// call can only stall this thread, never an HTTP endpoint.
/// </summary>
public sealed class DevicePoller : BackgroundService
{
    private readonly ZkFingerService _svc;
    private readonly ILogger<DevicePoller> _log;

    public DevicePoller(ZkFingerService svc, ILogger<DevicePoller> log)
    {
        _svc = svc;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // One immediate refresh so the cache is warm before the first request.
        await Task.Run(() => SafeRefresh(), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await Task.Run(() => SafeRefresh(), stoppingToken);
        }
    }

    private void SafeRefresh()
    {
        try
        {
            _svc.RefreshCache(_log);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "USB refresh failed");
        }
    }
}
