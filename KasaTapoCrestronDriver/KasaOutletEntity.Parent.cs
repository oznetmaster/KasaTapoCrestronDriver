namespace KasaTapoCrestronDriver;

internal sealed partial class KasaOutletEntity : IParentDeviceChild
{
    private readonly ManagedParentDevicePoller? _parentPoller;
    string IParentDeviceChild.ChildId => _descriptor.ChildId!;

    private bool UseParentPolling()
    {
        if (_parentPoller is null) return false;
        if (!_disposed && _isConfigured && Volatile.Read(ref _stopState) == 0)
            _parentPoller.RegisterChild(this);
        else
            _parentPoller.UnregisterChild(this);
        return true;
    }

    void IParentDeviceChild.ApplyPushedState(ChildDevice? child, KasaDevice parentDevice)
    {
        if (_disposed || !_isConfigured || Volatile.Read(ref _stopState) != 0) return;
        if (child is null)
        {
            ((IParentDeviceChild)this).ApplyConnectionState(false);
            return;
        }
        _connectedDevice = parentDevice;
        UpdateDescriptorFromConnectedDevice(parentDevice);
        ApplyState(parentDevice);
        OnlineIndicatorIsOnline = true;
        ReadyIndicatorIsReady = true;
        if (_childPublished) PublishStateSnapshot();
    }

    void IParentDeviceChild.ApplyConnectionState(bool online)
    {
        if (_disposed || !_isConfigured || Volatile.Read(ref _stopState) != 0) return;
        OnlineIndicatorIsOnline = online;
        ReadyIndicatorIsReady = online;
        if (_childPublished) PublishStateSnapshot();
    }
}
