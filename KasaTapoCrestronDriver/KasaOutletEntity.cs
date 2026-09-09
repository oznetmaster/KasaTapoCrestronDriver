// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.IO;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.EntityModel.Logging;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

namespace KasaTapoCrestronDriver;

/// <summary>
/// Standalone managed entity for plug/outlet children (<see cref="ManagedChildKind.Outlet"/>).
/// Unlike <see cref="KasaLightEntity"/>, this class exposes only on/off control plus - when the
/// connected root device reports it - energy usage telemetry. It deliberately does not derive
/// from or share implementation with <see cref="KasaLightEntity"/>: that class is a large,
/// tightly-coupled light-specific implementation, and outlets have their own, simpler UI
/// definition and lifecycle needs. Per-child-kind UI definitions are expected for all future
/// non-light device kinds as well.
/// </summary>
internal sealed partial class KasaOutletEntity : ReflectedAttributeDriverEntity, IKasaManagedChildEntity, IParentDeviceChild
	{
	// Startup/reconnect timing mirrors KasaLightEntity's tuning: a cold TPAP PAKE handshake after
	// a long-idle period can legitimately take this long, so both the connect and command phases
	// need a realistic, generous budget.
	private static readonly TimeSpan StartupConnectTimeout = TimeSpan.FromSeconds (20);
	private static readonly TimeSpan DeviceConnectTimeout = StartupConnectTimeout;
	private static readonly TimeSpan DeviceCommandTimeout = StartupConnectTimeout;
	private static readonly TimeSpan DeviceCommandRetryDelay = TimeSpan.FromSeconds (1);
	private const int DEVICE_COMMAND_MAX_ATTEMPTS = 2;

	private readonly DriverControllerLogger _logger;
	private readonly string _driverLogId;
	private readonly Action<ManagedLightDescriptor>? _descriptorUpdated;
	private readonly IPlatformSharedConfiguration _sharedConfiguration;
	private readonly CancellationTokenSource _lifetimeCancellationSource = new ();
	private readonly IComponentLogger? _uiDefinitionLogger;
	private readonly string? _basicUiDefinitionFilePath;
	private readonly string? _energyUiDefinitionFilePath;
	private UiDefinitionProperty? _uiDefinition;
	private bool _usingEnergyUiDefinition;

	private ManagedLightDescriptor _descriptor = null!;
	private DeviceConfiguration? _configuration;
	private KasaDevice? _connectedDevice;
	private bool _isConfigured;
	private bool _childPublished;
	private bool _energyAvailable;
	private int _stopState;
	private int _pollingGeneration;
	private Task? _pollingTask;
	private bool _disposed;

	public string DeviceName { get; private set; } = string.Empty;

	public string ModelName { get; private set; } = string.Empty;

	public string SerialNumber { get; private set; } = string.Empty;

	// The outlet UI definition's MainPage layout binds title="{deviceLabel}"; without this
	// property the binding never resolves. Overkiz's OverkizShadeEntity exposes the equivalent
	// "deviceLabel" EntityProperty for the same reason.
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

	[EntityProperty (Id = "outletIsOn")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public bool OutletIsOn
		{
		get;
		private set => SetAndNotify ("outletIsOn", value, ref field);
		}

	[EntityProperty (Id = "outletEnergyCurrentPowerWatts")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public double OutletEnergyCurrentPowerWatts
		{
		get;
		private set => SetAndNotify ("outletEnergyCurrentPowerWatts", value, ref field);
		}

	[EntityProperty (Id = "outletEnergyTodayKilowattHours")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public double OutletEnergyTodayKilowattHours
		{
		get;
		private set => SetAndNotify ("outletEnergyTodayKilowattHours", value, ref field);
		}

	[EntityProperty (Id = "outletEnergyMonthKilowattHours")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public double OutletEnergyMonthKilowattHours
		{
		get;
		private set => SetAndNotify ("outletEnergyMonthKilowattHours", value, ref field);
		}

	[EntityProperty (Id = "outletEnergyTotalKilowattHours")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public double OutletEnergyTotalKilowattHours
		{
		get;
		private set => SetAndNotify ("outletEnergyTotalKilowattHours", value, ref field);
		}

	[EntityProperty (Id = "outletEnergyVoltageVolts")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public double OutletEnergyVoltageVolts
		{
		get;
		private set => SetAndNotify ("outletEnergyVoltageVolts", value, ref field);
		}

	[EntityProperty (Id = "outletEnergyCurrentAmps")]
	[EntityPropertyMetadata (Programmable = true, ExtensionUiProperty = true)]
	public double OutletEnergyCurrentAmps
		{
		get;
		private set => SetAndNotify ("outletEnergyCurrentAmps", value, ref field);
		}

	[EntityProperty (Id = "hasEnergyReporting")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public bool HasEnergyReporting
		{
		get;
		private set => SetAndNotify ("hasEnergyReporting", value, ref field);
		}

	[EntityProperty (Id = "outletStatus")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string OutletStatus
		{
		get;
		private set => SetAndNotify ("outletStatus", value, ref field);
		} = string.Empty;

	// icOutlet does not exist in Crestron's extension-device icon set (see
	// Extension-Device-Icons.pdf); icGenericDeviceOn/icGenericDeviceOff are bound dynamically so
	// the tile reflects live state.
	[EntityProperty (Id = "outletIcon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string OutletIcon
		{
		get;
		private set => SetAndNotify ("outletIcon", value, ref field);
		} = "icGenericDeviceOff";

	// Discrete on/off transition events give Crestron Home's Actions & Events / sequences a way to
	// trigger directly off a state change (e.g. "when this outlet turns on, do X"), rather than only
	// being able to poll outletIsOn. Mirrors the door lock example from the extension events docs.
	[EntityEvent (Id = "outletTurnedOn", FriendlyName = "Outlet Turned On", NameLocalizationKey = "Event_OutletTurnedOn")]
	[EntityEventMetadata (Programmable = true, TriggeredByCommands = new[] { "outletOn", "outletToggle", "setOutletIsOn" })]
	public event EventHandler OutletTurnedOn = null!;

	[EntityEvent (Id = "outletTurnedOff", FriendlyName = "Outlet Turned Off", NameLocalizationKey = "Event_OutletTurnedOff")]
	[EntityEventMetadata (Programmable = true, TriggeredByCommands = new[] { "outletOff", "outletToggle", "setOutletIsOn" })]
	public event EventHandler OutletTurnedOff = null!;

	[EntityCommand (Id = "outletOff")]
	[EntityCommandMetadata (Programmable = true)]
	public void OutletOff ()
		{
		LogInfo ($"Outlet entity '{ControllerId}' command invoked: outletOff.");
		OutletIsOn = false;
		OutletStatus = "Off";
		OutletIcon = "icGenericDeviceOff";
		OutletTurnedOff?.Invoke (this, EventArgs.Empty);
		StartBackgroundOperation (() => ExecuteDeviceCommandAsync ((device, cancellationToken) => StripOutletControl.SetIsOnAsync (device, _descriptor.ChildId, false, cancellationToken), _lifetimeCancellationSource.Token), "outletOff");
		}

	[EntityCommand (Id = "outletOn")]
	[EntityCommandMetadata (Programmable = true)]
	public void OutletOn ()
		{
		LogInfo ($"Outlet entity '{ControllerId}' command invoked: outletOn.");
		OutletIsOn = true;
		OutletStatus = "On";
		OutletIcon = "icGenericDeviceOn";
		OutletTurnedOn?.Invoke (this, EventArgs.Empty);
		StartBackgroundOperation (() => ExecuteDeviceCommandAsync ((device, cancellationToken) => StripOutletControl.SetIsOnAsync (device, _descriptor.ChildId, true, cancellationToken), _lifetimeCancellationSource.Token), "outletOn");
		}

	[EntityCommand (Id = "outletToggle")]
	[EntityCommandMetadata (Programmable = true)]
	public void OutletToggle ()
		{
		LogInfo ($"Outlet entity '{ControllerId}' command invoked: outletToggle.");
		if (OutletIsOn)
			{
			OutletOff ();
			}
		else
			{
			OutletOn ();
			}
		}

	// ExtensionSetPropertyValueExecutor (extension:setPropertyValue) does not write the property
	// directly; it maps property "outletIsOn" to a command literally named "setOutletIsOn"
	// ("set" + capitalized property name) and invokes it with a "value" parameter. Without this
	// command the main-page toggle control (which writes {outletIsOn} rather than invoking
	// outletToggle/outletOn/outletOff) silently does nothing - the tile still works because it
	// uses an explicit action="command:outletToggle" instead of a value binding.
	[EntityCommand (Id = "setOutletIsOn")]
	[EntityCommandMetadata (Programmable = true)]
	public void SetOutletIsOn (bool value)
		{
		LogInfo ($"Outlet entity '{ControllerId}' command invoked: setOutletIsOn({value}).");
		if (value)
			{
			OutletOn ();
			}
		else
			{
			OutletOff ();
			}
		}

	public KasaOutletEntity (
		string controllerId,
		ManagedLightDescriptor descriptor,
		DeviceConfiguration configuration,
		Action<ManagedLightDescriptor>? descriptorUpdated,
		IPlatformSharedConfiguration sharedConfiguration,
		DriverImplementationResources resources,
		DriverControllerLogger logger,
		string driverLogId,
		string? driverDataDirectoryPath = null, ManagedParentDevicePoller? parentPoller = null)
		: base (controllerId)
		{
		_parentPoller = parentPoller;
		_descriptorUpdated = descriptorUpdated;
		_sharedConfiguration = sharedConfiguration;
		_logger = logger;
		_driverLogId = driverLogId;

		UpdateDescriptor (descriptor, configuration);

		// Outlet is a Crestron extension device type (unlike Light, which is native), so it
		// requires its own packaged UI definition/translation assets. Mirrors the per-kind
		// "{root}/outlet/uidefinitions/" layout used by other Entity V2 drivers. Two variants
		// ship in that folder: a minimal, tile-only definition for outlets with no power/energy
		// module, and a fuller one (with a MainPage layout showing energy telemetry) for outlets
		// that do have one. Whether the connected device has an energy module is only known once
		// it has actually been queried, so the entity is always constructed with the basic
		// (no-navigation) UI definition and is later swapped to the energy definition - see
		// ApplyState/SwitchToEnergyUiDefinition - the first time energy reporting is detected.
		var baseDir = driverDataDirectoryPath ?? Path.GetTempPath ();
		var outletUiDir = Path.Combine (baseDir, "outlet", "uidefinitions");
		_uiDefinitionLogger = resources.InitLogger;
		_basicUiDefinitionFilePath = Path.Combine (outletUiDir, "UiDefinitionBasic.xml");
		_energyUiDefinitionFilePath = Path.Combine (outletUiDir, "UiDefinitionEnergy.xml");
		LogInfo ($"Outlet entity '{ControllerId}' UiDefinition: driverDataDirectoryPath='{driverDataDirectoryPath}', outletUiDir='{outletUiDir}', exists={Directory.Exists (outletUiDir)}.");
		try
			{
			if (File.Exists (_basicUiDefinitionFilePath))
				{
				_uiDefinition = new UiDefinitionProperty (_basicUiDefinitionFilePath, _uiDefinitionLogger);
				_usingEnergyUiDefinition = false;
				}
			else
				{
				LogError ($"Outlet entity '{ControllerId}' UiDefinition load failed: file not found at '{_basicUiDefinitionFilePath}'.");
				}
			}
		catch (Exception ex)
			{
			LogError ($"Outlet entity '{ControllerId}' UiDefinition load failed: {ex.Message}");
			}

		LogInfo ($"Outlet entity '{ControllerId}' UiDefinition: loaded={_uiDefinition != null}.");

		try
			{
			AddProperty (this, UiDefinitionProperty.Name, _uiDefinition);
			}
		catch (Exception ex)
			{
			LogError ($"Outlet entity '{ControllerId}' AddProperty UiDefinition failed: {ex.Message}");
			}

		// The IDriverLogger overloads of these executors call resources.Logger.GetComponentLogger with a
		// fixed, non-unique component name ("CAPA - "/"ExtensionExecutor"), and the SDK's logger registry
		// throws ArgumentException if that same key is requested more than once. Since every KasaOutletEntity
		// in the driver would otherwise pass the same shared resources.Logger, only the first outlet entity
		// ever constructed would succeed - every subsequent one would throw here and never be materialized,
		// which is why outlet children were silently missing from the room. Requesting our own
		// per-controllerId component logger avoids the collision.
		IComponentLogger extensionExecutorLogger = resources.Logger.GetComponentLogger ("CAPA - ", $"ExtensionExecutor:{controllerId}");
		var doCommand = new ExtensionDoCommandExecutor (GetCommand, extensionExecutorLogger);
		AddCommand (this, ExtensionDoCommandExecutor.CommandName, doCommand);

		var setPropertyValue = new ExtensionSetPropertyValueExecutor (GetCommand, extensionExecutorLogger);
		AddCommand (this, ExtensionSetPropertyValueExecutor.CommandName, setPropertyValue);

		LogInfo ($"Outlet entity '{ControllerId}' created in passive discovered state; awaiting child configuration callback before activation.");
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
		_parentPoller?.UpdateConfiguration (configuration);
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
		LogInfo ($"Outlet entity '{ControllerId}' adopted connected device from context='{context}'.");
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
		LogInfo ($"Outlet entity '{ControllerId}' SetConfigured: configured={configured}, context='{context}'.");

		if (!configured)
			{
			_parentPoller?.UnregisterChild (this);
			Interlocked.Increment (ref _pollingGeneration);
			_pollingTask = null;
			return;
			}

		RestartPolling ();
		StartBackgroundOperation (InitializeStartupAsync, $"child-configured:{context}");
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
		LogInfo ($"Outlet entity '{ControllerId}' SetConfiguredAsync: configured={configured}, context='{context}'.");

		if (!configured)
			{
			_parentPoller?.UnregisterChild (this);
			Interlocked.Increment (ref _pollingGeneration);
			_pollingTask = null;
			return;
			}

		RestartPolling ();
		await InitializeConnectedStateAsync (cancellationToken).ConfigureAwait (false);
		}

	public void ApplyRuntimeConfiguration (PlatformSharedConfigurationSnapshot previousConfiguration, PlatformSharedConfigurationSnapshot currentConfiguration)
		{
		if (_disposed)
			{
			return;
			}

		bool pollingChanged = previousConfiguration.EnableLightPolling != currentConfiguration.EnableLightPolling
			|| previousConfiguration.LightPollInterval != currentConfiguration.LightPollInterval;

		if (pollingChanged)
			{
			RestartPolling ();
			}
		}

	public void Stop ()
		{
		if (Interlocked.Exchange (ref _stopState, 1) != 0)
			{
			return;
			}

		_parentPoller?.UnregisterChild (this);
		_lifetimeCancellationSource.Cancel ();
		Interlocked.Increment (ref _pollingGeneration);
		_pollingTask = null;
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

	public void NotifyChildPublished ()
		{
		if (_disposed)
			{
			return;
			}

		_childPublished = true;

		LogInfo ($"Outlet entity '{ControllerId}' NotifyChildPublished invoked.");

		// Unlike KasaLightEntity (a native Crestron device type, rendered by the SDK independent
		// of any explicit UI push), Outlet is a Crestron extension device type: the room only ever
		// renders a tile/controls for it once extension:uiDefinition has actually been notified.
		// Gating this snapshot on _connectedDevice being non-null (as this used to do) meant that
		// whenever the physical device's connect was slow, failed, or simply hadn't completed yet
		// by the time the child was published, the UI definition was never sent at all and the
		// outlet's room UI silently never appeared - even though the managed-device list (a
		// separate, ungated publication path) still showed it correctly. Overkiz's
		// OverkizShadeEntity.PushInitialState publishes unconditionally as soon as the child is
		// registered for exactly this reason; mirror that here.
		PublishStateSnapshot ();
		}

	public void NotifyChildRunning (string context)
		{
		if (_disposed)
			{
			return;
			}

		LogInfo ($"Outlet entity '{ControllerId}' NotifyChildRunning invoked, context='{context}'.");
		PublishStateSnapshot ();
		}

	public void PublishStateSnapshot ()
		{
		// The extension:uiDefinition property is registered via AddProperty in the constructor, but
		// registering a property definition does not itself push its value to the room UI - the SDK
		// requires an explicit NotifyPropertyChanged for the value to be sent. Overkiz's room/shade
		// entities do this in their initial-state push (TraceUiDefinitionNotification); without the
		// equivalent notification here, the outlet's UI definition was never delivered to the room,
		// so the device configures/comes online successfully but never renders any tile/controls.
		LogInfo ($"Outlet entity '{ControllerId}' PublishStateSnapshot invoked, uiDefinitionLoaded={_uiDefinition != null}.");
		if (_uiDefinition is not null)
			{
			DriverEntityValue? uiDefinitionValue = _uiDefinition.GetValue (this, null);
			LogInfo ($"Outlet entity '{ControllerId}' PublishStateSnapshot: uiDefinitionValue.HasValue={uiDefinitionValue.HasValue}.");
			if (uiDefinitionValue.HasValue)
				{
				NotifyPropertyChanged (UiDefinitionProperty.Name, uiDefinitionValue.Value);
				LogInfo ($"Outlet entity '{ControllerId}' PublishStateSnapshot: NotifyPropertyChanged({UiDefinitionProperty.Name}) sent.");
				}
			}

		PublishProperty ("deviceLabel", new DriverEntityValue (DeviceLabel), "PublishStateSnapshot");
		PublishProperty ("outletIsOn", new DriverEntityValue (OutletIsOn), "PublishStateSnapshot");
		PublishProperty ("outletIcon", new DriverEntityValue (OutletIcon), "PublishStateSnapshot");
		PublishProperty ("hasEnergyReporting", new DriverEntityValue (HasEnergyReporting), "PublishStateSnapshot");
		PublishProperty ("outletStatus", new DriverEntityValue (OutletStatus), "PublishStateSnapshot");
		if (_energyAvailable)
			{
			PublishProperty ("outletEnergyCurrentPowerWatts", new DriverEntityValue (OutletEnergyCurrentPowerWatts), "PublishStateSnapshot");
			PublishProperty ("outletEnergyTodayKilowattHours", new DriverEntityValue (OutletEnergyTodayKilowattHours), "PublishStateSnapshot");
			PublishProperty ("outletEnergyMonthKilowattHours", new DriverEntityValue (OutletEnergyMonthKilowattHours), "PublishStateSnapshot");
			PublishProperty ("outletEnergyTotalKilowattHours", new DriverEntityValue (OutletEnergyTotalKilowattHours), "PublishStateSnapshot");
			PublishProperty ("outletEnergyVoltageVolts", new DriverEntityValue (OutletEnergyVoltageVolts), "PublishStateSnapshot");
			PublishProperty ("outletEnergyCurrentAmps", new DriverEntityValue (OutletEnergyCurrentAmps), "PublishStateSnapshot");
			}

		// The online/ready indicators are published last so the host sees a complete payload
		// (UI definition plus every value property) before either indicator flips.
		//
		// NOTE: this ordering was introduced as an attempted fix for the Light -> Outlet
		// Configure Pro enumeration problem and it did NOT fix it - see the capture of
		// 2026-09-06 15:43:01, where extension:uiDefinition was sent at .511 ahead of every
		// indicator and the Load adapter was still removed at .067 of the following second by
		// the host's own "Validate driver definition" pass. That turned out not to be a driver
		// defect at all (a reload restores the correct outlet view, and the Outlet -> Light
		// direction refreshes correctly in-session; see the notes on
		// ReconcileChildKindAfterTreatAsLightChange). The ordering is kept only because sending
		// a complete payload before the indicators is defensible on its own terms.
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

	public async Task RefreshAsync (CancellationToken cancellationToken)
		{
		if (_parentPoller is not null)
			{
			await _parentPoller.RefreshAsync (cancellationToken).ConfigureAwait (false);
			return;
			}
		try
			{
			KasaDevice device = await EnsureConnectedAsync (cancellationToken).ConfigureAwait (false);
			await device.UpdateAsync (cancellationToken).ConfigureAwait (false);
			ApplyState (device);
			OnlineIndicatorIsOnline = true;
			ReadyIndicatorIsReady = true;
			}
		catch (OperationCanceledException)
			{
			throw;
			}
		catch (Exception ex)
			{
			OnlineIndicatorIsOnline = false;
			ReadyIndicatorIsReady = false;
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Outlet entity '{ControllerId}' refresh failed: {ex}");
			}
		}

	private void ApplyState (KasaDevice device)
		{
		OutletIsOn = StripOutletControl.ReadIsOn (device, _descriptor.ChildId);
		// Parent aggregate energy must not be displayed as one socket's consumption.
		_energyAvailable = string.IsNullOrWhiteSpace (_descriptor.ChildId) && (device.Energy?.IsAvailable ?? false);
		HasEnergyReporting = _energyAvailable;
		if (_energyAvailable)
			{
			OutletEnergyCurrentPowerWatts = device.Energy!.CurrentPowerWatts ?? 0d;
			OutletEnergyTodayKilowattHours = device.Energy!.TodayKilowattHours ?? 0d;
			OutletEnergyMonthKilowattHours = device.Energy!.MonthKilowattHours ?? 0d;
			OutletEnergyTotalKilowattHours = device.Energy!.TotalKilowattHours ?? 0d;
			OutletEnergyVoltageVolts = device.Energy!.VoltageVolts ?? 0d;
			OutletEnergyCurrentAmps = device.Energy!.CurrentAmps ?? 0d;
			}
		OutletStatus = OutletIsOn ? "On" : "Off";
		OutletIcon = OutletIsOn ? "icGenericDeviceOn" : "icGenericDeviceOff";
		EnsureUiDefinitionMatchesEnergyCapability ();
		}

	// The entity always starts with the basic (no-navigation, empty-layout) UI definition -
	// see the constructor - because energy-module presence is only known once the connected
	// device has actually been queried. Once ApplyState first observes energy reporting, swap
	// in the fuller energy-capable UI definition so the tile gains navigation to MainPage and
	// its energy telemetry controls. The reverse never happens: once a device has proven it has
	// an energy module it will not lose it, so there is no need to swap back to the basic UI.
	private void EnsureUiDefinitionMatchesEnergyCapability ()
		{
		if (!_energyAvailable || _usingEnergyUiDefinition || _uiDefinitionLogger is null || _energyUiDefinitionFilePath is null)
			{
			return;
			}

		if (!File.Exists (_energyUiDefinitionFilePath))
			{
			LogError ($"Outlet entity '{ControllerId}' UiDefinition switch to energy variant failed: file not found at '{_energyUiDefinitionFilePath}'.");
			return;
			}

		try
			{
			RemoveProperty (UiDefinitionProperty.Name);
			_uiDefinition = new UiDefinitionProperty (_energyUiDefinitionFilePath, _uiDefinitionLogger);
			AddProperty (this, UiDefinitionProperty.Name, _uiDefinition);
			RaiseDefinitionChangedEvent ();
			_usingEnergyUiDefinition = true;
			LogInfo ($"Outlet entity '{ControllerId}' UiDefinition switched to energy-capable variant.");
			if (_childPublished)
				{
				PublishStateSnapshot ();
				}
			}
		catch (Exception ex)
			{
			LogError ($"Outlet entity '{ControllerId}' UiDefinition switch to energy variant failed: {ex.Message}");
			}
		}

	private async Task InitializeStartupAsync ()
		{
		try
			{
			await InitializeConnectedStateAsync (_lifetimeCancellationSource.Token).ConfigureAwait (false);
			}
		catch (OperationCanceledException)
			{
			}
		catch (Exception ex)
			{
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Outlet entity '{ControllerId}' startup initialization failed: {ex}");
			}
		}

	private async Task InitializeConnectedStateAsync (CancellationToken cancellationToken)
		{
		KasaDevice device = await EnsureConnectedAsync (cancellationToken).ConfigureAwait (false);
		ApplyState (device);
		OnlineIndicatorIsOnline = true;
		ReadyIndicatorIsReady = true;
		if (_childPublished)
			{
			PublishStateSnapshot ();
			}
		}

	private async Task<KasaDevice> EnsureConnectedAsync (CancellationToken cancellationToken)
		{
		if (_parentPoller is not null) return await _parentPoller.ConnectSharedAsync (cancellationToken).ConfigureAwait (false);
		KasaDevice? existingDevice = Volatile.Read (ref _connectedDevice);
		if (existingDevice is not null)
			{
			return existingDevice;
			}

		DeviceConfiguration? configuration = _configuration;
		if (configuration is null)
			{
			throw new InvalidOperationException ($"Outlet entity '{ControllerId}' cannot connect because no device configuration was supplied by the platform.");
			}

		using CancellationTokenSource connectCancellationSource = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken, _lifetimeCancellationSource.Token);
		connectCancellationSource.CancelAfter (DeviceConnectTimeout);

		DeviceConfiguration startupConfiguration = configuration.Timeout < StartupConnectTimeout
			? new DeviceConfiguration (configuration.Host, configuration.Port, configuration.Credentials, configuration.ConnectionOptions, StartupConnectTimeout)
			: configuration;

		KasaDevice connectedDevice;
		try
			{
			connectedDevice = await Discover.GetOrConnectSharedAsync (startupConfiguration, updateState: true, cancellationToken: connectCancellationSource.Token).ConfigureAwait (false);
			}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && connectCancellationSource.IsCancellationRequested)
			{
			throw new TimeoutException ($"Outlet entity '{ControllerId}' connect timed out after {DeviceConnectTimeout.TotalSeconds:0} seconds.");
			}

		_connectedDevice = connectedDevice;
		UpdateDescriptorFromConnectedDevice (connectedDevice);
		return connectedDevice;
		}

	private void UpdateDescriptorFromConnectedDevice (KasaDevice device)
		{
		// Strip child outlets (_descriptor.ChildId set) must resolve their own alias from the
		// specific child entry - the root device's Alias/SystemInfo.Alias belongs to the strip
		// itself (e.g. "TP-LINK_Power Strip_4BCD"), not to any individual outlet on it.
		string? childAlias = !string.IsNullOrWhiteSpace (_descriptor.ChildId)
			? device.GetChild (_descriptor.ChildId!)?.Alias
			: null;

		string resolvedAlias = !string.IsNullOrWhiteSpace (childAlias)
			? childAlias!
			: !string.IsNullOrWhiteSpace (_descriptor.ChildId)
				? _descriptor.Name
				: !string.IsNullOrWhiteSpace (device.Alias)
					? device.Alias!
					: !string.IsNullOrWhiteSpace (device.SystemInfo?.Alias)
						? device.SystemInfo!.Alias!
						: _descriptor.Name;

		LogInfo ($"Outlet entity '{ControllerId}' UpdateDescriptorFromConnectedDevice: resolvedAlias='{resolvedAlias}', childId='{_descriptor.ChildId ?? "<null>"}', childAlias='{childAlias ?? "<null>"}', deviceAlias='{device.Alias ?? "<null>"}', systemInfoAlias='{device.SystemInfo?.Alias ?? "<null>"}', previousName='{_descriptor.Name ?? "<null>"}'.");

		_descriptor.Name = resolvedAlias;
		_descriptor.AwaitingConnectedIdentity = false;

		DeviceName = _descriptor.Name;
		ModelName = _descriptor.ModelName;
		SerialNumber = _descriptor.SerialNumber;
		DeviceLabel = _descriptor.Name;
		LogInfo ($"Outlet entity '{ControllerId}' invoking descriptor update callback with name='{_descriptor.Name}', model='{_descriptor.ModelName}', serial='{_descriptor.SerialNumber}'.");
		_descriptorUpdated?.Invoke (_descriptor);
		LogInfo ($"Outlet entity '{ControllerId}' descriptor update callback completed.");
		}

	private void StartBackgroundOperation (Func<Task> operation, string operationName)
		{
		Task task = Task.Run (() => operation ());
		task.ContinueWith (
			continuationTask =>
				{
				if (continuationTask.IsFaulted)
					{
					Exception exception = continuationTask.Exception?.GetBaseException () ?? continuationTask.Exception!;
					_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Outlet entity '{ControllerId}' background operation '{operationName}' failed: {exception}");
					}
				},
			CancellationToken.None,
			TaskContinuationOptions.None,
			TaskScheduler.Default);
		}

	private async Task ExecuteDeviceCommandAsync (Func<KasaDevice, CancellationToken, Task> action, CancellationToken cancellationToken)
		{
		for (int attempt = 1; ; attempt++)
			{
			bool isFinalAttempt = attempt >= DEVICE_COMMAND_MAX_ATTEMPTS;
			try
				{
				if (_parentPoller is not null)
					{
					await _parentPoller.ExecuteCommandAsync (action, cancellationToken).ConfigureAwait (false);
					return;
					}
				KasaDevice device = await EnsureConnectedAsync (cancellationToken).ConfigureAwait (false);
				using CancellationTokenSource timeoutCancellationSource = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken);
				timeoutCancellationSource.CancelAfter (DeviceCommandTimeout);
				await action (device, timeoutCancellationSource.Token).ConfigureAwait (false);
				await device.UpdateAsync (timeoutCancellationSource.Token).ConfigureAwait (false);
				ApplyState (device);
				OnlineIndicatorIsOnline = true;
				ReadyIndicatorIsReady = true;
				PublishStateSnapshot ();
				return;
				}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
				throw;
				}
			catch (Exception ex) when (!isFinalAttempt)
				{
				_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Outlet entity '{ControllerId}' command attempt {attempt} of {DEVICE_COMMAND_MAX_ATTEMPTS} failed; retrying after {DeviceCommandRetryDelay.TotalSeconds:0} second(s): {ex}");
				_connectedDevice = null;
				await Task.Delay (DeviceCommandRetryDelay, cancellationToken).ConfigureAwait (false);
				}
			catch (Exception ex)
				{
				_connectedDevice = null;
				OnlineIndicatorIsOnline = false;
				ReadyIndicatorIsReady = false;
				_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Outlet entity '{ControllerId}' command failed: {ex}");
				return;
				}
			}
		}

	private void RestartPolling ()
		{
		if (UseParentPolling ()) return;
		int generation = Interlocked.Increment (ref _pollingGeneration);
		_pollingTask = RunPollingCycleAsync (generation);
		}

	private async Task RunPollingCycleAsync (int generation)
		{
		try
			{
			await Task.Delay (_sharedConfiguration.LightPollInterval, _lifetimeCancellationSource.Token).ConfigureAwait (false);

			if (_disposed || Volatile.Read (ref _stopState) != 0 || generation != Volatile.Read (ref _pollingGeneration))
				{
				return;
				}

			if (_sharedConfiguration.EnableLightPolling)
				{
				await RefreshAsync (_lifetimeCancellationSource.Token).ConfigureAwait (false);
				PublishStateSnapshot ();
				}

			if (_disposed || Volatile.Read (ref _stopState) != 0 || generation != Volatile.Read (ref _pollingGeneration))
				{
				return;
				}

			_pollingTask = RunPollingCycleAsync (generation);
			}
		catch (OperationCanceledException)
			{
			}
		catch (Exception ex)
			{
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Outlet entity '{ControllerId}' polling loop failed: {ex}");
			}
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
