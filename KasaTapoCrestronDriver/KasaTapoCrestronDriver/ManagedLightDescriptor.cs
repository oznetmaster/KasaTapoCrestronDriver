using System;
using System.Threading;
using System.Threading.Tasks;

using KasaTapoClient;

namespace KasaTapoCrestronDriver;

internal enum ManagedLightKind
	{
	Unknown,
	Dimmable,
	TunableWhite,
	Color,
	OnOff
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
		string? childId = null)
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
	}

internal interface IKasaManagedLightEntity : IDisposable
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

	void SetConfigured (bool configured, string context);

	Task SetConfiguredAsync (bool configured, string context, CancellationToken cancellationToken);

	void ApplyRuntimeConfiguration (PlatformSharedConfigurationSnapshot previousConfiguration, PlatformSharedConfigurationSnapshot currentConfiguration);

	void Stop ();

	Task RefreshAsync (CancellationToken cancellationToken);

	void NotifyChildPublished ();

	void PublishStateSnapshot ();
	}