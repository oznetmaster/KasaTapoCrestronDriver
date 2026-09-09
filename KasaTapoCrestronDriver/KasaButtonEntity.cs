// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Globalization;
using System.IO;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.EntityModel.Logging;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

namespace KasaTapoCrestronDriver;

/// <summary>
/// Standalone managed entity for hub child button devices (<see cref="ManagedChildKind.Button"/>),
/// such as a Tapo H100 hub's S200B smart button. Button children report battery state plus a
/// trigger log (single/double click, rotate); there is no persistent on/off state to poll, so
/// this entity surfaces the latest trigger as a discrete event rather than a level property.
/// It deliberately does not derive from or share implementation with
/// <see cref="KasaOutletEntity"/>/<see cref="KasaSensorEntity"/>: each child kind has its own,
/// simpler UI definition and lifecycle needs.
/// </summary>
internal sealed partial class KasaButtonEntity : ReflectedAttributeDriverEntity, IKasaHubChildEntity
	{
	private readonly DriverControllerLogger _logger;
	private readonly string _driverLogId;
	private readonly Action<ManagedLightDescriptor>? _descriptorUpdated;
	private readonly IPlatformSharedConfiguration _sharedConfiguration;
	private readonly ManagedParentDevicePoller? _hubPoller;
	private readonly CancellationTokenSource _lifetimeCancellationSource = new ();
	private readonly IComponentLogger? _uiDefinitionLogger;
	private readonly string? _uiDefinitionFilePath;
	private UiDefinitionProperty? _uiDefinition;

	private ManagedLightDescriptor _descriptor = null!;
	private DeviceConfiguration? _configuration;
	private KasaDevice? _connectedDevice;
	private bool _isConfigured;
	private bool _childPublished;
	private int _stopState;
	private bool _disposed;
	private bool _registeredWithHubPoller;
	private bool _doubleClickEnableAttempted;
	private bool _allowDoubleClick = true;
	private long? _lastSeenTriggerTimestamp;
	private ChildBatterySensorState? _lastBatteryState;
	private bool _lastStateWasNull = true;

	/// <summary>
	/// This hub child's own <c>ChildId</c>, used by the owning <see cref="ManagedParentDevicePoller"/>
	/// to look up this entity's own slice of the hub's child list on each poll tick. See
	/// <see cref="IKasaHubChildEntity"/>.
	/// </summary>
	public string ChildId
		{
		get { return _descriptor.ChildId ?? string.Empty; }
		}

	public string DeviceName { get; private set; } = string.Empty;

	public string ModelName { get; private set; } = string.Empty;

	public string SerialNumber { get; private set; } = string.Empty;

	[EntityProperty (Id = "deviceLabel")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string DeviceLabel
		{
		get;
		private set => SetAndNotify ("deviceLabel", value, ref field);
		} = string.Empty;

	[EntityProperty (Id = "onlineIndicatorIsOnline")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool OnlineIndicatorIsOnline
		{
		get;
		private set => SetAndNotify ("onlineIndicatorIsOnline", value, ref field);
		}

	[EntityProperty (Id = "readyIndicatorIsReady")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool ReadyIndicatorIsReady
		{
		get;
		private set => SetAndNotify ("readyIndicatorIsReady", value, ref field);
		}

	[EntityProperty (Id = "batteryLevelPercent")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public double BatteryLevelPercent
		{
		get;
		private set => SetAndNotify ("batteryLevelPercent", value, ref field);
		}

	[EntityProperty (Id = "batteryIsLow")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public bool BatteryIsLow
		{
		get;
		private set => SetAndNotify ("batteryIsLow", value, ref field);
		}

	[EntityProperty (Id = "lastTriggerType")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public string LastTriggerType
		{
		get;
		private set => SetAndNotify ("lastTriggerType", value, ref field);
		} = string.Empty;

	[EntityProperty (Id = "lastTriggerTime")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public double LastTriggerTime
		{
		get;
		private set => SetAndNotify ("lastTriggerTime", value, ref field);
		}

	// Tile status text: "SinglePress - 7:03 AM". Repeated identical presses still differ because
	// the formatted time changes, so the tile visibly refreshes on every press.
	[EntityProperty (Id = "lastTriggerDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string LastTriggerDisplay
		{
		get;
		private set => SetAndNotify ("lastTriggerDisplay", value, ref field);
		} = string.Empty;

	[EntityProperty (Id = "lastGestureLabel")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string LastGestureLabel
		{
		get;
		private set => SetAndNotify ("lastGestureLabel", value, ref field);
		} = string.Empty;

	[EntityProperty (Id = "lastTriggerTimeDisplay")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string LastTriggerTimeDisplay
		{
		get;
		private set => SetAndNotify ("lastTriggerTimeDisplay", value, ref field);
		} = string.Empty;

	[EntityProperty (Id = "batteryStatusLabel")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string BatteryStatusLabel
		{
		get;
		private set => SetAndNotify ("batteryStatusLabel", value, ref field);
		} = string.Empty;

	[EntityProperty (Id = "hasBattery")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool HasBattery
		{
		get;
		private set => SetAndNotify ("hasBattery", value, ref field);
		}

	[EntityProperty (Id = "buttonIcon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string ButtonIcon
		{
		get;
		private set => SetAndNotify ("buttonIcon", value, ref field);
		} = "icGenericDeviceOff";

	private EventHandler? _buttonTriggered;

	// Explicit add/remove (rather than a plain field-like event) so subscription changes are
	// directly observable: they are both logged and, more importantly, reported to the owning
	// ManagedParentDevicePoller via EventSubscribersChanged so it can start/stop polling this hub
	// the moment this button's subscription state changes. Note: the C# 13 'field' keyword only
	// applies to property accessors, not custom event add/remove accessors, so an explicit backing
	// field is still required here.
	[EntityEvent (Id = "buttonTriggered", FriendlyName = "Button Triggered", NameLocalizationKey = "Event_ButtonTriggered")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler ButtonTriggered
		{
		add
			{
			_buttonTriggered += value;
			LogInfo ($"Button entity '{ControllerId}' ButtonTriggered subscriber added; totalSubscribers={_buttonTriggered?.GetInvocationList ().Length ?? 0}.");
			EventSubscribersChanged?.Invoke ();
			}
		remove
			{
			_buttonTriggered -= value;
			LogInfo ($"Button entity '{ControllerId}' ButtonTriggered subscriber removed; totalSubscribers={_buttonTriggered?.GetInvocationList ().Length ?? 0}.");
			EventSubscribersChanged?.Invoke ();
			}
		}

	/// <summary>
	/// See <see cref="IKasaHubChildEntity.EventSubscribersChanged"/>.
	/// </summary>
	public event Action? EventSubscribersChanged;

	private EventHandler? _singlePressed;
	private EventHandler? _doublePressed;
	private EventHandler? _held;
	private EventHandler? _released;

	// Discrete per-gesture events so Crestron Home can react to each gesture independently
	// (single vs. double vs. hold vs. release) instead of only the aggregate ButtonTriggered.
	[EntityEvent (Id = "buttonSinglePressed", FriendlyName = "Button Single Pressed", NameLocalizationKey = "Event_ButtonSinglePressed")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler SinglePressed
		{
		add
			{
			_singlePressed += value;
			LogInfo ($"Button entity '{ControllerId}' SinglePressed subscriber added; totalSubscribers={_singlePressed?.GetInvocationList ().Length ?? 0}.");
			EventSubscribersChanged?.Invoke ();
			}
		remove
			{
			_singlePressed -= value;
			LogInfo ($"Button entity '{ControllerId}' SinglePressed subscriber removed; totalSubscribers={_singlePressed?.GetInvocationList ().Length ?? 0}.");
			EventSubscribersChanged?.Invoke ();
			}
		}

	[EntityEvent (Id = "buttonDoublePressed", FriendlyName = "Button Double Pressed", NameLocalizationKey = "Event_ButtonDoublePressed")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler DoublePressed
		{
		add
			{
			_doublePressed += value;
			LogInfo ($"Button entity '{ControllerId}' DoublePressed subscriber added; totalSubscribers={_doublePressed?.GetInvocationList ().Length ?? 0}.");
			EventSubscribersChanged?.Invoke ();
			}
		remove
			{
			_doublePressed -= value;
			LogInfo ($"Button entity '{ControllerId}' DoublePressed subscriber removed; totalSubscribers={_doublePressed?.GetInvocationList ().Length ?? 0}.");
			EventSubscribersChanged?.Invoke ();
			}
		}

	[EntityEvent (Id = "buttonHeld", FriendlyName = "Button Held", NameLocalizationKey = "Event_ButtonHeld")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler Held
		{
		add
			{
			_held += value;
			LogInfo ($"Button entity '{ControllerId}' Held subscriber added; totalSubscribers={_held?.GetInvocationList ().Length ?? 0}.");
			EventSubscribersChanged?.Invoke ();
			}
		remove
			{
			_held -= value;
			LogInfo ($"Button entity '{ControllerId}' Held subscriber removed; totalSubscribers={_held?.GetInvocationList ().Length ?? 0}.");
			EventSubscribersChanged?.Invoke ();
			}
		}

	[EntityEvent (Id = "buttonReleased", FriendlyName = "Button Released", NameLocalizationKey = "Event_ButtonReleased")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler Released
		{
		add
			{
			_released += value;
			LogInfo ($"Button entity '{ControllerId}' Released subscriber added; totalSubscribers={_released?.GetInvocationList ().Length ?? 0}.");
			EventSubscribersChanged?.Invoke ();
			}
		remove
			{
			_released -= value;
			LogInfo ($"Button entity '{ControllerId}' Released subscriber removed; totalSubscribers={_released?.GetInvocationList ().Length ?? 0}.");
			EventSubscribersChanged?.Invoke ();
			}
		}

	/// <summary>
	/// See <see cref="IKasaHubChildEntity.HasEventSubscribers"/>. Mirrors
	/// <see cref="KasaSensorEntity.HasEventSubscribers"/>: once this button is added to a room and
	/// its <c>buttonTriggered</c> event is wired into the UI/a scene, a real subscriber is attached
	/// to <see cref="ButtonTriggered"/>, which is what keeps this hub's poller actively polling. If
	/// nothing is subscribed there is nothing to observe a trigger for, so polling correctly stops
	/// until a subscriber appears - same behavior as sensors.
	/// </summary>
	public bool HasEventSubscribers
		{
		get
			{
			return _buttonTriggered is not null
				|| _singlePressed is not null
				|| _doublePressed is not null
				|| _held is not null
				|| _released is not null;
			}
		}

	public KasaButtonEntity (
		string controllerId,
		ManagedLightDescriptor descriptor,
		DeviceConfiguration configuration,
		Action<ManagedLightDescriptor>? descriptorUpdated,
		IPlatformSharedConfiguration sharedConfiguration,
		DriverImplementationResources resources,
		DriverControllerLogger logger,
		string driverLogId,
		string? driverDataDirectoryPath = null,
		ManagedParentDevicePoller? hubPoller = null,
		bool allowDoubleClick = true)
		: base (controllerId)
		{
		_descriptorUpdated = descriptorUpdated;
		_sharedConfiguration = sharedConfiguration;
		_hubPoller = hubPoller;
		_logger = logger;
		_driverLogId = driverLogId;
		_allowDoubleClick = allowDoubleClick;

		UpdateDescriptor (descriptor, configuration);

		var baseDir = driverDataDirectoryPath ?? Path.GetTempPath ();
		var buttonUiDir = Path.Combine (baseDir, "button", "uidefinitions");
		_uiDefinitionLogger = resources.InitLogger;
		_uiDefinitionFilePath = Path.Combine (buttonUiDir, "UiDefinitionBasic.xml");
		LogInfo ($"Button entity '{ControllerId}' UiDefinition: driverDataDirectoryPath='{driverDataDirectoryPath}', buttonUiDir='{buttonUiDir}', exists={Directory.Exists (buttonUiDir)}.");
		try
			{
			if (File.Exists (_uiDefinitionFilePath))
				{
				_uiDefinition = new UiDefinitionProperty (_uiDefinitionFilePath, _uiDefinitionLogger);
				}
			else
				{
				LogError ($"Button entity '{ControllerId}' UiDefinition load failed: file not found at '{_uiDefinitionFilePath}'.");
				}
			}
		catch (Exception ex)
			{
			LogError ($"Button entity '{ControllerId}' UiDefinition load failed: {ex.Message}");
			}

		LogInfo ($"Button entity '{ControllerId}' UiDefinition: loaded={_uiDefinition != null}.");

		try
			{
			AddProperty (this, UiDefinitionProperty.Name, _uiDefinition);
			}
		catch (Exception ex)
			{
			LogError ($"Button entity '{ControllerId}' AddProperty UiDefinition failed: {ex.Message}");
			}

		IComponentLogger extensionExecutorLogger = resources.Logger.GetComponentLogger ("CAPA - ", $"ExtensionExecutor:{controllerId}");
		var doCommand = new ExtensionDoCommandExecutor (GetCommand, extensionExecutorLogger);
		AddCommand (this, ExtensionDoCommandExecutor.CommandName, doCommand);

		var setPropertyValue = new ExtensionSetPropertyValueExecutor (GetCommand, extensionExecutorLogger);
		AddCommand (this, ExtensionSetPropertyValueExecutor.CommandName, setPropertyValue);

		LogInfo ($"Button entity '{ControllerId}' created in passive discovered state; awaiting child configuration callback before activation.");
		}

	public void UpdateDescriptor (ManagedLightDescriptor descriptor, DeviceConfiguration configuration)
		{
		if (_descriptor is null)
			{
			_descriptor = descriptor;
			}
		else
			{
			_descriptor.Name = SelectPreferredDescriptorName (_descriptor, descriptor);
			}

		_configuration = configuration;
		DeviceName = _descriptor.Name;
		ModelName = _descriptor.ModelName;
		SerialNumber = _descriptor.SerialNumber;
		DeviceLabel = _descriptor.Name;
		}

	private static string SelectPreferredDescriptorName (ManagedLightDescriptor currentDescriptor, ManagedLightDescriptor incomingDescriptor)
		{
		if (!string.IsNullOrWhiteSpace (currentDescriptor.Name)
			&& incomingDescriptor.AwaitingConnectedIdentity)
			{
			return currentDescriptor.Name;
			}

		return !string.IsNullOrWhiteSpace (incomingDescriptor.Name)
			? incomingDescriptor.Name
			: currentDescriptor.Name;
		}

	public void UpdateConfiguration (DeviceConfiguration configuration)
		{
		_configuration = configuration;
		}

	public bool TryAttachConnectedDevice (KasaDevice device, string context)
		{
		if (device is null)
			{
			throw new ArgumentNullException (nameof (device));
			}

		if (_disposed || Volatile.Read (ref _stopState) != 0)
			{
			return false;
			}

		KasaDevice? previousDevice = Interlocked.CompareExchange (ref _connectedDevice, device, null);
		if (ReferenceEquals (previousDevice, device))
			{
			return true;
			}

		if (previousDevice is not null)
			{
			return false;
			}

		UpdateDescriptorFromConnectedDevice (device);
		LogInfo ($"Button entity '{ControllerId}' adopted connected device from context='{context}'.");
		return true;
		}

	public void SetConfigured (bool configured, string context)
		{
		if (_disposed)
			{
			return;
			}

		if (_isConfigured == configured)
			{
			return;
			}

		_isConfigured = configured;
		LogInfo ($"Button entity '{ControllerId}' SetConfigured: configured={configured}, context='{context}'.");

		if (!configured)
			{
			UnregisterFromHubPoller ();
			return;
			}

		RegisterWithHubPollerIfConfigured ();
		}

	public async Task SetConfiguredAsync (bool configured, string context, CancellationToken cancellationToken)
		{
		if (_disposed)
			{
			return;
			}

		if (_isConfigured == configured)
			{
			return;
			}

		_isConfigured = configured;
		LogInfo ($"Button entity '{ControllerId}' SetConfiguredAsync: configured={configured}, context='{context}'.");

		if (!configured)
			{
			UnregisterFromHubPoller ();
			return;
			}

		RegisterWithHubPollerIfConfigured ();
		await Task.CompletedTask.ConfigureAwait (false);
		}

	public void ApplyRuntimeConfiguration (PlatformSharedConfigurationSnapshot previousConfiguration, PlatformSharedConfigurationSnapshot currentConfiguration)
		{
		// Polling cadence is now owned entirely by the shared ManagedParentDevicePoller for this
		// hub; ManagedParentDevicePoller.ApplyRuntimeConfiguration is what reacts to a
		// SensorPollInterval change, not this entity.
		}

	public void Stop ()
		{
		if (Interlocked.Exchange (ref _stopState, 1) != 0)
			{
			return;
			}

		UnregisterFromHubPoller ();
		_lifetimeCancellationSource.Cancel ();
		}

	public override void Dispose ()
		{
		if (_disposed)
			{
			return;
			}

		Stop ();
		_lifetimeCancellationSource.Dispose ();
		_disposed = true;
		}

	private void RegisterWithHubPollerIfConfigured ()
		{
		if (_hubPoller is null || _registeredWithHubPoller)
			{
			return;
			}

		_registeredWithHubPoller = true;
		_hubPoller.RegisterChild (this);
		}

	// The S200B only writes 'doubleClick' entries to its trigger log when double-click reporting is
	// enabled on the device itself; with it off, a double press is logged as two separate
	// 'singleClick' entries and the doubleClick gesture can never be observed. Enable it once per
	// driver load so the discrete DoublePressed event can actually fire, unless the "Allow Double
	// Click" configuration item has been turned off for this device.
	private void EnsureDoubleClickEnabled (ChildDevice? child)
		{
		if (child is null || _doubleClickEnableAttempted)
			{
			return;
			}

		bool? enabled = child.DoubleClick.Enabled;
		if (enabled is null)
			{
			// Child does not report double-click support; nothing to enable.
			return;
			}

		if (!_allowDoubleClick)
			{
			// AllowDoubleClick is off; do not force-enable it on the device here. SetAllowDoubleClick
			// handles the disabled case explicitly (including turning it off if it was already on).
			return;
			}

		_doubleClickEnableAttempted = true;
		if (enabled.Value)
			{
			LogInfo ($"Button entity '{ControllerId}' EnsureDoubleClickEnabled: already enabled on device.");
			return;
			}

		LogInfo ($"Button entity '{ControllerId}' EnsureDoubleClickEnabled: double-click reporting is disabled; enabling it on the device.");
		_ = SetDoubleClickEnabledAsync (child, true);
		}

	/// <summary>
	/// Invoked when the "Allow Double Click" configuration item changes. Applies the new
	/// preference to the currently connected child device (if any) by calling
	/// <c>ChildDoubleClick.SetEnabledAsync</c>; if no device is connected yet, the preference is
	/// still recorded and will be applied the next time <see cref="EnsureDoubleClickEnabled"/>
	/// (or a future call to this method) runs against a connected child.
	/// </summary>
	public void SetAllowDoubleClick (bool allowDoubleClick)
		{
		if (_allowDoubleClick == allowDoubleClick)
			{
			return;
			}

		_allowDoubleClick = allowDoubleClick;
		_doubleClickEnableAttempted = false;
		LogInfo ($"Button entity '{ControllerId}' SetAllowDoubleClick: allowDoubleClick={allowDoubleClick}.");

		ChildDevice? child = _connectedDevice?.GetChildDevice (ChildId);
		if (child is null)
			{
			return;
			}

		bool? enabled = child.DoubleClick.Enabled;
		if (enabled is null || enabled.Value == allowDoubleClick)
			{
			return;
			}

		_doubleClickEnableAttempted = true;
		_ = SetDoubleClickEnabledAsync (child, allowDoubleClick);
		}

	private async Task SetDoubleClickEnabledAsync (ChildDevice child, bool enabled)
		{
		try
			{
			await child.DoubleClick.SetEnabledAsync (enabled).ConfigureAwait (false);
			LogInfo ($"Button entity '{ControllerId}' SetDoubleClickEnabledAsync: double-click reporting set to {enabled} successfully.");
			}
		catch (Exception ex)
			{
			LogError ($"Button entity '{ControllerId}' failed to set double-click reporting to {enabled}: {ex}");
			}
		}

	private void UnregisterFromHubPoller ()
		{
		if (_hubPoller is null || !_registeredWithHubPoller)
			{
			return;
			}

		_registeredWithHubPoller = false;
		_hubPoller.UnregisterChild (ChildId);
		}

	/// <summary>
	/// Invoked by the owning <see cref="ManagedParentDevicePoller"/> once per successful poll tick
	/// with this entity's current child slice (or <c>null</c> if the child was not found on the
	/// hub's most recent child list). See <see cref="IKasaHubChildEntity"/>.
	/// </summary>
	public void ApplyPushedState (ChildDevice? child, KasaDevice parentDevice)
		{
		if (_disposed)
			{
			return;
			}

		UpdateDescriptorFromConnectedDevice (parentDevice);
		EnsureDoubleClickEnabled (child);
		bool stateChanged = HasChildStateChanged (child);
		if (stateChanged)
			{
			ApplyState (child);
			}

		bool wasOnline = OnlineIndicatorIsOnline && ReadyIndicatorIsReady;
		OnlineIndicatorIsOnline = true;
		ReadyIndicatorIsReady = true;

		if (_childPublished && (stateChanged || !wasOnline))
			{
			PublishStateSnapshot ();
			}
		}

	// Compares the freshly pushed child's typed battery state record (value-comparable per
	// KasaClient v1.6.0) and latest trigger-log timestamp against the last-seen snapshot, so an
	// unchanged poll tick can skip ApplyState's property/event work and the subsequent publish
	// entirely, rather than gating only the final publish after unconditionally re-applying state.
	private bool HasChildStateChanged (ChildDevice? child)
		{
		if (child is null)
			{
			bool changed = !_lastStateWasNull;
			_lastStateWasNull = true;
			_lastBatteryState = null;
			return changed;
			}

		ChildBatterySensorState? batteryState = child.Battery.State;
		IReadOnlyList<ChildTriggerLogEntry> logs = child.TriggerLogs.Logs;
		long? latestTriggerTimestamp = logs.Count > 0 ? logs[0].Timestamp : null;

		bool stateChanged = _lastStateWasNull
			|| _lastBatteryState != batteryState
			|| latestTriggerTimestamp != _lastSeenTriggerTimestamp;

		_lastStateWasNull = false;
		_lastBatteryState = batteryState;

		return stateChanged;
		}

	/// <summary>
	/// Invoked by the owning <see cref="ManagedParentDevicePoller"/> when a poll tick's connect or
	/// <c>UpdateAsync</c> call fails, so this entity can reflect the hub being unreachable without
	/// receiving stale pushed state. See <see cref="IKasaHubChildEntity"/>.
	/// </summary>
	public void ApplyConnectionState (bool online)
		{
		if (_disposed)
			{
			return;
			}

		bool changed = OnlineIndicatorIsOnline != online || ReadyIndicatorIsReady != online;
		OnlineIndicatorIsOnline = online;
		ReadyIndicatorIsReady = online;

		if (_childPublished && changed)
			{
			PublishStateSnapshot ();
			}
		}

	// Hub children never poll on their own - state arrives via ApplyPushedState from the shared
	// ManagedParentDevicePoller - so this legacy IKasaManagedChildEntity member is a no-op here.
	public Task RefreshAsync (CancellationToken cancellationToken)
		{
		return Task.CompletedTask;
		}

	public void NotifyChildPublished ()
		{
		if (_disposed)
			{
			return;
			}

		_childPublished = true;

		LogInfo ($"Button entity '{ControllerId}' NotifyChildPublished invoked.");
		PublishStateSnapshot ();
		}

	public void NotifyChildRunning (string context)
		{
		if (_disposed)
			{
			return;
			}

		LogInfo ($"Button entity '{ControllerId}' NotifyChildRunning invoked, context='{context}'.");
		PublishStateSnapshot ();
		}

	public void PublishStateSnapshot ()
		{
		LogInfo ($"Button entity '{ControllerId}' PublishStateSnapshot invoked, uiDefinitionLoaded={_uiDefinition != null}.");
		if (_uiDefinition is not null)
			{
			DriverEntityValue? uiDefinitionValue = _uiDefinition.GetValue (this, null);
			if (uiDefinitionValue.HasValue)
				{
				NotifyPropertyChanged (UiDefinitionProperty.Name, uiDefinitionValue.Value);
				}
			}

		PublishProperty ("deviceLabel", new DriverEntityValue (DeviceLabel), "PublishStateSnapshot");
		PublishProperty ("batteryLevelPercent", new DriverEntityValue (BatteryLevelPercent), "PublishStateSnapshot");
		PublishProperty ("batteryIsLow", new DriverEntityValue (BatteryIsLow), "PublishStateSnapshot");
		PublishProperty ("lastTriggerType", new DriverEntityValue (LastTriggerType), "PublishStateSnapshot");
		PublishProperty ("lastTriggerTime", new DriverEntityValue (LastTriggerTime), "PublishStateSnapshot");
		PublishProperty ("lastTriggerDisplay", new DriverEntityValue (LastTriggerDisplay), "PublishStateSnapshot");
		PublishProperty ("lastTriggerTimeDisplay", new DriverEntityValue (LastTriggerTimeDisplay), "PublishStateSnapshot");
		PublishProperty ("lastGestureLabel", new DriverEntityValue (LastGestureLabel), "PublishStateSnapshot");
		PublishProperty ("batteryStatusLabel", new DriverEntityValue (BatteryStatusLabel), "PublishStateSnapshot");
		PublishProperty ("hasBattery", new DriverEntityValue (HasBattery), "PublishStateSnapshot");
		PublishProperty ("buttonIcon", new DriverEntityValue (ButtonIcon), "PublishStateSnapshot");
		PublishProperty ("onlineIndicatorIsOnline", new DriverEntityValue (OnlineIndicatorIsOnline), "PublishStateSnapshot");
		PublishProperty ("readyIndicatorIsReady", new DriverEntityValue (ReadyIndicatorIsReady), "PublishStateSnapshot");
		}

	private void PublishProperty (string propertyId, DriverEntityValue value, string context)
		{
		NotifyPropertyChanged (propertyId, DriverEntityValueUpdate.Create (value));
		}

	private void SetAndNotify (string propertyId, bool value, ref bool field)
		{
		if (field == value)
			{
			return;
			}

		field = value;
		PublishProperty (propertyId, new DriverEntityValue (value), "SetAndNotify<bool>");
		}

	private void SetAndNotify (string propertyId, double value, ref double field)
		{
		if (Math.Abs (field - value) < 0.0001d)
			{
			return;
			}

		field = value;
		PublishProperty (propertyId, new DriverEntityValue (value), "SetAndNotify<double>");
		}

	private void SetAndNotify (string propertyId, string value, ref string field)
		{
		if (string.Equals (field, value, StringComparison.Ordinal))
			{
			return;
			}

		field = value;
		PublishProperty (propertyId, new DriverEntityValue (value), "SetAndNotify<string>");
		}

	private void ApplyState (ChildDevice? child)
		{
		bool anyState = false;

		if (child is not null)
			{
			int? batteryLevel = child.Battery.BatteryLevel;
			bool? batteryLow = child.Battery.BatteryLow;
			if (batteryLevel.HasValue || batteryLow.HasValue)
				{
				BatteryLevelPercent = batteryLevel ?? BatteryLevelPercent;
				BatteryIsLow = batteryLow ?? false;
				HasBattery = true;
				BatteryStatusLabel = batteryLevel.HasValue
					? $"{batteryLevel.Value}%{(BatteryIsLow ? " (Low)" : string.Empty)}"
					: (BatteryIsLow ? "Low" : "Normal");
				anyState = true;
				}

			IReadOnlyList<ChildTriggerLogEntry> logs = child.TriggerLogs.Logs;
			LogInfo ($"Button entity '{ControllerId}' ApplyState: doubleClickEnabled={(child.DoubleClick.Enabled?.ToString () ?? "unreported")}, triggerLogCount={logs.Count}, lastSeenTimestamp={(_lastSeenTriggerTimestamp?.ToString () ?? "none")}, entries=[{FormatTriggerLogEntries (logs)}].");
			if (logs.Count > 0)
				{
				ChildTriggerLogEntry latest = logs[0];
				if (!_lastSeenTriggerTimestamp.HasValue || latest.Timestamp != _lastSeenTriggerTimestamp)
					{
					bool isFirstObservation = !_lastSeenTriggerTimestamp.HasValue;
					long? previousSeen = _lastSeenTriggerTimestamp;
					_lastSeenTriggerTimestamp = latest.Timestamp;
					LastTriggerType = ResolveEventName (latest);

					// The very first poll after configuration just establishes the baseline: the
					// newest entry already in the device's trigger log is historical, not a press
					// that happened just now, so raising ButtonTriggered for it would fire a
					// spurious event (and could trigger a sequence) on every driver load.
					if (isFirstObservation)
						{
						UpdateTriggerDisplay (latest.Timestamp);
						LogInfo ($"Button entity '{ControllerId}' ApplyState: baselining trigger log at timestamp={latest.Timestamp}, event='{LastTriggerType}'; not raising ButtonTriggered.");
						}
					else
						{
						// The device buffers several gestures between polls, so a double-click or
						// hold/release that happened after the previous poll would be lost if only
						// logs[0] were examined. Replay every entry newer than the last one seen,
						// oldest first, so each gesture raises its own discrete event.
						for (int index = logs.Count - 1; index >= 0; index--)
							{
							ChildTriggerLogEntry entry = logs[index];
							if (!entry.Timestamp.HasValue || (previousSeen.HasValue && entry.Timestamp.Value <= previousSeen.Value))
								{
								continue;
								}

							string entryEvent = ResolveEventName (entry);
							LastTriggerType = entryEvent;
							LastTriggerTime = (double)entry.Timestamp.Value;
							UpdateTriggerDisplay (entry.Timestamp);

							LogInfo ($"Button entity '{ControllerId}' ApplyState: new trigger detected, timestamp={entry.Timestamp}, event='{entryEvent}'; raising ButtonTriggered (subscribers={_buttonTriggered?.GetInvocationList ().Length ?? 0}).");

							// Consecutive presses of the same kind produce identical property values, so
							// SetAndNotify suppresses the update as a no-op change and the UI never sees
							// the press. Republish explicitly so every trigger reaches the UI.
							PublishProperty ("lastTriggerType", new DriverEntityValue (LastTriggerType), "ApplyState:trigger");
							PublishProperty ("lastTriggerDisplay", new DriverEntityValue (LastTriggerDisplay), "ApplyState:trigger");
							PublishProperty ("lastGestureLabel", new DriverEntityValue (LastGestureLabel), "ApplyState:trigger");
							PublishProperty ("lastTriggerTimeDisplay", new DriverEntityValue (LastTriggerTimeDisplay), "ApplyState:trigger");

							_buttonTriggered?.Invoke (this, EventArgs.Empty);
							RaiseGestureEvent (entryEvent);
							}
						}
					}

				anyState = true;
				}
			}

		ButtonIcon = anyState ? "icGenericDeviceOn" : "icGenericDeviceOff";
		}

	// Formats the newest trigger for the tile as a short gesture label on line 1 and the 24-hour
	// time (including seconds, since presses often occur within the same minute) on line 2.
	private void UpdateTriggerDisplay (long? timestamp)
		{
		string pressType = FormatGestureLabel (LastTriggerType);
		LastGestureLabel = pressType;

		if (timestamp.HasValue)
			{
			DateTime local = DateTimeOffset.FromUnixTimeSeconds (timestamp.Value).ToLocalTime ().DateTime;
			LastTriggerTimeDisplay = local.ToString ("HH:mm:ss", CultureInfo.InvariantCulture);
			LastTriggerDisplay = $"{pressType}\n{LastTriggerTimeDisplay}";
			}
		else
			{
			LastTriggerTimeDisplay = string.Empty;
			LastTriggerDisplay = pressType;
			}
		}

	// The device reports the gesture in the trigger-log 'event' field, but some firmware only
	// populates 'eventId'. Prefer the event name and fall back to the id so double-click and
	// hold/release are still recognized.
	private static string ResolveEventName (ChildTriggerLogEntry entry)
		{
		if (!string.IsNullOrWhiteSpace (entry.EventName))
			{
			return entry.EventName!;
			}

		return entry.EventId ?? string.Empty;
		}

	private static string FormatTriggerLogEntries (IReadOnlyList<ChildTriggerLogEntry> logs)
		{
		var parts = new List<string> (logs.Count);
		foreach (ChildTriggerLogEntry entry in logs)
			{
			parts.Add ($"{entry.Timestamp}:event='{entry.EventName}',eventId='{entry.EventId}'");
			}

		return string.Join (" | ", parts);
		}

	// Maps the device's raw trigger-log event name to a short, friendly gesture label.
	private static string FormatGestureLabel (string eventName)
		{
		if (string.IsNullOrEmpty (eventName))
			{
			return "Press";
			}

		if (IsGesture (eventName, "double"))
			{
			return "Double";
			}

		if (IsGesture (eventName, "long") || IsGesture (eventName, "hold"))
			{
			return "Hold";
			}

		if (IsGesture (eventName, "release"))
			{
			return "Release";
			}

		if (IsGesture (eventName, "single") || IsGesture (eventName, "click"))
			{
			return "Single";
			}

		return eventName;
		}

	private static bool IsGesture (string eventName, string token)
		{
		return eventName.IndexOf (token, StringComparison.OrdinalIgnoreCase) >= 0;
		}

	// Dispatches the discrete per-gesture event so Crestron Home can react to single, double,
	// hold and release independently. The device reports the gesture in the trigger-log entry's
	// event name; unrecognized names still raise the aggregate ButtonTriggered above.
	private void RaiseGestureEvent (string eventName)
		{
		if (string.IsNullOrEmpty (eventName))
			{
			return;
			}

		EventHandler? handler;
		string gesture;

		if (IsGesture (eventName, "double"))
			{
			handler = _doublePressed;
			gesture = "DoublePressed";
			}
		else if (IsGesture (eventName, "long") || IsGesture (eventName, "hold"))
			{
			handler = _held;
			gesture = "Held";
			}
		else if (IsGesture (eventName, "release"))
			{
			handler = _released;
			gesture = "Released";
			}
		else if (IsGesture (eventName, "single") || IsGesture (eventName, "click"))
			{
			handler = _singlePressed;
			gesture = "SinglePressed";
			}
		else
			{
			LogInfo ($"Button entity '{ControllerId}' RaiseGestureEvent: unrecognized trigger event name '{eventName}'; only ButtonTriggered was raised.");
			return;
			}

		LogInfo ($"Button entity '{ControllerId}' RaiseGestureEvent: event='{eventName}' mapped to {gesture} (subscribers={handler?.GetInvocationList ().Length ?? 0}).");
		handler?.Invoke (this, EventArgs.Empty);
		}

	private void UpdateDescriptorFromConnectedDevice (KasaDevice device)
		{
		string? childAlias = !string.IsNullOrWhiteSpace (_descriptor.ChildId)
			? device.GetChild (_descriptor.ChildId!)?.Alias
			: null;

		string resolvedAlias = !string.IsNullOrWhiteSpace (childAlias)
			? childAlias!
			: _descriptor.Name;

		_descriptor.Name = resolvedAlias;
		_descriptor.AwaitingConnectedIdentity = false;

		DeviceName = _descriptor.Name;
		ModelName = _descriptor.ModelName;
		SerialNumber = _descriptor.SerialNumber;
		DeviceLabel = _descriptor.Name;
		}

	[Conditional ("DEBUG")]
	private void LogInfo (string message)
		{
		_logger?.Log (_driverLogId, LogEntryLevel.Info, message);
		}

	private void LogError (string message)
		{
		_logger?.Log (_driverLogId, LogEntryLevel.Error, message);
		}
	}
