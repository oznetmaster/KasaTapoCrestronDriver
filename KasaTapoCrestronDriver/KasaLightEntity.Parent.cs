namespace KasaTapoCrestronDriver;

internal partial class KasaLightEntity : IParentDeviceChild
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
        bool establishingBaseline = !_initialStateEstablished;
        bool previousSuppression = _suppressPropertyNotifications;
        if (establishingBaseline) _suppressPropertyNotifications = true;
        try
        {
            _connectedDevice = parentDevice;
            UpdateDescriptorFromConnectedDevice(parentDevice);
            ConfigureDynamicFeatures(parentDevice, _descriptor);
            ApplyState(parentDevice);
            OnlineIndicatorIsOnline = true;
            ReadyIndicatorIsReady = true;
            if (establishingBaseline)
            {
                _initialStateEstablished = true;
                RaiseDefinitionChangedEvent();
            }
        }
        finally { _suppressPropertyNotifications = previousSuppression; }

    }

    void IParentDeviceChild.ApplyConnectionState(bool online)
    {
        if (_disposed || !_isConfigured || Volatile.Read(ref _stopState) != 0) return;
        if (!_initialStateEstablished) return;
        OnlineIndicatorIsOnline = online;
        ReadyIndicatorIsReady = online;

    }
}
