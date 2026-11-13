using MemoryMagic.Models;

namespace MemoryMagic.Services;

public sealed record MonitorConfiguration(
    bool IsEnabled,
    double RamThresholdPercent = 85.0,
    int PeriodicIntervalMinutes = 30,
    int PollIntervalSeconds = 10,
    int CooldownSeconds = 180,
    OptimizationScope Scope = OptimizationScope.SafeStandard,
    int MinProcessWorkingSetMb = 25);

/// <summary>
/// Background daemon monitoring RAM pressure and periodic schedules using PeriodicTimer.
/// Enforces cooldown hysteresis so automatic trims never cause page-fault thrashing.
/// </summary>
public sealed class BackgroundMonitorService : IAsyncDisposable
{
    private readonly MemoryOptimizerService _optimizer;
    private readonly AuditLogger _logger;
    private readonly object _configLock = new();

    private MonitorConfiguration _config;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private DateTimeOffset _lastOptimizationAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPeriodicTriggerAt = DateTimeOffset.Now;

    public event Action<MemorySnapshot>? SnapshotUpdated;
    public event Action<OptimizationResult>? AutoOptimizationCompleted;

    public BackgroundMonitorService(
        MemoryOptimizerService optimizer,
        AuditLogger logger,
        MonitorConfiguration? initialConfig = null)
    {
        _optimizer = optimizer;
        _logger = logger;
        _config = initialConfig ?? new MonitorConfiguration(IsEnabled: false);
    }

    public MonitorConfiguration Configuration
    {
        get { lock (_configLock) return _config; }
    }

    public void UpdateConfiguration(MonitorConfiguration updated)
    {
        lock (_configLock)
        {
            _config = updated with
            {
                RamThresholdPercent = Math.Clamp(updated.RamThresholdPercent, 50.0, 98.0),
                PeriodicIntervalMinutes = Math.Clamp(updated.PeriodicIntervalMinutes, 0, 1440),
                PollIntervalSeconds = Math.Clamp(updated.PollIntervalSeconds, 2, 300),
                CooldownSeconds = Math.Clamp(updated.CooldownSeconds, 30, 3600)
            };
        }

        _logger.Info(
            "Monitor",
            $"Configuration updated: AutoEnabled={_config.IsEnabled}, Threshold={_config.RamThresholdPercent:F0}%, " +
            $"Periodic={_config.PeriodicIntervalMinutes}m, Cooldown={_config.CooldownSeconds}s, Scope={_config.Scope}");
    }

    public void Start()
    {
        if (_loopTask is not null)
            return;

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
        _logger.Info("Monitor", "Background memory monitor started.");
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        int currentPollSeconds = Configuration.PollIntervalSeconds;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(currentPollSeconds));

        try
        {
            // Emit initial snapshot immediately on startup
            EmitSnapshot();

            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                MonitorConfiguration cfg = Configuration;
                if (cfg.PollIntervalSeconds != currentPollSeconds)
                {
                    currentPollSeconds = cfg.PollIntervalSeconds;
                    timer.Period = TimeSpan.FromSeconds(currentPollSeconds);
                }

                MemorySnapshot? snapshot = EmitSnapshot();
                if (snapshot is null || !cfg.IsEnabled)
                    continue;

                DateTimeOffset now = DateTimeOffset.Now;
                TimeSpan sinceLastOpt = now - _lastOptimizationAt;
                if (sinceLastOpt < TimeSpan.FromSeconds(cfg.CooldownSeconds))
                    continue;

                string? triggerReason = null;

                if (snapshot.LoadPercent >= cfg.RamThresholdPercent)
                {
                    triggerReason = $"Auto-Threshold ({snapshot.LoadPercent:F0}% >= {cfg.RamThresholdPercent:F0}%)";
                }
                else if (cfg.PeriodicIntervalMinutes > 0 &&
                         (now - _lastPeriodicTriggerAt) >= TimeSpan.FromMinutes(cfg.PeriodicIntervalMinutes))
                {
                    triggerReason = $"Auto-Periodic ({cfg.PeriodicIntervalMinutes}m)";
                }

                if (triggerReason is null)
                    continue;

                _lastOptimizationAt = now;
                _lastPeriodicTriggerAt = now;

                var options = new OptimizationOptions(
                    Scope: cfg.Scope,
                    MinimumWorkingSetThresholdMb: cfg.MinProcessWorkingSetMb,
                    ExcludeForegroundWindow: true,
                    TriggerSource: triggerReason);

                OptimizationResult result = await _optimizer.OptimizeAsync(options, ct).ConfigureAwait(false);
                AutoOptimizationCompleted?.Invoke(result);
                SnapshotUpdated?.Invoke(result.After);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.Error("Monitor", $"Background monitor faulted: {ex.Message}");
        }
    }

    private MemorySnapshot? EmitSnapshot()
    {
        try
        {
            MemorySnapshot snapshot = _optimizer.GetSnapshot();
            SnapshotUpdated?.Invoke(snapshot);
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.Warn("Monitor", $"Snapshot capture failed: {ex.Message}");
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
            return;

        _cts.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
        _cts = null;
        _loopTask = null;
    }
}
