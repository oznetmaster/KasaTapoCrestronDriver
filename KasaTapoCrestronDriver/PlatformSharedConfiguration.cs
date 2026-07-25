using System;

namespace KasaTapoCrestronDriver;

internal interface IPlatformSharedConfiguration
	{
	string UserName
		{
		get;
		}

	bool EnableProcessorBaselineWorkaround
		{
		get;
		}

	string ProcessorSshHost
		{
		get;
		}

	string ProcessorSshUserName
		{
		get;
		}

	string ProcessorSshPassword
		{
		get;
		}

	string Password
		{
		get;
		}

	TimeSpan DiscoveryTimeout
		{
		get;
		}

	bool EnableLightPolling
		{
		get;
		}

	TimeSpan LightPollInterval
		{
		get;
		}

	TimeSpan SensorPollInterval
		{
		get;
		}

	bool TreatPlugsAsLights
		{
		get;
		}

	PlatformSharedConfigurationSnapshot Snapshot ();
	}

internal readonly struct PlatformSharedConfigurationSnapshot
	{
	public PlatformSharedConfigurationSnapshot (
		string userName,
		string password,
		TimeSpan discoveryTimeout,
		bool enableLightPolling,
		TimeSpan lightPollInterval,
		TimeSpan sensorPollInterval,
		bool treatPlugsAsLights,
		bool enableProcessorBaselineWorkaround,
		string processorSshHost,
		string processorSshUserName,
		string processorSshPassword)
		{
		UserName = userName;
		Password = password;
		DiscoveryTimeout = discoveryTimeout;
		EnableLightPolling = enableLightPolling;
		LightPollInterval = lightPollInterval;
		SensorPollInterval = sensorPollInterval;
		TreatPlugsAsLights = treatPlugsAsLights;
		EnableProcessorBaselineWorkaround = enableProcessorBaselineWorkaround;
		ProcessorSshHost = processorSshHost;
		ProcessorSshUserName = processorSshUserName;
		ProcessorSshPassword = processorSshPassword;
		}

	public bool EnableProcessorBaselineWorkaround { get; }
	public string ProcessorSshHost { get; }
	public string ProcessorSshUserName { get; }
	public string ProcessorSshPassword { get; }

	public string UserName
		{
		get;
		}

	public string Password
		{
		get;
		}

	public TimeSpan DiscoveryTimeout
		{
		get;
		}

	public bool EnableLightPolling
		{
		get;
		}

	public TimeSpan LightPollInterval
		{
		get;
		}

	public TimeSpan SensorPollInterval
		{
		get;
		}

	public bool TreatPlugsAsLights
		{
		get;
		}
	}

internal sealed class PlatformSharedConfiguration : IPlatformSharedConfiguration
	{
	private readonly object _gate = new ();
	private string _userName = string.Empty;
	private string _password = string.Empty;
	private TimeSpan _discoveryTimeout = TimeSpan.FromSeconds (8);
	private bool _enableLightPolling;
	private TimeSpan _lightPollInterval = TimeSpan.FromSeconds (15);
	private TimeSpan _sensorPollInterval = TimeSpan.FromSeconds (3);
	private bool _treatPlugsAsLights;
	private bool _enableProcessorBaselineWorkaround;
	private string _processorSshHost = string.Empty;
	private string _processorSshUserName = string.Empty;
	private string _processorSshPassword = string.Empty;

	public string UserName
		{
		get
			{
			lock (_gate)
				{
				return _userName;
				}
			}
		}

	public bool EnableProcessorBaselineWorkaround { get { lock (_gate) { return _enableProcessorBaselineWorkaround; } } }
	public string ProcessorSshHost { get { lock (_gate) { return _processorSshHost; } } }
	public string ProcessorSshUserName { get { lock (_gate) { return _processorSshUserName; } } }
	public string ProcessorSshPassword { get { lock (_gate) { return _processorSshPassword; } } }

	public string Password
		{
		get
			{
			lock (_gate)
				{
				return _password;
				}
			}
		}

	public TimeSpan DiscoveryTimeout
		{
		get
			{
			lock (_gate)
				{
				return _discoveryTimeout;
				}
			}
		}

	public bool EnableLightPolling
		{
		get
			{
			lock (_gate)
				{
				return _enableLightPolling;
				}
			}
		}

	public TimeSpan LightPollInterval
		{
		get
			{
			lock (_gate)
				{
				return _lightPollInterval;
				}
			}
		}

	public TimeSpan SensorPollInterval
		{
		get
			{
			lock (_gate)
				{
				return _sensorPollInterval;
				}
			}
		}

	public bool TreatPlugsAsLights
		{
		get
			{
			lock (_gate)
				{
				return _treatPlugsAsLights;
				}
			}
		}

	public void Update (
		string userName,
		string password,
		TimeSpan discoveryTimeout,
		bool enableLightPolling,
		TimeSpan lightPollInterval,
		TimeSpan sensorPollInterval,
		bool treatPlugsAsLights,
		bool enableProcessorBaselineWorkaround,
		string processorSshHost,
		string processorSshUserName,
		string processorSshPassword)
		{
		lock (_gate)
			{
			_userName = userName;
			_password = password;
			_discoveryTimeout = discoveryTimeout;
			_enableLightPolling = enableLightPolling;
			_lightPollInterval = lightPollInterval;
			_sensorPollInterval = sensorPollInterval;
			_treatPlugsAsLights = treatPlugsAsLights;
		_enableProcessorBaselineWorkaround = enableProcessorBaselineWorkaround;
		_processorSshHost = processorSshHost;
		_processorSshUserName = processorSshUserName;
		_processorSshPassword = processorSshPassword;
			}
		}

	public PlatformSharedConfigurationSnapshot Snapshot ()
		{
		lock (_gate)
			{
			return new PlatformSharedConfigurationSnapshot (
				_userName,
				_password,
				_discoveryTimeout,
				_enableLightPolling,
				_lightPollInterval,
				_sensorPollInterval,
				_treatPlugsAsLights,
				_enableProcessorBaselineWorkaround,
				_processorSshHost,
				_processorSshUserName,
				_processorSshPassword);
			}
		}
	}