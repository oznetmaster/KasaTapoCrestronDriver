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
internal interface IKasaHubChildEntity : IKasaManagedChildEntity
	{
	/// <summary>
	/// The hub child's own <c>ChildId</c> (see <see cref="ManagedLightDescriptor.ChildId"/>),
	/// used by the owning <see cref="ManagedParentDevicePoller"/> to look up this entity's own
	/// slice of the hub's child list on each poll tick.
	/// </summary>
	string ChildId
		{
		get;
		}

	/// <summary>
	/// Whether at least one of this entity's <c>[EntityEvent]</c>-attributed events (e.g.
	/// <c>MotionDetectedEvent</c>, <c>BatteryLowEvent</c>, <c>ButtonTriggered</c>) currently has a
	/// subscriber. All continuous property updates now happen only as a side effect of the delta
	/// checks that raise these events (see <see cref="ManagedParentDevicePoller"/>'s poll loop), so
	/// once nothing is subscribed to any of them there is nothing for a poll to usefully drive -
	/// the owning <see cref="ManagedParentDevicePoller"/> uses this to skip actually polling the
	/// hub on a given tick (while still checking again next tick) rather than needlessly hitting
	/// the device over the network. This does not affect the one-time initial read performed when
	/// the child first registers/gets configured, which always happens regardless of subscribers so
	/// properties have a correct starting value.
	/// </summary>
	bool HasEventSubscribers
		{
		get;
		}

	/// <summary>
	/// Called by the owning <see cref="ManagedParentDevicePoller"/> once per successful poll tick
	/// (and once immediately upon registration) with this entity's current <see cref="ChildDevice"/>
	/// (or <c>null</c> if the child was not found in the hub's most recent child list) and the
	/// shared, already-updated <see cref="KasaDevice"/> the child came from.
	/// </summary>
	void ApplyPushedState (ChildDevice? child, KasaDevice parentDevice);

	/// <summary>
	/// Called by the owning <see cref="ManagedParentDevicePoller"/> when a poll tick's connect or
	/// <c>UpdateAsync</c> call fails, so the entity can reflect the hub being unreachable (e.g.
	/// clear its online/ready indicators) without receiving stale pushed state.
	/// </summary>
	void ApplyConnectionState (bool online);
	}
