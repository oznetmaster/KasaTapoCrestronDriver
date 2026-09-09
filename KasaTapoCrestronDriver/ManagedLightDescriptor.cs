// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE file in the project root for full license information.

namespace KasaTapoCrestronDriver;

internal enum ManagedLightKind
	{
	Unknown,
	Dimmable,
	TunableWhite,
	Color,
	OnOff
	}

/// <summary>
/// Identifies which concrete entity type a managed child controllerId is materialized as:
/// <see cref="KasaLightEntity"/>, <see cref="KasaOutletEntity"/>, a hub child sensor
/// (<see cref="KasaSensorEntity"/>), a hub child button, or a hub child thermostat/TRV.
/// Every new value added here must be handled explicitly everywhere this enum is switched on
/// (see CreateManagedDeviceCacheEntry / ResolveCachedChildKind) - silently defaulting an unknown
/// kind to Light previously hid classification bugs and must never happen again.
/// </summary>
internal enum ManagedChildKind
	{
	Light,
	Outlet,
	Sensor,
	Button,
	Thermostat
	}

/// <summary>
/// Identifies the specific telemetry category a hub child sensor/button descriptor represents.
/// Populated only when <see cref="ManagedLightDescriptor.ChildKind"/> is <see cref="ManagedChildKind.Sensor"/>
/// or <see cref="ManagedChildKind.Button"/>; every other child kind leaves this at
/// <see cref="None"/>. Every new value added here must be handled explicitly by
/// <see cref="KasaSensorEntity"/>/<see cref="KasaButtonEntity"/> - silently ignoring an unknown
/// hub child category would hide classification bugs the same way an unhandled
/// <see cref="ManagedChildKind"/> would.
/// </summary>
internal enum HubChildCategory
	{
	None,
	Temperature,
	Humidity,
	TemperatureHumidity,
	Contact,
	Motion,
	WaterLeak,
	Button
	}

internal sealed class ManagedLightDescriptor
	{
	public ManagedLightDescriptor (
		string controllerId,
		string host,
		DeviceType discoveredDeviceType,
		string name,
		string modelName,
		string serialNumber,
		ManagedLightKind kind,
		bool awaitingConnectedIdentity = false,
		string? discoveryDeviceId = null,
		string? childId = null,
		ManagedChildKind childKind = ManagedChildKind.Light,
		HubChildCategory hubChildCategory = HubChildCategory.None)
		{
		ControllerId = controllerId;
		Host = host;
		DiscoveredDeviceType = discoveredDeviceType;
		Name = name;
		ModelName = modelName;
		SerialNumber = serialNumber;
		if (kind != ManagedLightKind.Unknown)
			{
			Kind = kind;
			}
		AwaitingConnectedIdentity = awaitingConnectedIdentity;
		DiscoveryDeviceId = discoveryDeviceId;
		ChildId = childId;
		ChildKind = childKind;
		HubChildCategory = hubChildCategory;
		}

	public string ControllerId
		{
		get;
		}

	public string Host
		{
		get;
		}

	public DeviceType DiscoveredDeviceType
		{
		get;
		}

	public string Name
		{
		get;
		internal set;
		}

	public string ModelName
		{
		get;
		}

	public string SerialNumber
		{
		get;
		}

	public ManagedLightKind Kind
		{
		get
			;
		internal set
			{
			if (value == ManagedLightKind.Unknown)
				{
				throw new InvalidOperationException ($"Managed light kind for controllerId='{ControllerId}' cannot be set to Unknown.");
				}

			if (field == value)
				{
				return;
				}

			if (field != ManagedLightKind.Unknown)
				{
				throw new InvalidOperationException ($"Managed light kind for controllerId='{ControllerId}' cannot be changed from {field} to {value} after initialization.");
				}

			field = value;
			}
		}

	public bool AwaitingConnectedIdentity
		{
		get;
		internal set;
		}

	public string? DiscoveryDeviceId
		{
		get;
		}

	public string? ChildId
		{
		get;
		}

	/// <summary>
	/// Whether this controllerId is currently materialized as a Light entity or an Outlet entity.
	/// </summary>
	public ManagedChildKind ChildKind
		{
		get;
		internal set;
		}

	/// <summary>
	/// Identifies the hub child telemetry category (temperature/humidity/contact/motion/button)
	/// when <see cref="ChildKind"/> is <see cref="ManagedChildKind.Sensor"/> or
	/// <see cref="ManagedChildKind.Button"/>. Unused for every other child kind.
	/// </summary>
	public HubChildCategory HubChildCategory
		{
		get;
		internal set;
		}
	}

/// <summary>
/// Common lifecycle contract shared by both managed Light and Outlet child entities.
/// </summary>
internal interface IKasaManagedChildEntity : IDisposable
	{
	string DeviceName
		{
		get;
		}

	string ModelName
		{
		get;
		}

	string SerialNumber
		{
		get;
		}

	void UpdateDescriptor (ManagedLightDescriptor descriptor, DeviceConfiguration configuration);

	void UpdateConfiguration (DeviceConfiguration configuration);

	bool TryAttachConnectedDevice (KasaDevice device, string context);

	void SetConfigured (bool configured, string context);

	Task SetConfiguredAsync (bool configured, string context, CancellationToken cancellationToken);

	void ApplyRuntimeConfiguration (PlatformSharedConfigurationSnapshot previousConfiguration, PlatformSharedConfigurationSnapshot currentConfiguration);

	void Stop ();

	Task RefreshAsync (CancellationToken cancellationToken);

	void NotifyChildPublished ();

	void NotifyChildRunning (string context);

	void PublishStateSnapshot ();
	}

internal interface IKasaManagedLightEntity : IKasaManagedChildEntity
	{
	}

/// <summary>
/// Contract for hub child entities (<see cref="KasaSensorEntity"/>, <see cref="KasaButtonEntity"/>)
/// that never poll on their own. Instead, a single <see cref="ManagedParentDevicePoller"/> per
/// physical hub owns the connection and the polling loop, and pushes each registered child its
/// own current state once per poll tick via <see cref="ApplyPushedState"/> - or reports a failed
/// poll via <see cref="ApplyConnectionState"/> - rather than every child independently calling
/// <c>device.UpdateAsync</c> and redundantly re-fetching the whole hub's child list.
/// </summary>
internal interface IKasaHubChildEntity : IKasaManagedChildEntity, IParentDeviceChild
	{
	/// <summary>
	/// Whether at least one of this entity's <c>[EntityEvent]</c>-attributed events (e.g.
	/// <c>MotionDetectedEvent</c>, <c>BatteryLowEvent</c>, <c>ButtonTriggered</c>) currently has a
	/// subscriber. The owning <see cref="ManagedParentDevicePoller"/> polls the hub only while at
	/// least one of its children reports <c>true</c> here - a hub whose children have nothing
	/// subscribed has nothing for a poll to drive, so the poller stops entirely rather than
	/// needlessly hitting the device over the network.
	/// </summary>
	bool HasEventSubscribers
		{
		get;
		}

	/// <summary>
	/// Raised by this entity whenever a subscriber is added to or removed from any of its
	/// <c>[EntityEvent]</c>-attributed events, i.e. whenever <see cref="HasEventSubscribers"/> may
	/// have changed. This is what makes the hub polling lifecycle genuinely event driven: the
	/// owning <see cref="ManagedParentDevicePoller"/> subscribes to this while the child is
	/// registered and uses it to start polling the moment the first subscriber appears on any of
	/// its children, and to stop polling as soon as the last one goes away - instead of only
	/// noticing subscription changes on the next timer tick.
	/// </summary>
	event Action? EventSubscribersChanged;

	}
