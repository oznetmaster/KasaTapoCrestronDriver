using Crestron.DeviceDrivers.EntityModel.Logging;

namespace KasaTapoCrestronDriver;

// One connection, operation queue and scheduled refresh per physical parent. Hub policy
// retains event demand and the shortest child report interval; strip policy uses the
// existing light/outlet polling settings and configured socket registrations.
internal sealed class ManagedParentDevicePoller : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IParentDeviceChild> _children = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IPlatformSharedConfiguration _sharedConfiguration;
    private readonly DriverControllerLogger? _logger;
    private readonly string _driverLogId;
    private readonly string _hostKey;
    private readonly bool _isStrip;
    private readonly Func<DeviceConfiguration, CancellationToken, Task<KasaDevice>> _connect;
    private readonly Func<KasaDevice, CancellationToken, Task> _update;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private DeviceConfiguration _configuration;
    private KasaDevice? _connectedDevice;
    private DeviceConfiguration? _connectedConfiguration;
    private CancellationTokenSource? _loopCancellation;
    private Task? _pollingTask;
    private bool _disposed;
    private bool _online;
    private int _failures;
    private TimeSpan? _deviceReportedInterval;

    public ManagedParentDevicePoller(string hostKey, DeviceConfiguration configuration,
        IPlatformSharedConfiguration sharedConfiguration, DriverControllerLogger? logger,
        string driverLogId, bool isStrip = false)
        : this(hostKey, configuration, sharedConfiguration, logger, driverLogId, isStrip,
            (config, token) => Discover.GetOrConnectSharedAsync(config, updateState: true, cancellationToken: token),
            (device, token) => device.UpdateAsync(token), (interval, token) => Task.Delay(interval, token)) { }

    // Inject only I/O and waiting for deterministic coordinator tests.
    internal ManagedParentDevicePoller(string hostKey, DeviceConfiguration configuration,
        IPlatformSharedConfiguration sharedConfiguration, DriverControllerLogger? logger,
        string driverLogId, bool isStrip,
        Func<DeviceConfiguration, CancellationToken, Task<KasaDevice>> connect,
        Func<KasaDevice, CancellationToken, Task> update,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _hostKey = hostKey;
        _configuration = configuration;
        _sharedConfiguration = sharedConfiguration;
        _logger = logger;
        _driverLogId = driverLogId;
        _isStrip = isStrip;
        _connect = connect;
        _update = update;
        _delay = delay;
    }

    public void UpdateConfiguration(DeviceConfiguration configuration)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_configuration, configuration)) return;
            _configuration = configuration;
            _online = false;
        }
    }

    public void RegisterChild(IParentDeviceChild child)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_children.TryGetValue(child.ChildId, out var previous))
            {
                if (ReferenceEquals(previous, child)) return;
                if (previous is IKasaHubChildEntity oldHub) oldHub.EventSubscribersChanged -= EvaluatePollingState;
            }
            _children[child.ChildId] = child;
            if (child is IKasaHubChildEntity hub) hub.EventSubscribersChanged += EvaluatePollingState;
            LogInfo($"RegisterChild: childId='{child.ChildId}', totalChildren={_children.Count}.");
            EvaluatePollingState();
        }
    }

    public void UnregisterChild(IParentDeviceChild child)
    {
        lock (_gate)
        {
            if (_children.TryGetValue(child.ChildId, out var current) && ReferenceEquals(current, child))
                UnregisterChild(child.ChildId);
        }
    }

    public void UnregisterChild(string childId)
    {
        lock (_gate)
        {
            if (_children.TryGetValue(childId, out var child) && child is IKasaHubChildEntity hub)
                hub.EventSubscribersChanged -= EvaluatePollingState;
            _children.Remove(childId);
            EvaluatePollingState();
        }
    }

    private void EvaluatePollingState()
    {
        lock (_gate)
        {
            if (_disposed) return;
            bool needed = _isStrip ? _children.Count > 0
                : _children.Values.OfType<IKasaHubChildEntity>().Any(child => child.HasEventSubscribers);
            if (!needed)
            {
                _loopCancellation?.Cancel();
                _loopCancellation = null;
                _pollingTask = null;
                if (_children.Count == 0)
                {
                    _connectedDevice = null;
                    _online = false;
                    _deviceReportedInterval = null;
                }
                return;
            }
            if (_loopCancellation is not null) return;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _loopCancellation = cancellation;
            _pollingTask = Task.Run(() => RunPollingAsync(cancellation));
        }
    }

    public void ApplyRuntimeConfiguration(PlatformSharedConfigurationSnapshot previous, PlatformSharedConfigurationSnapshot current)
    {
        bool restart = _isStrip
            ? previous.EnableLightPolling != current.EnableLightPolling || previous.LightPollInterval != current.LightPollInterval
            : previous.SensorPollInterval != current.SensorPollInterval;
        if (!restart) return;
        lock (_gate)
        {
            if (_disposed) return;
            _loopCancellation?.Cancel();
            _loopCancellation = null;
            _pollingTask = null;
            EvaluatePollingState();
        }
    }

    private async Task RunPollingAsync(CancellationTokenSource cancellation)
    {
        CancellationToken token = cancellation.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Keep initial state and offline recovery when steady strip polling is off.
                if (!_isStrip || _sharedConfiguration.EnableLightPolling || !_online)
                    await RefreshAsync(token).ConfigureAwait(false);
                await _delay(ComputeNextDelay(), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { LogError($"Polling loop failed: {ex}"); }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_loopCancellation, cancellation))
                {
                    _loopCancellation = null;
                    _pollingTask = null;
                }
            }
            cancellation.Dispose();
        }
    }

    internal TimeSpan ComputeNextDelay()
    {
        TimeSpan interval = _isStrip ? _sharedConfiguration.LightPollInterval
            : _deviceReportedInterval ?? _sharedConfiguration.SensorPollInterval;
        if (interval <= TimeSpan.Zero) interval = TimeSpan.FromSeconds(1);
        if (_failures == 0) return interval;
        return TimeSpan.FromMilliseconds(Math.Max(interval.TotalMilliseconds,
            Math.Min(120000, interval.TotalMilliseconds * Math.Pow(2, Math.Min(_failures, 10)))));
    }

    private void UpdateDeviceReportedInterval(KasaDevice device, IParentDeviceChild[] children)
    {
        if (_isStrip) return;
        int? shortest = null;
        foreach (var child in children)
        {
            int? seconds = device.GetChildDevice(child.ChildId)?.ReportMode.ReportInterval;
            if (seconds > 0 && (shortest is null || seconds < shortest)) shortest = seconds;
        }
        _deviceReportedInterval = shortest.HasValue ? TimeSpan.FromSeconds(shortest.Value) : (TimeSpan?)null;
    }

    public async Task<KasaDevice> ConnectSharedAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try { return await EnsureConnectedAsync(linked.Token).ConfigureAwait(false); }
        finally { _operationGate.Release(); }
    }

    private async Task<KasaDevice> EnsureConnectedAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_disposed) throw new ObjectDisposedException(nameof(ManagedParentDevicePoller));
        DeviceConfiguration config;
        lock (_gate) config = _configuration;
        if (_connectedDevice is not null && !_connectedDevice.IsDisposed && ReferenceEquals(config, _connectedConfiguration)) return _connectedDevice;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            _connectedDevice = await _connect(config, timeout.Token).ConfigureAwait(false);
            _connectedConfiguration = config;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException($"Parent '{_hostKey}' connection timed out."); }
        return _connectedDevice;
    }

    public async Task RefreshAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            IParentDeviceChild[] children;
            lock (_gate) children = _children.Values.ToArray();
            if (children.Length == 0) return;
            var device = await EnsureConnectedAsync(linked.Token).ConfigureAwait(false);
            await _update(device, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            UpdateDeviceReportedInterval(device, children);
            _failures = 0;
            _online = true;
            PublishCurrentState(device);
            LogInfo($"Parent refresh completed; children={children.Length}, policy={(_isStrip ? "strip" : "hub")}, intervalSeconds={ComputeNextDelay().TotalSeconds}.");
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            HandleFailure();
            LogError($"Parent refresh failed; nextDelaySeconds={ComputeNextDelay().TotalSeconds}: {ex}");
        }
        finally { _operationGate.Release(); }
    }

    // KasaClient relay commands refresh the parent. Fan that snapshot out without a
    // redundant follow-up UpdateAsync or a separate timer for the commanded socket.
    public async Task ExecuteCommandAsync(Func<KasaDevice, CancellationToken, Task> command, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var device = await EnsureConnectedAsync(linked.Token).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await command(device, timeout.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            _failures = 0;
            _online = true;
            PublishCurrentState(device);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
        catch
        {
            HandleFailure();
            throw;
        }
        finally { _operationGate.Release(); }
    }

    private void PublishCurrentState(KasaDevice device)
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var child in _children.Values.ToArray())
            {
                // Unregister/dispose cannot race with a callback to that registration.
                if (!_children.TryGetValue(child.ChildId, out var current) || !ReferenceEquals(current, child)) continue;
                try { child.ApplyPushedState(device.GetChildDevice(child.ChildId), device); }
                catch (Exception ex) { LogError($"State delivery failed for '{child.ChildId}': {ex}"); }
            }
        }
    }

    private void HandleFailure()
    {
        _failures++;
        _online = false;
        _connectedDevice = null; // The cache owns the device, not its projections.
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var child in _children.Values.ToArray())
            {
                try { child.ApplyConnectionState(false); }
                catch (Exception ex) { LogError($"Offline delivery failed for '{child.ChildId}': {ex}"); }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var child in _children.Values.OfType<IKasaHubChildEntity>())
                child.EventSubscribersChanged -= EvaluatePollingState;
            _children.Clear();
            _lifetime.Cancel();
            _connectedDevice = null;
        }
        // Waiters still own these primitives. Do not dispose a semaphore/token source
        // underneath an in-flight SDK operation; cancellation drains those operations.
    }

    private void LogInfo(string message) => _logger?.Log(_driverLogId, LogEntryLevel.Info, $"ManagedParentDevicePoller[{_hostKey}]: {message}");
    private void LogError(string message) => _logger?.Log(_driverLogId, LogEntryLevel.Error, $"ManagedParentDevicePoller[{_hostKey}]: {message}");
}
