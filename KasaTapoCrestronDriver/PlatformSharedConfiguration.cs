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

	public string UserName
		{
		get { lock (_gate) { return field; } }
		private set;
		} = string.Empty;

	public bool EnableProcessorBaselineWorkaround
		{
		get { lock (_gate) { return field; } }
		private set;
		}

	public string ProcessorSshHost
		{
		get { lock (_gate) { return field; } }
		private set;
		} = string.Empty;

	public string ProcessorSshUserName
		{
		get { lock (_gate) { return field; } }
		private set;
		} = string.Empty;

	public string ProcessorSshPassword
		{
		get { lock (_gate) { return field; } }
		private set;
		} = string.Empty;

	public string Password
		{
		get { lock (_gate) { return field; } }
		private set;
		} = string.Empty;

	public TimeSpan DiscoveryTimeout
		{
		get { lock (_gate) { return field; } }
		private set;
		} = TimeSpan.FromSeconds (8);

	public bool EnableLightPolling
		{
		get { lock (_gate) { return field; } }
		private set;
		}

	public TimeSpan LightPollInterval
		{
		get { lock (_gate) { return field; } }
		private set;
		} = TimeSpan.FromSeconds (15);

	public TimeSpan SensorPollInterval
		{
		get { lock (_gate) { return field; } }
		private set;
		} = TimeSpan.FromSeconds (3);

	public bool TreatPlugsAsLights
		{
		get { lock (_gate) { return field; } }
		private set;
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
		}

	public PlatformSharedConfigurationSnapshot Snapshot ()
		{
		lock (_gate)
			{
			return new PlatformSharedConfigurationSnapshot (
				UserName,
				Password,
				DiscoveryTimeout,
				EnableLightPolling,
				LightPollInterval,
				SensorPollInterval,
				TreatPlugsAsLights,
				EnableProcessorBaselineWorkaround,
				ProcessorSshHost,
				ProcessorSshUserName,
				ProcessorSshPassword);
			}
		}
	}
