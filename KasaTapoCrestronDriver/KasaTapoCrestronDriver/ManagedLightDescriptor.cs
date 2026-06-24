using System;
using System.Threading;
using System.Threading.Tasks;

using KasaTapoClient;

namespace KasaTapoCrestronDriver;

internal enum ManagedLightKind
	{
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
		Kind = kind;
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
		get;
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

	void ApplyRuntimeConfiguration (PlatformSharedConfigurationSnapshot previousConfiguration, PlatformSharedConfigurationSnapshot currentConfiguration);

	void Stop ();

	Task RefreshAsync (CancellationToken cancellationToken);

	void PublishStateSnapshot ();
	}