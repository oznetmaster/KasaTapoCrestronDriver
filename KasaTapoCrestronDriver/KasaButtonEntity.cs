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
/// Standalone managed entity for hub child button devices (<see cref="ManagedChildKind.Button"/>),
/// such as a Tapo H100 hub's S200B smart button. Button children report battery state plus a
/// trigger log (single/double click, rotate); there is no persistent on/off state to poll, so
/// this entity surfaces the latest trigger as a discrete event rather than a level property.
/// It deliberately does not derive from or share implementation with
/// <see cref="KasaOutletEntity"/>/<see cref="KasaSensorEntity"/>: each child kind has its own,
/// simpler UI definition and lifecycle needs.
/// </summary>
internal sealed partial class KasaButtonEntity : ReflectedAttributeDriverEntity, IKasaManagedChildEntity
	{
	private static readonly TimeSpan StartupConnectTimeout = TimeSpan.FromSeconds (20);
	private static readonly TimeSpan DeviceConnectTimeout = StartupConnectTimeout;

	private readonly DriverControllerLogger _logger;
	private readonly string _driverLogId;
	private readonly Action<ManagedLightDescriptor>? _descriptorUpdated;
	private readonly IPlatformSharedConfiguration _sharedConfiguration;
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
	private int _pollingGeneration;
	private Task? _pollingTask;
	private bool _disposed;
	private long? _lastSeenTriggerTimestamp;

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

	[EntityProperty (Id = "buttonStatus")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string ButtonStatus
		{
		get;
		private set => SetAndNotify ("buttonStatus", value, ref field);
		} = string.Empty;

	[EntityProperty (Id = "buttonIcon")]
	[EntityPropertyMetadata (ExtensionUiProperty = true)]
	public string ButtonIcon
		{
		get;
		private set => SetAndNotify ("buttonIcon", value, ref field);
		} = "icGenericDeviceOff";

	[EntityEvent (Id = "buttonTriggered", FriendlyName = "Button Triggered", NameLocalizationKey = "Event_ButtonTriggered")]
	[EntityEventMetadata (Programmable = true)]
	public event EventHandler ButtonTriggered = null!;

	public KasaButtonEntity (
		string controllerId,
		ManagedLightDescriptor descriptor,
		DeviceConfiguration configuration,
		Action<ManagedLightDescriptor>? descriptorUpdated,
		IPlatformSharedConfiguration sharedConfiguration,
		DriverImplementationResources resources,
		DriverControllerLogger logger,
		string driverLogId,
		string? driverDataDirectoryPath = null)
		: base (controllerId)
		{
		_descriptorUpdated = descriptorUpdated;
		_sharedConfiguration = sharedConfiguration;
		_logger = logger;
		_driverLogId = driverLogId;

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
		LogInfo ($"Button entity '{ControllerId}' SetConfiguredAsync: configured={configured}, context='{context}'.");

		if (!configured)
			{
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
		PublishProperty ("buttonStatus", new DriverEntityValue (ButtonStatus), "PublishStateSnapshot");
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

	public async Task RefreshAsync (CancellationToken cancellationToken)
		{
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
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Button entity '{ControllerId}' refresh failed: {ex}");
			}
		}

	private void ApplyState (KasaDevice device)
		{
		ChildDevice? child = !string.IsNullOrWhiteSpace (_descriptor.ChildId)
			? device.GetChildDevice (_descriptor.ChildId!)
			: null;

		bool anyState = false;

		if (child is not null)
			{
			int? batteryLevel = child.Battery.BatteryLevel;
			bool? batteryLow = child.Battery.BatteryLow;
			if (batteryLevel.HasValue || batteryLow.HasValue)
				{
				BatteryLevelPercent = batteryLevel ?? BatteryLevelPercent;
				BatteryIsLow = batteryLow ?? false;
				anyState = true;
				}

			IReadOnlyList<ChildTriggerLogEntry> logs = child.TriggerLogs.Logs;
			if (logs.Count > 0)
				{
				ChildTriggerLogEntry latest = logs[0];
				if (!_lastSeenTriggerTimestamp.HasValue || latest.Timestamp != _lastSeenTriggerTimestamp)
					{
					_lastSeenTriggerTimestamp = latest.Timestamp;
					LastTriggerType = latest.EventName ?? string.Empty;
					ButtonTriggered?.Invoke (this, EventArgs.Empty);
					}

				anyState = true;
				}
			}

		ButtonStatus = anyState ? "Reporting" : "Unknown";
		ButtonIcon = anyState ? "icGenericDeviceOn" : "icGenericDeviceOff";
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
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Button entity '{ControllerId}' startup initialization failed: {ex}");
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
		KasaDevice? existingDevice = Volatile.Read (ref _connectedDevice);
		if (existingDevice is not null)
			{
			return existingDevice;
			}

		DeviceConfiguration? configuration = _configuration;
		if (configuration is null)
			{
			throw new InvalidOperationException ($"Button entity '{ControllerId}' cannot connect because no device configuration was supplied by the platform.");
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
			throw new TimeoutException ($"Button entity '{ControllerId}' connect timed out after {DeviceConnectTimeout.TotalSeconds:0} seconds.");
			}

		_connectedDevice = connectedDevice;
		UpdateDescriptorFromConnectedDevice (connectedDevice);
		return connectedDevice;
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

	private void StartBackgroundOperation (Func<Task> operation, string operationName)
		{
		Task task = Task.Run (() => operation ());
		task.ContinueWith (
			continuationTask =>
				{
				if (continuationTask.IsFaulted)
					{
					Exception exception = continuationTask.Exception?.GetBaseException () ?? continuationTask.Exception!;
					_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Button entity '{ControllerId}' background operation '{operationName}' failed: {exception}");
					}
				},
			CancellationToken.None,
			TaskContinuationOptions.None,
			TaskScheduler.Default);
		}

	private void RestartPolling ()
		{
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
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Button entity '{ControllerId}' polling loop failed: {ex}");
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
