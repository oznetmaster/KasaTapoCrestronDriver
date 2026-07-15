using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.EntityModel.Logging;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

using KasaTapoClient;

using KasaDeviceType = KasaTapoClient.DeviceType;

namespace KasaTapoCrestronDriver;

internal class KasaLightEntity : ReflectedAttributeDriverEntity, IKasaManagedLightEntity
	{
	private const string BRIGHTNESS_FEATURE_ID = "brightness";
	private const string COLOR_TEMPERATURE_FEATURE_ID = "color_temperature";
	private const string HUE_FEATURE_ID = "hue";
	private const string SATURATION_FEATURE_ID = "saturation";
	private const double HUE_MAX_DEGREES = 359d;
	private enum DesiredLightMode
		{
		Off,
		On,
		Brightness,
		ColorTemperature,
		Hsv
		}

	private readonly struct DesiredLightCommand
		{
		public DesiredLightCommand (DesiredLightMode mode, double level, double hue, double saturation, long colorTemperature)
			{
			Mode = mode;
			Level = level;
			Hue = hue;
			Saturation = saturation;
			ColorTemperature = colorTemperature;
			}

		public DesiredLightMode Mode
			{
			get;
			}
		public double Level
			{
			get;
			}
		public double Hue
			{
			get;
			}
		public double Saturation
			{
			get;
			}
		public long ColorTemperature
			{
			get;
			}
		}

	// Kasa/Tapo devices expose color_temp, hue, and saturation as independent device-level fields;
	// setting color_temp never causes the device to recompute hue/saturation (confirmed against the
	// KasaTapoClient transport, which passes these three fields through unmodified). Even so, per the
	// Crestron Lights API docs, lightEmulatedColorTemperature is the correct capability to register for
	// lights that support hue/saturation but also have a color-temperature control that overrides the
	// displayed color (Color-kind bulbs here), while lightColorTemperature remains correct for lights
	// that only support color temperature and not hue/saturation at all (TunableWhite-kind bulbs).
	// Shared abstraction over the two mutually-exclusive color-temperature capability shapes we can
	// register (lightColorTemperature vs lightEmulatedColorTemperature - see ConfigureDynamicFeatures),
	// so the owner class can publish/read/write the active level without needing to know which
	// concrete capability is currently registered.
	private interface IColorTemperatureLevelHolder
		{
		string LevelPropertyId { get; }
		long LightColorTemperatureLevel { get; set; }
		}

	// Per the Crestron Lights API docs, lightColorTemperature is intended for lights where color
	// temperature operates in combination with hue/saturation (e.g. saturation blends between hue and
	// color temperature), or for lights that only support color temperature and not hue/saturation at
	// all. This capability is therefore only registered for TunableWhite-kind lights (CT only, no
	// hue/saturation) - see ConfigureDynamicFeatures.
	private sealed class ColorTemperatureMembers : IColorTemperatureLevelHolder
		{
		private readonly KasaLightEntity _owner;
		private long _lightColorTemperatureLevel;

		public ColorTemperatureMembers (KasaLightEntity owner, DriverEntityValueRange range, long level)
			{
			_owner = owner;
			LightColorTemperatureRange = range;
			_lightColorTemperatureLevel = level;
			}

		string IColorTemperatureLevelHolder.LevelPropertyId => "lightColorTemperature:level";

		[EntityProperty (Id = "lightColorTemperature:range", Units = "Kelvin")]
		public DriverEntityValueRange LightColorTemperatureRange { get; set; } = new DriverEntityValueRange (0, 0, 1);

		[EntityProperty (Id = "lightColorTemperature:level", RangeProperty = "lightColorTemperature:range", Units = "Kelvin")]
		public long LightColorTemperatureLevel
			{
			get => _lightColorTemperatureLevel;
			set => _owner.SetAndNotify ("lightColorTemperature:level", value, ref _lightColorTemperatureLevel);
			}

		[EntityCommand (Id = "lightColorTemperature:setLevel")]
		public void SetColorTemperatureLevel ([EntityParameter (RangeProperty = "lightColorTemperature:range", Units = "Kelvin")] long level)
			{
			_owner.SetColorTemperatureLevel ("lightColorTemperature:setLevel", level);
			}
		}

	// Per the Crestron Lights API docs, lightEmulatedColorTemperature is intended for lights that
	// support hue/saturation but also have a color-temperature control that overrides/supersedes the
	// displayed hue/saturation color. This is a much closer structural match than lightColorTemperature
	// for Color-kind Kasa/Tapo bulbs (KL130, L530, L900, etc.), which independently retain HSV and
	// color-temperature values, but only ever display one or the other at a time. This capability is
	// therefore registered instead of lightColorTemperature whenever full color is also supported - see
	// ConfigureDynamicFeatures.
	private sealed class EmulatedColorTemperatureMembers : IColorTemperatureLevelHolder
		{
		private readonly KasaLightEntity _owner;
		private long _lightColorTemperatureLevel;

		public EmulatedColorTemperatureMembers (KasaLightEntity owner, DriverEntityValueRange range, long level)
			{
			_owner = owner;
			LightEmulatedColorTemperatureRange = range;
			_lightColorTemperatureLevel = level;
			}

		string IColorTemperatureLevelHolder.LevelPropertyId => "lightEmulatedColorTemperature:level";

		[EntityProperty (Id = "lightEmulatedColorTemperature:range", Units = "Kelvin")]
		public DriverEntityValueRange LightEmulatedColorTemperatureRange { get; set; } = new DriverEntityValueRange (0, 0, 1);

		[EntityProperty (Id = "lightEmulatedColorTemperature:level", RangeProperty = "lightEmulatedColorTemperature:range", Units = "Kelvin")]
		public long LightColorTemperatureLevel
			{
			get => _lightColorTemperatureLevel;
			set => _owner.SetAndNotify ("lightEmulatedColorTemperature:level", value, ref _lightColorTemperatureLevel);
			}

		[EntityCommand (Id = "lightEmulatedColorTemperature:setLevel")]
		public void SetColorTemperatureLevel ([EntityParameter (RangeProperty = "lightEmulatedColorTemperature:range", Units = "Kelvin")] long level)
			{
			_owner.SetColorTemperatureLevel ("lightEmulatedColorTemperature:setLevel", level);
			}
		}

	// Color-temperature capability for full-color bulbs that expose the lightTunable capability. It
	// publishes the real lightColorTemperature:range/level (which lightTunable:setLevels references for
	// its colorTemperature parameter) but deliberately declares NO lightColorTemperature:setLevel
	// command: the individual set-command cannot coexist with lightTunable:setLevels. All CT changes
	// for these bulbs arrive through lightTunable:setLevels instead.
	private sealed class TunableColorTemperatureMembers : IColorTemperatureLevelHolder
		{
		private readonly KasaLightEntity _owner;
		private long _lightColorTemperatureLevel;

		public TunableColorTemperatureMembers (KasaLightEntity owner, DriverEntityValueRange range, long level)
			{
			_owner = owner;
			LightColorTemperatureRange = range;
			_lightColorTemperatureLevel = level;
			}

		string IColorTemperatureLevelHolder.LevelPropertyId => "lightColorTemperature:level";

		[EntityProperty (Id = "lightColorTemperature:range", Units = "Kelvin")]
		public DriverEntityValueRange LightColorTemperatureRange { get; set; } = new DriverEntityValueRange (0, 0, 1);

		[EntityProperty (Id = "lightColorTemperature:level", RangeProperty = "lightColorTemperature:range", Units = "Kelvin")]
		public long LightColorTemperatureLevel
			{
			get => _lightColorTemperatureLevel;
			set => _owner.SetAndNotify ("lightColorTemperature:level", value, ref _lightColorTemperatureLevel);
			}
		}

	private sealed class FullColorMembers
		{
		private readonly KasaLightEntity _owner;
		private double _lightColorHue;
		private double _lightColorSaturation;

		public FullColorMembers (KasaLightEntity owner, double hue, double saturation)
			{
			_owner = owner;
			_lightColorHue = hue;
			_lightColorSaturation = saturation;
			}

		[EntityProperty (Id = "lightColor:hueRange")]
		public DriverEntityValueRelativeRange LightColorHueRange { get; } = new (1d / HUE_MAX_DEGREES);

		[EntityProperty (Id = "lightColor:hue", RelativeRangeProperty = "lightColor:hueRange")]
		public double LightColorHue
			{
			get => _lightColorHue;
			set => _owner.SetAndNotify ("lightColor:hue", value, ref _lightColorHue);
			}

		[EntityProperty (Id = "lightColor:saturationRange")]
		public DriverEntityValueRelativeRange LightColorSaturationRange { get; } = new (0.01);

		[EntityProperty (Id = "lightColor:saturation", RelativeRangeProperty = "lightColor:saturationRange")]
		public double LightColorSaturation
			{
			get => _lightColorSaturation;
			set => _owner.SetAndNotify ("lightColor:saturation", value, ref _lightColorSaturation);
			}

		// Color-capable bulbs no longer expose the individual lightColor:setHue/setSaturation commands:
		// the Crestron Home lightTunable capability requires all tuning to flow through the single
		// lightTunable:setLevels command instead (see LightTunableMembers). The hue/saturation state
		// properties and their ranges are still published so the UI can render the current color.
		}

	// Registered for color-capable bulbs so the driver can report the authoritative Crestron Home
	// tuning mode (color vs white/CCT) via lightTunable:mode. Crestron Home derives the color-picker
	// vs color-temperature control from this property; when it is not published the UI infers the mode
	// from the last color-temperature setLevel it injected on power-on and spuriously flips to white.
	// Publishing lightTunable:mode explicitly keeps the UI in the mode the bulb is actually in.
	private sealed class LightTunableMembers
		{
		private readonly KasaLightEntity _owner;
		private LightTunableTuningMode _mode;

		public LightTunableMembers (KasaLightEntity owner, LightTunableTuningMode mode)
			{
			_owner = owner;
			_mode = mode;
			}

		[EntityProperty (Id = "lightTunable:mode")]
		public LightTunableTuningMode LightTunableMode
			{
			get => _mode;
			set => _owner.SetAndNotify ("lightTunable:mode", value, ref _mode);
			}

		// The single tuning command for color-capable bulbs. The Crestron Home lightTunable capability
		// requires all appearance changes (hue, saturation, brightness, colour temperature) to flow
		// through this one command instead of the individual lightColor:*/lightColorTemperature:setLevel
		// commands - the two styles cannot coexist. The UI always sends every parameter declared here.
		// We declare the REAL colorTemperature parameter (not emulatedColorTemperature): emulated CT
		// always overwrites hue/saturation and "wins" whenever sent, which - because the UI always sends
		// every declared parameter - would pin the bulb into white mode permanently. Real colorTemperature
		// sets the white point and Kasa/Tapo bulbs treat colorTemperature==0 as "colour/HSV mode" and
		// colorTemperature>0 as "white/CT mode", so the mode follows the CT value naturally.
		//
		// intensity is declared OptionalParameter = false: per the SDK EntityParameterAttribute docs
		// OptionalParameter is the runtime flag that decides whether the UI is REQUIRED to send a value.
		// intensity is our power/level channel, so we force the UI to always send it - it must never
		// arrive as <none>. hue/saturation/colorTemperature stay optional because their presence or
		// absence is exactly how the UI signals colour-vs-white intent.
		[EntityCommand (Id = "lightTunable:setLevels")]
		public void LightTunableSetLevels (
			[EntityParameter (RelativeRangeProperty = "lightColor:hueRange", OptionalFeature = true, OptionalParameter = true)] double? hue,
			[EntityParameter (RelativeRangeProperty = "lightColor:saturationRange", OptionalFeature = true, OptionalParameter = true)] double? saturation,
			[EntityParameter (RelativeRangeProperty = "lightDimmer:levelRange", OptionalFeature = false, OptionalParameter = false)] double intensity,
			[EntityParameter (RangeProperty = "lightColorTemperature:range", OptionalFeature = true, OptionalParameter = true, Units = "Kelvin")] long? colorTemperature,
			[EntityParameter (DefaultValue = 0, OptionalFeature = true, OptionalParameter = true, Units = "Milliseconds")] long transitionTime)
			{
			_owner.LightTunableSetLevels (hue, saturation, intensity, colorTemperature, transitionTime);
			}
		}

	// Registered only for lights that report brightness support; mutually exclusive with
	// OnOffMembers - see ConfigureDynamicFeatures. Kept as its own dynamic object (rather than static
	// reflected members on the owner) so nothing ever needs to be added/removed after connect.
	private sealed class DimmableMembers
		{
		private readonly KasaLightEntity _owner;
		private double _lightDimmerLevel;

		public DimmableMembers (KasaLightEntity owner, double initialLevel)
			{
			_owner = owner;
			_lightDimmerLevel = initialLevel;
			}

		[EntityProperty (Id = "lightDimmer:levelRange")]
		public DriverEntityValueRelativeRange LightDimmerLevelRange { get; } = new (0.01);

		[EntityProperty (Id = "lightDimmer:level", RelativeRangeProperty = "lightDimmer:levelRange")]
		public double LightDimmerLevel
			{
			get => _lightDimmerLevel;
			set => _owner.SetAndNotify ("lightDimmer:level", value, ref _lightDimmerLevel);
			}

		[EntityCommand (Id = "lightDimmer:setLevel")]
		public void LightDimmerSetLevel ([EntityParameter (RangeMinimum = 0, RangeMaximum = 1, RangeStepSize = 0.01)] double level)
			{
			_owner.LightDimmerSetLevel (level);
			}
		}

	// Registered only for lights that do not report brightness support (simple on/off lights);
	// mutually exclusive with DimmableMembers - see ConfigureDynamicFeatures. Kept as its own dynamic
	// object (rather than static reflected members on the owner) so nothing ever needs to be
	// added/removed after connect.
	private sealed class OnOffMembers
		{
		private readonly KasaLightEntity _owner;
		private bool _lightIsOn;

		public OnOffMembers (KasaLightEntity owner, bool initialIsOn)
			{
			_owner = owner;
			_lightIsOn = initialIsOn;
			}

		[EntityProperty (Id = "light:isOn")]
		public bool LightIsOn
			{
			get => _lightIsOn;
			set => _owner.SetAndNotify ("light:isOn", value, ref _lightIsOn);
			}

		[EntityCommand (Id = "light:off")]
		public void LightOff ()
			{
			_owner.LightOff ();
			}

		[EntityCommand (Id = "light:on")]
		public void LightOn ()
			{
			_owner.LightOn ();
			}
		}

	private void SetActiveColorTemperatureLevel (long value)
		{
		_registeredColorTemperatureMembers!.LightColorTemperatureLevel = value;
		}

	private double LightColorHue
		{
		get => _registeredFullColorMembers?.LightColorHue ?? 0d;
		set
			{
			if (_registeredFullColorMembers is not null)
				{
				_registeredFullColorMembers.LightColorHue = value;
				}
			}
		}

	private double LightColorSaturation
		{
		get => _registeredFullColorMembers?.LightColorSaturation ?? 0d;
		set
			{
			if (_registeredFullColorMembers is not null)
				{
				_registeredFullColorMembers.LightColorSaturation = value;
				}
			}
		}

	private double LightDimmerLevel
		{
		get => _registeredDimmableMembers?.LightDimmerLevel ?? 0d;
		set
			{
			if (_registeredDimmableMembers is not null)
				{
				_registeredDimmableMembers.LightDimmerLevel = value;
				}
			}
		}

	// Dimmable bulbs (e.g. L530/L900) never register OnOffMembers - see ConfigureDynamicFeatures -
	// because per the lightDimmer command documentation, such lights have no dedicated power on/off
	// command at all: power state is derived purely from lightDimmer:level (0 = off, >0 = on).
	// Without this fallback, LightIsOn would always read false for these bulbs, which previously
	// caused IgnoreValueCommandWhileOff to believe the light was always off and force-republish/drop
	// color commands even while genuinely on, producing a spurious color/white mode flip in the UI.
	private bool LightIsOn
		{
		get => _registeredOnOffMembers is not null
			? _registeredOnOffMembers.LightIsOn
			: _supportsBrightness && LightDimmerLevel > 0d;
		set
			{
			if (_registeredOnOffMembers is not null)
				{
				_registeredOnOffMembers.LightIsOn = value;
				}
			}
		}

	// Optimistic slider tracking: while a slider is dragged the light follows it live. Streamed values
	// are dispatched to the device throttled by this minimum interval (tracks visibly without flooding
	// the device); once the slider settles a single authoritative final sync (with refresh) is sent.
	private static readonly TimeSpan SliderTrackingDispatchInterval = TimeSpan.FromMilliseconds (150);
	private static int _dynamicFeatureStateDiagnosticLogged;

	// Process-wide startup connect serialization. The ServicePoint.ConnectionLimit fix only affects the
	// TPAP/HttpWebRequest path and cannot help the legacy KL130 (raw TcpClient) -- yet the L900 (TPAP) and
	// KL130 (legacy) stall together on independent transports that share no discovery or connection code.
	// The likely common denominator is thread-pool/CPU contention during the concurrent connect storm on
	// the embedded host (e.g. blocking BouncyCastle SecureRandom seeding, Task.Run-wrapped synchronous DNS).
	// This gate throttles the startup connect BURST across ALL entities to relieve that contention.
	// It intentionally wraps ONLY the startup connect path, so steady-state per-device reconnects and
	// polling remain fully independent.
	//
	// It must NOT serialize to a single connect: an unreachable/slow device can hang its handshake for
	// the full StartupConnectTimeout (20s). With a degree of 1, that single hang blocks every other
	// entity's very first connect for 20s at a time, and because all entities retry in lockstep the
	// entire platform can fail to initialize for minutes. Live logs showed even a legacy TCP/9999 bulb
	// (a completely different transport from the TPAP bulbs) never initializing because it was queued
	// behind a hung TPAP handshake. A small bounded degree keeps the reload burst throttled while
	// ensuring one stalled device cannot stall the initialization of the others.
	private const int StartupConnectMaxConcurrency = 3;
	private static readonly SemaphoreSlim StartupConnectConcurrencyGate = new (StartupConnectMaxConcurrency, StartupConnectMaxConcurrency);

	private SemaphoreSlim _connectionGate { get; }
	private object _sliderGate { get; } = new ();
	private DriverControllerLogger _logger { get; }
	private string _driverLogId { get; }
	private Action<ManagedLightDescriptor>? _descriptorUpdated { get; }
	private IPlatformSharedConfiguration _sharedConfiguration { get; }
	private CancellationTokenSource _lifetimeCancellationSource { get; } = new ();

	private ManagedLightDescriptor _descriptor { get; set; } = null!;
	private DeviceConfiguration? _configuration { get; set; }
	private KasaDevice? _connectedDevice { get; set; }
	private Task? _pollingTask { get; set; }
	private ManagedLightKind Kind { get; set; }
	private string? ChildId { get; set; }
	private bool _supportsBrightness { get; set; }
	private bool _supportsFullColor { get; set; }
	private bool _supportsColorTemperature { get; set; }
	private bool _isColorTemperatureUiModeActive { get; set; }
	// Diagnostic instrumentation: records when the last power-on command was requested so that an
	// inbound color-temperature setLevel arriving immediately afterward can be identified as a
	// Load-layer-originated drive during power-on (rather than a genuine user white-mode selection).
	private DateTime? _lastPowerOnRequestedUtc;
	private int _dynamicFeaturesConfigured;
	private static readonly TimeSpan StartupConnectRetryInterval = TimeSpan.FromSeconds (10);
	// Live logs show all light entities retrying in lockstep every cycle for minutes after a driver
	// reload, with per-connect durations of 7000+ ms even for devices that succeed (observed: 7049 ms
	// and 7104 ms) right up against the previous 8-second timeout, while other devices using a
	// completely different transport (legacy XOR-encrypted TCP on port 9999 vs. TPAP over HTTP) show
	// the identical stall pattern. Because the slowdown is common to unrelated transport
	// implementations, it is network/OS-level contention on the Crestron processor immediately after
	// reload (e.g. socket/ARP resolution), not a per-protocol handshake cost. Canceling a startup
	// connect discards all progress and forces a full reconnect from scratch next attempt, so a
	// timeout only slightly larger than the typical connect duration causes some devices to loop for
	// minutes instead of connecting once. Using a longer timeout gives slower connects room to finish
	// instead of being aborted just before completion.
	private static readonly TimeSpan StartupConnectTimeout = TimeSpan.FromSeconds (20);
	// Immediately after a processor reboot/program load, the Crestron network stack (link/ARP/routing)
	// may not be fully settled yet, causing the first several TPAP connect attempts to time out even
	// though the device itself is healthy. Rather than always waiting the full 10-second retry interval
	// during this window, use a short ramp-up schedule so the driver reconnects quickly once the network
	// stabilizes, falling back to the normal 10-second interval for any attempts beyond the ramp-up.
	private static readonly TimeSpan[] StartupConnectRetryRampUp =
		{
		TimeSpan.FromSeconds (1),
		TimeSpan.FromSeconds (2),
		TimeSpan.FromSeconds (2),
		TimeSpan.FromSeconds (5),
		};
	// Multiple light entities perform their very first startup connect at essentially the same
	// instant after a driver reload (all triggered from the same child-configuration callback
	// wave). This causes a burst of concurrent TCP connects/ARP resolutions to different hosts on
	// the Crestron processor at once, which live logs show can make an arbitrary device's initial
	// attempt stall for a long time despite the 8-second startup timeout. Staggering the very first
	// attempt with a small random jitter spreads that initial burst out to reduce contention.
	private static readonly Random StartupJitterRandom = new ();
	private static readonly object StartupJitterRandomGate = new ();
	private static readonly TimeSpan StartupJitterMax = TimeSpan.FromSeconds (3);

	private static TimeSpan GetStartupJitterDelay ()
		{
		int jitterMs;
		lock (StartupJitterRandomGate)
			{
			jitterMs = StartupJitterRandom.Next (0, (int)StartupJitterMax.TotalMilliseconds);
			}

		return TimeSpan.FromMilliseconds (jitterMs);
		}
	private DesiredLightCommand? _pendingSliderCommand { get; set; }
	private bool _sliderWorkerRunning { get; set; }
	private CancellationTokenSource? _sliderCommandCancellationSource { get; set; }
	private bool _suppressPropertyNotifications { get; set; } = true;
	private bool _isConfigured { get; set; }
	private bool _childPublished { get; set; }
	private bool _pendingStartupSnapshotAfterConnectedState { get; set; }
	private bool _deferredDeviceStatePending { get; set; }
	private FullColorMembers? _registeredFullColorMembers { get; set; }
	private IColorTemperatureLevelHolder? _registeredColorTemperatureMembers { get; set; }
	private LightTunableMembers? _registeredLightTunableMembers { get; set; }
	private DriverEntityValueRange? _colorTemperatureLevelRange { get; set; }

	// While a color-capable bulb is in color/HSV mode, the device has no active color-temperature
	// value at all (Kasa/Tapo devices report color_temp=0, which is not a valid Kelvin reading - it
	// simply means "color temperature is not the active mode"). We still register the
	// lightEmulatedColorTemperature capability with the device's real declared range (e.g.
	// 2500-6500K) so the UI always sees an honest, device-accurate range. Because a level must be
	// within that range, the color-mode placeholder level is the range minimum rather than 0 (which
	// would be out of range). The mode itself is kept stable by dispatching power-on/off directly
	// instead of through the deferred slider path - see LightDimmerSetLevel - not by the CT value.
	private long _colorTemperatureRangeMinimum { get; set; }
	private DimmableMembers? _registeredDimmableMembers { get; set; }
	private OnOffMembers? _registeredOnOffMembers { get; set; }

	// The emulated<->plain color-temperature capability swap (see ReconcileColorTemperatureCapability)
	// must NOT run while a color-temperature slider interaction is in flight: unregistering the DTO
	// mid-command deletes the very property the UI-issued command is bound to, which makes Crestron's
	// LightTunable capability fail to retrieve 'lightEmulatedColorTemperature:range'/':level' and the
	// command times out. When a swap is requested during an active interaction we record it here and
	// apply it once the interaction has settled - see ProcessSliderCommandAsync.
	private bool _pendingColorTemperatureReconcile { get; set; }
	private bool _pendingColorTemperatureReconcileHasActive { get; set; }
	private int? _pendingColorTemperatureReconcileLevel { get; set; }
	private int _stopState;
	private int _pollingGeneration;
	private bool _disposed { get; set; }

	public KasaLightEntity (
		string controllerId,
		ManagedLightDescriptor descriptor,
		DeviceConfiguration configuration,
		SemaphoreSlim connectionGate,
			Action<ManagedLightDescriptor>? descriptorUpdated,
		IPlatformSharedConfiguration sharedConfiguration,
		DriverImplementationResources resources,
		DriverControllerLogger logger,
		string driverLogId)
		: base (controllerId)
		{
		_connectionGate = connectionGate ?? throw new ArgumentNullException (nameof (connectionGate));
		_descriptorUpdated = descriptorUpdated;
		_sharedConfiguration = sharedConfiguration;
		_logger = logger;
		_driverLogId = driverLogId;

		UpdateDescriptor (descriptor, configuration);
		_suppressPropertyNotifications = false;
		LogInfo ($"Light entity '{ControllerId}' created in passive discovered state; awaiting child configuration callback before activation.");
		}

	public string DeviceName { get; private set; } = string.Empty;

	public string ModelName { get; private set; } = string.Empty;

	public string SerialNumber { get; private set; } = string.Empty;

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

		if (!_connectionGate.Wait (0))
			{
			return false;
			}

		try
			{
			if (_connectedDevice is not null)
				{
				return false;
				}

			_connectedDevice = device;
			UpdateDescriptorFromConnectedDevice (device);
			LogInfo ($"Light entity '{ControllerId}' adopted connected device from context='{context}'.");
			return true;
			}
		finally
			{
			_ = _connectionGate.Release ();
			}
		}

	[EntityProperty (Id = "onlineIndicator:isOnline")]
	public bool OnlineIndicatorIsOnline
		{
		get;
		private set => SetAndNotify ("onlineIndicator:isOnline", value, ref field);
		}

	[EntityProperty (Id = "readyIndicator:isReady")]
	public bool ReadyIndicatorIsReady
		{
		get;
		private set => SetAndNotify ("readyIndicator:isReady", value, ref field);
		}

	// Consolidated tuning handler for the lightTunable:setLevels command (full-color bulbs). Command
	// parameters are optional: the UI sends only the ones relevant to the current interaction and omits
	// the rest (arriving here as null = "leave unchanged"). Which tuning parameter is present therefore
	// signals the requested mode - colorTemperature present => white/CT mode, hue/saturation present =>
	// colour/HSV mode. Intensity carries power: intensity==0 turns the light off, otherwise on. Because
	// power arrives through this command, it must NEVER be routed through IgnoreValueCommandWhileOff
	// (which would drop the power-on and leave the light dead).
	private void LightTunableSetLevels (double? hue, double? saturation, double intensity, long? colorTemperature, long transitionTime)
		{
		LogCommandInvocation (
			"lightTunable:setLevels",
			$"hue={(hue.HasValue ? hue.Value.ToString ("0.####") : "<none>")}, saturation={(saturation.HasValue ? saturation.Value.ToString ("0.####") : "<none>")}, intensity={intensity:0.####}, colorTemperature={(colorTemperature.HasValue ? colorTemperature.Value.ToString () : "<none>")}, transitionTime={transitionTime}");

		// Intensity 0 means power off; handle it directly (never drop it while off).
		if (intensity <= 0d)
			{
			CancelSliderInteraction ();
			LightIsOn = false;
			StartBackgroundOperation (() => ExecuteDeviceCommandAsync ((device, cancellationToken) => ExecutePowerAsync (device, false, cancellationToken), refreshAfterCommand: true, _lifetimeCancellationSource.Token), "lightTunable:setLevels:off");
			return;
			}

		bool wasOff = !LightIsOn;
		double effectiveLevel = Clamp01 (intensity);
		LightIsOn = true;
		LightDimmerLevel = effectiveLevel;

		// A power-on (transition from off to on) must be treated as power only. The UI reasserts its
		// retained tuning state (e.g. colorTemperature) alongside intensity on every on-press, but a
		// bare on/off press is not a tuning interaction: routing it into a full colour-temperature/HSV
		// re-dispatch fires several sequential device calls that can hang and time out (knocking the
		// entity offline). Just power the device on and let it retain its own last state; the deferred
		// reassert after refresh keeps the UI mode aligned.
		if (wasOff)
			{
			StartBackgroundOperation (() => ExecuteDeviceCommandAsync ((device, cancellationToken) => ExecutePowerAsync (device, true, cancellationToken), refreshAfterCommand: true, _lifetimeCancellationSource.Token, reassertColorModeAfterRefresh: true), "lightTunable:setLevels:on");
			return;
			}

		// Already on: this is a genuine tuning interaction. The tuning parameter the UI sent signals the
		// requested mode - colorTemperature present => white/CT mode, hue or saturation present => colour
		// mode. When neither is present (a brightness-only change) retain the current mode.
		bool requestsWhite = colorTemperature.HasValue && colorTemperature.Value > 0L;
		bool requestsColor = hue.HasValue || saturation.HasValue;

		if (requestsWhite)
			{
			long temperatureLevel = Math.Max (1L, colorTemperature!.Value);
			_isColorTemperatureUiModeActive = true;
			SetActiveColorTemperatureLevel (temperatureLevel);
			PublishTuningMode ("lightTunable:setLevels");
			QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.ColorTemperature, effectiveLevel, 0d, 0d, temperatureLevel));
			return;
			}

		if (requestsColor)
			{
			_isColorTemperatureUiModeActive = false;
			double hueLevel = hue.HasValue ? Clamp01 (hue.Value) : LightColorHue;
			double saturationLevel = saturation.HasValue ? Clamp01 (saturation.Value) : LightColorSaturation;
			LightColorHue = hueLevel;
			LightColorSaturation = saturationLevel;
			PublishTuningMode ("lightTunable:setLevels");
			QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Hsv, effectiveLevel, hueLevel, saturationLevel, 0L));
			return;
			}

		// Already on with no tuning parameter: a brightness-only change. Reassert the current mode so a
		// brightness move does not let the UI's persisted mode state flip the tile.
		if (IsCurrentColorTemperatureUiMode ())
			{
			QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.ColorTemperature, effectiveLevel, 0d, 0d, GetActiveColorTemperatureLevel ()));
			}
		else
			{
			QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Hsv, effectiveLevel, LightColorHue, LightColorSaturation, 0L));
			}
		}

	private void LightDimmerSetLevel (double level)
		{
		double relativeLevel = Clamp01 (level);
		LogCommandInvocation ("lightDimmer:setLevel", $"level={relativeLevel:0.####}");
		LightDimmerLevel = relativeLevel;

		// For dimmable bulbs the extreme dimmer levels are the power commands: level 0 = power off,
		// level 1 = power on. These must NEVER be routed through the debounced slider path - even if
		// they arrive during an in-flight slider drag - because that path marks the subsequent
		// device-driven state re-apply as deferred. During power-on Crestron's own Load layer drives
		// the load's ColorTemp channel (SetMultipleChannels ... ColorTemp), and deferring our re-apply
		// lets that Load-layer drive be the last word, flipping a color-mode bulb to white/CCT in the
		// UI. Cancel any pending slider interaction and dispatch the extremes directly as power
		// commands (identical to light:on / light:off), so the settled device state is applied
		// immediately and authoritatively.
		if (relativeLevel <= 0d)
			{
			CancelSliderInteraction ();
			LightIsOn = false;
			StartBackgroundOperation (() => ExecuteDeviceCommandAsync ((device, cancellationToken) => ExecutePowerAsync (device, false, cancellationToken), refreshAfterCommand: true, _lifetimeCancellationSource.Token), "lightDimmer:setLevel:off");
			return;
			}

		if (relativeLevel >= 1d)
			{
			CancelSliderInteraction ();
			LightIsOn = true;
			StartBackgroundOperation (() => ExecuteDeviceCommandAsync ((device, cancellationToken) => ExecutePowerAsync (device, true, cancellationToken), refreshAfterCommand: true, _lifetimeCancellationSource.Token, reassertColorModeAfterRefresh: true), "lightDimmer:setLevel:on");
			return;
			}

		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Brightness, relativeLevel, 0d, 0d, 0L));
		}

	private void LightOff ()
		{
		LogCommandInvocation ("light:off", "requested power off");

		CancelSliderInteraction ();
		LightIsOn = false;
		StartBackgroundOperation (() => ExecuteDeviceCommandAsync ((device, cancellationToken) => ExecutePowerAsync (device, false, cancellationToken), refreshAfterCommand: true, _lifetimeCancellationSource.Token), "light:off");
		}

	private void LightOn ()
		{
		LogCommandInvocation ("light:on", "requested power on");

		_lastPowerOnRequestedUtc = DateTime.UtcNow;
		LightIsOn = true;
		StartBackgroundOperation (() => ExecuteDeviceCommandAsync ((device, cancellationToken) => ExecutePowerAsync (device, true, cancellationToken), refreshAfterCommand: true, _lifetimeCancellationSource.Token), "light:on");
		}

	private void SetColorTemperatureLevel (string commandId, long level)
		{
		string powerOnCorrelation = _lastPowerOnRequestedUtc is DateTime powerOnUtc
			? $"msSincePowerOn={(DateTime.UtcNow - powerOnUtc).TotalMilliseconds:0}"
			: "msSincePowerOn=<none>";
		LogCommandInvocation (commandId, $"level={level}, uiModeWasActive={_isColorTemperatureUiModeActive}, {powerOnCorrelation}");
		if (IgnoreValueCommandWhileOff (commandId))
			{
			return;
			}

		long temperatureLevel = Math.Max (1L, level);
		_isColorTemperatureUiModeActive = true;
		SetActiveColorTemperatureLevel (temperatureLevel);

		// Publish the authoritative white/CCT tuning mode so the UI tile follows the mode switch the
		// user just made through the color-temperature slider.
		PublishTuningMode ("lightColorTemperature:setLevel");

		// Queue the slider command first so the interaction is marked active before we request the
		// capability swap. That guarantees ReconcileColorTemperatureCapability defers the emulated->plain
		// DTO swap (rather than unregistering the property this very command is bound to) until the
		// interaction settles; ProcessSliderCommandAsync applies the deferred swap afterward.
		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.ColorTemperature, GetEffectiveOnLevel (), 0d, 0d, temperatureLevel));
		ReconcileColorTemperatureCapability (hasActiveColorTemperature: true, colorTemperature: (int)temperatureLevel);
		}

	private bool IgnoreValueCommandWhileOff (string commandId)
		{
		if (LightIsOn)
			{
			return false;
			}

		LogInfo ($"Light entity '{ControllerId}' ignoring value command '{commandId}' while off; waiting for dimmer power-on command.");

		// The UI (e.g. a slider) may optimistically switch its displayed mode before the command
		// result is known. Since this command is being dropped rather than applied, republish the
		// current authoritative mode/property state so the UI reverts to reality instead of being
		// left showing a mode the device never actually entered.
		PublishCurrentLightModeProperties ($"IgnoreValueCommandWhileOff:{commandId}");
		return true;
		}

	private void QueueSliderCommand (DesiredLightCommand command)
		{
		CancellationTokenSource? cancellationSource = null;
		lock (_sliderGate)
			{
			// Always record the latest slider value; a running tracking worker will pick it up on its
			// next iteration. Only spin up a new worker (and its cancellation source) when none is
			// currently running, so a drag is served by a single long-lived worker that tracks the
			// light live instead of a fresh cancel-per-value debounce that only fires once on release.
			_pendingSliderCommand = command;
			if (!_sliderWorkerRunning)
				{
				_sliderWorkerRunning = true;
				_sliderCommandCancellationSource?.Dispose ();
				_sliderCommandCancellationSource = new CancellationTokenSource ();
				cancellationSource = _sliderCommandCancellationSource;
				}
			}

		if (cancellationSource is not null)
			{
			_ = ProcessSliderCommandAsync (cancellationSource.Token);
			}
		}

	private void StartBackgroundOperation (Func<Task> operation, string operationName)
		{
		Task task;
		try
			{
			task = operation ();
			}
		catch (Exception ex)
			{
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' background operation '{operationName}' failed before dispatch: {ex}");
			return;
			}

		task.ContinueWith (
			continuationTask =>
				{
					if (continuationTask.IsFaulted)
						{
						Exception exception = continuationTask.Exception?.GetBaseException () ?? continuationTask.Exception!;
						_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' background operation '{operationName}' failed: {exception}");
						}
					else if (continuationTask.IsCanceled)
						{
						LogInfo ($"Light entity '{ControllerId}' background operation '{operationName}' was canceled.");
						}
				},
			CancellationToken.None,
			TaskContinuationOptions.None,
			TaskScheduler.Default);
		}

	private async Task ProcessSliderCommandAsync (CancellationToken cancellationToken)
		{
		bool reapplyResolvedState = false;
		try
			{
			// Optimistic tracking loop: keep dispatching the latest slider value to the physical light
			// as the drag progresses (throttled, no device refresh so we don't fight the incoming
			// stream), and only exit when the slider has settled - i.e. no new value arrived during a
			// full throttle interval. The final settled value is then dispatched authoritatively with a
			// device refresh outside the loop.
			DesiredLightCommand? finalCommand = null;
			CancellationToken lifetimeToken = _lifetimeCancellationSource.Token;

			while (true)
				{
				await Task.Delay (SliderTrackingDispatchInterval, cancellationToken).ConfigureAwait (false);

				DesiredLightCommand? command;
				bool settled;
				lock (_sliderGate)
					{
					if (cancellationToken.IsCancellationRequested || _sliderCommandCancellationSource is null || _sliderCommandCancellationSource.Token != cancellationToken)
						{
						return;
						}

					command = _pendingSliderCommand;
					_pendingSliderCommand = null;

					// No new value arrived during this interval: the slider has settled. Clear the
					// worker flag atomically so a subsequent slider value starts a fresh worker, and
					// exit the tracking loop to perform the final authoritative sync.
					settled = !command.HasValue;
					if (settled)
						{
						_sliderWorkerRunning = false;
						reapplyResolvedState = true;
						}
					}

				if (settled)
					{
					break;
					}

				finalCommand = command;
				lifetimeToken.ThrowIfCancellationRequested ();

				// Intermediate live-tracking dispatch: drive the device without a follow-up refresh so
				// the light visibly tracks the slider without the refresh fighting the ongoing drag.
				await DispatchSliderCommandAsync (command!.Value, refreshAfterCommand: false, lifetimeToken).ConfigureAwait (false);
				}

			// Final authoritative sync of the settled value (with device refresh). finalCommand holds
			// the last value we actually dispatched during the drag.
			if (finalCommand.HasValue)
				{
				lifetimeToken.ThrowIfCancellationRequested ();
				LogInfo ($"Light entity '{ControllerId}' final slider sync: {FormatDesiredLightCommand (finalCommand.Value)}.");
				await DispatchSliderCommandAsync (finalCommand.Value, refreshAfterCommand: true, lifetimeToken).ConfigureAwait (false);
				}
			}
		catch (OperationCanceledException)
			{
			}
		catch (Exception ex)
			{
			ResetConnectionState ();
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' slider command failed: {ex}");
			}
		finally
			{
			lock (_sliderGate)
				{
				if (_sliderCommandCancellationSource is not null && _sliderCommandCancellationSource.Token == cancellationToken)
					{
					_sliderWorkerRunning = false;
					_sliderCommandCancellationSource.Dispose ();
					_sliderCommandCancellationSource = null;
					}
				}

			if (reapplyResolvedState)
				{
				try
					{
					TryApplyDeferredColorTemperatureReconcile ("ProcessSliderCommandAsync");
					TryApplyDeferredDeviceStateAfterSliderInteraction ("ProcessSliderCommandAsync");
					}
				catch (Exception ex)
					{
					_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' post-slider state apply failed: {ex}");
					}
				}
			}
		}

	private async Task DispatchSliderCommandAsync (DesiredLightCommand command, bool refreshAfterCommand, CancellationToken lifetimeToken)
		{
		LogInfo ($"Light entity '{ControllerId}' dispatch slider command: {FormatDesiredLightCommand (command)} (refresh={refreshAfterCommand}).");

		await ExecuteDeviceCommandAsync (
			async (device, innerCancellationToken) =>
				{
					innerCancellationToken.ThrowIfCancellationRequested ();
					await ApplyDesiredLightCommandAsync (device, command, innerCancellationToken).ConfigureAwait (false);
				},
			refreshAfterCommand: refreshAfterCommand,
			lifetimeToken).ConfigureAwait (false);
		}

	private void CancelSliderInteraction ()
		{
		lock (_sliderGate)
			{
			_pendingSliderCommand = null;
			_sliderWorkerRunning = false;
			_sliderCommandCancellationSource?.Cancel ();
			_sliderCommandCancellationSource?.Dispose ();
			_sliderCommandCancellationSource = null;
			}
		}

	private bool IsSliderInteractionActive ()
		{
		lock (_sliderGate)
			{
			return _sliderCommandCancellationSource is not null || _pendingSliderCommand.HasValue;
			}
		}

	private async Task ApplyDesiredLightCommandAsync (KasaDevice device, DesiredLightCommand command, CancellationToken cancellationToken)
		{
		switch (command.Mode)
			{
			case DesiredLightMode.Off:
				await device.TurnLightOffAsync (cancellationToken).ConfigureAwait (false);
				return;

			case DesiredLightMode.On:
				await device.TurnLightOnAsync (cancellationToken).ConfigureAwait (false);
				return;

			case DesiredLightMode.Hsv:
				await device.SetHsvAsync (
					(int)Math.Round (Clamp01 (command.Hue) * HUE_MAX_DEGREES, MidpointRounding.AwayFromZero),
					(int)Math.Round (Clamp01 (command.Saturation) * 100d, MidpointRounding.AwayFromZero),
					ToBrightnessPercent (command.Level),
					cancellationToken).ConfigureAwait (false);
				return;

			case DesiredLightMode.ColorTemperature:
				await EnsurePoweredForLevelAsync (device, command.Level, cancellationToken).ConfigureAwait (false);
				await device.SetBrightnessAsync (ToBrightnessPercent (command.Level), cancellationToken).ConfigureAwait (false);
				await device.SetColorTemperatureAsync ((int)Math.Max (1L, command.ColorTemperature), cancellationToken).ConfigureAwait (false);
				return;

			case DesiredLightMode.Brightness:
				await ApplyBrightnessCommandAsync (device, command.Level, cancellationToken).ConfigureAwait (false);
				return;
			}
		}

	public virtual void UpdateDescriptor (ManagedLightDescriptor descriptor, DeviceConfiguration configuration)
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
		Kind = _descriptor.Kind;
		ChildId = _descriptor.ChildId;

		RestartPolling ();
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
		DeviceConfiguration? previousConfiguration = _configuration;
		_configuration = configuration;

		if (previousConfiguration is not null && HasMaterialConfigurationChange (previousConfiguration, configuration))
			{
			ResetConnectionState ();
			}

		if (_isConfigured)
			{
			RestartPolling ();
			}
		}

	public void SetConfigured (bool configured, string context)
		{
		if (_disposed)
			{
			return;
			}

		if (_isConfigured == configured)
			{
			LogInfo ($"Light entity '{ControllerId}' SetConfigured ignored because configured={configured} is unchanged; context='{context}'.");
			return;
			}

		_isConfigured = configured;
		LogInfo ($"Light entity '{ControllerId}' SetConfigured: configured={configured}, context='{context}'.");

		if (!configured)
			{
			ResetConnectionState ();
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
			LogInfo ($"Light entity '{ControllerId}' SetConfiguredAsync ignored because configured={configured} is unchanged; context='{context}'.");
			return;
			}

		_isConfigured = configured;
		LogInfo ($"Light entity '{ControllerId}' SetConfiguredAsync: configured={configured}, context='{context}'.");

		if (!configured)
			{
			ResetConnectionState ();
			Interlocked.Increment (ref _pollingGeneration);
			_pollingTask = null;
			return;
			}

		RestartPolling ();
		await InitializeConnectedStateAsync (cancellationToken).ConfigureAwait (false);
		}

	public void NotifyChildPublished ()
		{
		if (_disposed)
			{
			return;
			}

		_childPublished = true;
		TryPublishDeferredStartupSnapshot ("NotifyChildPublished");
		}

	public void NotifyChildRunning (string context)
		{
		if (_disposed)
			{
			return;
			}

		LogInfo ($"Light entity '{ControllerId}' child reached Running; republishing current state snapshot; context='{context}'.");
		PublishStateSnapshot ();
		}

	public void ApplyRuntimeConfiguration (PlatformSharedConfigurationSnapshot previousConfiguration, PlatformSharedConfigurationSnapshot currentConfiguration)
		{
		if (_disposed)
			{
			return;
			}

		bool pollingChanged = previousConfiguration.EnableLightPolling != currentConfiguration.EnableLightPolling
			|| previousConfiguration.LightPollInterval != currentConfiguration.LightPollInterval;
		bool connectionChanged = !string.Equals (previousConfiguration.UserName, currentConfiguration.UserName, StringComparison.Ordinal)
			|| !string.Equals (previousConfiguration.Password, currentConfiguration.Password, StringComparison.Ordinal)
			|| previousConfiguration.DiscoveryTimeout != currentConfiguration.DiscoveryTimeout;

		if (pollingChanged)
			{
			RestartPolling ();
			}

		if (connectionChanged)
			{
			ResetConnectionState ();
			}
		}

	public void Stop ()
		{
		if (Interlocked.Exchange (ref _stopState, 1) != 0)
			{
			return;
			}

		CancelSliderInteraction ();
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

	public async Task RefreshAsync (CancellationToken cancellationToken)
		{
		try
			{
			await RefreshAndApplyStateAsync (cancellationToken).ConfigureAwait (false);
			}
		catch (TimeoutException timeoutException)
			{
			try
				{
				await ReconnectAndRefreshAsync (timeoutException, cancellationToken).ConfigureAwait (false);
				OnlineIndicatorIsOnline = true;
				ReadyIndicatorIsReady = true;
				return;
				}
			catch (OperationCanceledException)
				{
				throw;
				}
			catch (Exception reconnectException)
				{
				ResetConnectionState ();
				_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' reconnect after refresh timeout failed: {reconnectException}");
				return;
				}
			}
		catch (OperationCanceledException)
			{
			throw;
			}
		catch (Exception ex)
			{
			ResetConnectionState ();
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' refresh failed: {ex}");
			}
		}

	private void RestartPolling ()
		{
		if (_disposed)
			{
			LogInfo ($"Light entity '{ControllerId}' RestartPolling skipped because the entity is disposed.");
			return;
			}

		if (!_isConfigured)
			{
			LogInfo ($"Light entity '{ControllerId}' RestartPolling skipped because the child is not configured/installed yet.");
			return;
			}

		Interlocked.Exchange (ref _stopState, 0);
		int generation = Interlocked.Increment (ref _pollingGeneration);

		_pollingTask = _sharedConfiguration.EnableLightPolling
			? RunPollingCycleAsync (generation)
			: null;
		LogInfo ($"Light entity '{ControllerId}' RestartPolling: enabled={_sharedConfiguration.EnableLightPolling}, generation={generation}, intervalMs={_sharedConfiguration.LightPollInterval.TotalMilliseconds:0}.");
		}

	private void ResetConnectionState ()
		{
		KasaDevice? staleDevice = _connectedDevice;
		LogInfo ($"Light entity '{ControllerId}' ResetConnectionState: hadConnectedDevice={staleDevice is not null}.");
		_connectedDevice = null;
		_pendingStartupSnapshotAfterConnectedState = false;
		OnlineIndicatorIsOnline = false;
		ReadyIndicatorIsReady = false;

		if (staleDevice is not null)
			{
			try
				{
				staleDevice.Dispose ();
				}
			catch (Exception ex)
				{
				_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' failed to dispose stale connected device: {ex}");
				}
			}
		}

	private void TryPublishDeferredStartupSnapshot (string context)
		{
		if (!_pendingStartupSnapshotAfterConnectedState || !_childPublished)
			{
			return;
			}

		_pendingStartupSnapshotAfterConnectedState = false;
		LogInfo ($"Light entity '{ControllerId}' publishing deferred startup snapshot after connected state and child publication were both satisfied; context='{context}'.");
		PublishStateSnapshot ();
		}

	private void TryApplyDeferredDeviceStateAfterSliderInteraction (string context)
		{
		if (!_deferredDeviceStatePending || IsSliderInteractionActive ())
			{
			return;
			}

		KasaDevice? connectedDevice = _connectedDevice;
		if (connectedDevice is null)
			{
			return;
			}

		ApplyState (connectedDevice, allowDeferredDeviceStateDuringSliderInteraction: true);
		LogEntityStateSnapshot ($"{context}.AfterDeferredApplyState");
		LogInfo ($"Light entity '{ControllerId}' applied deferred device-driven state after slider interaction settled; context='{context}'.");
		}

	private static bool HasMaterialConfigurationChange (DeviceConfiguration previousConfiguration, DeviceConfiguration currentConfiguration)
		{
		if (!string.Equals (previousConfiguration.Host, currentConfiguration.Host, StringComparison.OrdinalIgnoreCase)
			|| previousConfiguration.Port != currentConfiguration.Port
			|| previousConfiguration.Timeout != currentConfiguration.Timeout)
			{
			return true;
			}

		if (!HaveEquivalentCredentials (previousConfiguration.Credentials, currentConfiguration.Credentials))
			{
			return true;
			}

		return !HaveEquivalentConnectionOptions (previousConfiguration.ConnectionOptions, currentConfiguration.ConnectionOptions);
		}

	private static bool HaveEquivalentCredentials (DeviceCredentials? previousCredentials, DeviceCredentials? currentCredentials)
		{
		if (ReferenceEquals (previousCredentials, currentCredentials))
			{
			return true;
			}

		if (previousCredentials is null || currentCredentials is null)
			{
			return false;
			}

		return string.Equals (previousCredentials.UserName, currentCredentials.UserName, StringComparison.Ordinal)
			&& string.Equals (previousCredentials.Password, currentCredentials.Password, StringComparison.Ordinal);
		}

	private static bool HaveEquivalentConnectionOptions (DeviceConnectionOptions previousOptions, DeviceConnectionOptions currentOptions)
		{
		return previousOptions.TransportKind == currentOptions.TransportKind
			&& string.Equals (previousOptions.ConnectionParameters?.ToString (), currentOptions.ConnectionParameters?.ToString (), StringComparison.Ordinal)
			&& previousOptions.UseSsl == currentOptions.UseSsl
			&& previousOptions.UseDefaultCredentials == currentOptions.UseDefaultCredentials
			&& previousOptions.DefaultCredentialProfile == currentOptions.DefaultCredentialProfile
			&& string.Equals (previousOptions.ApplicationPath, currentOptions.ApplicationPath, StringComparison.Ordinal)
			&& previousOptions.UseSecurePassthrough == currentOptions.UseSecurePassthrough
			&& previousOptions.TpapKeepAliveInterval == currentOptions.TpapKeepAliveInterval;
		}

	private async Task RunPollingCycleAsync (int generation)
		{
		try
			{
			await Task.Delay (_sharedConfiguration.LightPollInterval, _lifetimeCancellationSource.Token).ConfigureAwait (false);

			if (_disposed
				|| Volatile.Read (ref _stopState) != 0
				|| generation != Volatile.Read (ref _pollingGeneration)
				|| !_sharedConfiguration.EnableLightPolling)
				{
				return;
				}

			await RefreshAsync (_lifetimeCancellationSource.Token).ConfigureAwait (false);
			PublishStateSnapshot ();

			if (_disposed
				|| Volatile.Read (ref _stopState) != 0
				|| generation != Volatile.Read (ref _pollingGeneration)
				|| !_sharedConfiguration.EnableLightPolling)
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
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' polling loop failed: {ex}");
			}
		}

	public void PublishStateSnapshot ()
		{
		LogPublishedState ();

		PublishProperty ("onlineIndicator:isOnline", new DriverEntityValue (OnlineIndicatorIsOnline), "PublishStateSnapshot");
		PublishProperty ("readyIndicator:isReady", new DriverEntityValue (ReadyIndicatorIsReady), "PublishStateSnapshot");
		if (_registeredOnOffMembers is not null)
			{
			PublishProperty ("light:isOn", new DriverEntityValue (LightIsOn), "PublishStateSnapshot");
			}

		if (_supportsBrightness)
			{
			PublishProperty ("lightDimmer:level", new DriverEntityValue (LightDimmerLevel), "PublishStateSnapshot");
			}

		PublishColorModeStateProperties ("PublishStateSnapshot");

		}

	private async Task ExecuteWithConnectedDeviceAsync (Func<KasaDevice, Task> work, CancellationToken cancellationToken)
		{
		KasaDevice device = await EnsureConnectedAsync (cancellationToken).ConfigureAwait (false);
		await work (device).ConfigureAwait (false);
		}

	private async Task InitializeConnectedStateAsync (CancellationToken cancellationToken)
		{
		KasaDevice device = await EnsureConnectedAsync (cancellationToken).ConfigureAwait (false);
		ConfigureDynamicFeatures (device, _descriptor);
		LogReportedState ("InitializeConnectedState", device);

		bool previousSuppressPropertyNotifications = _suppressPropertyNotifications;
		_suppressPropertyNotifications = true;
		try
			{
			ApplyState (device);
			}
		finally
			{
			_suppressPropertyNotifications = previousSuppressPropertyNotifications;
			}

		OnlineIndicatorIsOnline = true;
		ReadyIndicatorIsReady = true;
		PublishCurrentLightModeProperties ("InitializeConnectedStateAsync.AfterOnlineReady");
		_pendingStartupSnapshotAfterConnectedState = true;
		TryPublishDeferredStartupSnapshot ("InitializeConnectedStateAsync");
		}

	private async Task InitializeStartupAsync ()
		{
		TimeSpan startupJitterDelay = GetStartupJitterDelay ();
		if (startupJitterDelay > TimeSpan.Zero)
			{
			LogInfo ($"Light entity '{ControllerId}' delaying initial startup connect to host '{_descriptor.Host}' by {startupJitterDelay.TotalMilliseconds:0} ms to stagger concurrent reload connects.");
			try
				{
				await Task.Delay (startupJitterDelay, _lifetimeCancellationSource.Token).ConfigureAwait (false);
				}
			catch (OperationCanceledException) when (!_disposed && Volatile.Read (ref _stopState) == 0)
				{
				return;
				}
			}

		int startupConnectAttempt = 0;
		while (!_disposed && Volatile.Read (ref _stopState) == 0)
			{
			TimeSpan retryInterval = startupConnectAttempt < StartupConnectRetryRampUp.Length
				? StartupConnectRetryRampUp[startupConnectAttempt]
				: StartupConnectRetryInterval;
			startupConnectAttempt++;

			try
				{
				LogInfo ($"Light entity '{ControllerId}' startup connecting to discovered host '{_descriptor.Host}' as {_descriptor.DiscoveredDeviceType}; driverId='{_driverLogId}'.");
				await StartupConnectConcurrencyGate.WaitAsync (_lifetimeCancellationSource.Token).ConfigureAwait (false);
				try
					{
					using (CancellationTokenSource timeoutCancellationSource = CancellationTokenSource.CreateLinkedTokenSource (_lifetimeCancellationSource.Token))
						{
						timeoutCancellationSource.CancelAfter (StartupConnectTimeout);
						await InitializeConnectedStateAsync (timeoutCancellationSource.Token).ConfigureAwait (false);
						}
					}
				finally
					{
					_ = StartupConnectConcurrencyGate.Release ();
					}
				return;
				}
			catch (OperationCanceledException) when (!_disposed && Volatile.Read (ref _stopState) == 0)
				{
				LogInfo ($"Light entity '{ControllerId}' startup connect canceled for host '{_descriptor.Host}'; retrying in {retryInterval.TotalSeconds:0} seconds.");
				OnlineIndicatorIsOnline = false;
				ReadyIndicatorIsReady = false;
				}
			catch (OperationCanceledException)
				{
				throw;
				}
			catch (Exception ex)
				{
				LogInfo ($"Light entity '{ControllerId}' startup connect failed for host '{_descriptor.Host}': {ex.Message}; retrying in {retryInterval.TotalSeconds:0} seconds.");
				OnlineIndicatorIsOnline = false;
				ReadyIndicatorIsReady = false;
				}

			await Task.Delay (retryInterval, _lifetimeCancellationSource.Token).ConfigureAwait (false);
			}
		}

	private async Task<KasaDevice> EnsureConnectedAsync (CancellationToken cancellationToken)
		{
		if (!_isConfigured)
			{
			throw new InvalidOperationException ($"Light entity '{ControllerId}' cannot connect before child configuration/installation has occurred.");
			}

		KasaDevice? existingDevice = _connectedDevice;
		if (existingDevice is not null)
			{
			return existingDevice;
			}

		cancellationToken.ThrowIfCancellationRequested ();
		_lifetimeCancellationSource.Token.ThrowIfCancellationRequested ();

		using var linkedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken, _lifetimeCancellationSource.Token);
		LogInfo ($"Light entity '{ControllerId}' EnsureConnectedAsync waiting on connection gate.");
		Stopwatch gateWaitStopwatch = Stopwatch.StartNew ();
		await _connectionGate.WaitAsync (linkedCancellationSource.Token).ConfigureAwait (false);
		gateWaitStopwatch.Stop ();
		LogInfo ($"Light entity '{ControllerId}' EnsureConnectedAsync acquired connection gate after {gateWaitStopwatch.Elapsed.TotalMilliseconds:0} ms.");
		try
			{
			existingDevice = _connectedDevice;
			if (existingDevice is not null)
				{
				return existingDevice;
				}

			Stopwatch connectStopwatch = Stopwatch.StartNew ();
			KasaDevice connectedDevice = await ConnectFromConfigurationAsync (updateState: true, linkedCancellationSource.Token).ConfigureAwait (false);
			connectStopwatch.Stop ();

			LogInfo ($"Light entity '{ControllerId}' connect succeeded after {connectStopwatch.Elapsed.TotalMilliseconds:0} ms: deviceAlias='{connectedDevice.Alias ?? "<null>"}', systemInfoAlias='{connectedDevice.SystemInfo?.Alias ?? "<null>"}', model='{connectedDevice.SystemInfo?.Model ?? "<null>"}', deviceId='{connectedDevice.SystemInfo?.DeviceId ?? "<null>"}'.");

			_connectedDevice = connectedDevice;
			UpdateDescriptorFromConnectedDevice (connectedDevice);
			return connectedDevice;
			}
		finally
			{
			_ = _connectionGate.Release ();
			}
		}

	private async Task<KasaDevice> ConnectFromConfigurationAsync (bool updateState, CancellationToken cancellationToken)
		{
		DeviceConfiguration? configuration = _configuration;
		if (configuration is null)
			{
			throw new InvalidOperationException ($"Light entity '{ControllerId}' cannot connect because no device configuration was supplied by the platform.");
			}

		cancellationToken.ThrowIfCancellationRequested ();
		_lifetimeCancellationSource.Token.ThrowIfCancellationRequested ();

		// TpapTransport/LegacyTransport apply configuration.Timeout as their own internal per-request
		// timeout (see TpapTransport.CreateOperationTimeoutSource), independent of the outer
		// StartupConnectTimeout cancellation below. configuration.Timeout is bound to the user-facing
		// DiscoveryTimeoutSeconds setting (commonly 10s), which is far shorter than StartupConnectTimeout
		// and therefore always fires first, silently overriding the intended startup connect budget. Live
		// logs confirmed connects being canceled at ~10s (the configured DiscoveryTimeoutSeconds) rather
		// than at StartupConnectTimeout. Use a configuration with a timeout at least as long as
		// StartupConnectTimeout for startup connects so the outer timeout is the one that actually governs.
		DeviceConfiguration startupConfiguration = configuration.Timeout < StartupConnectTimeout
			? new DeviceConfiguration (configuration.Host, configuration.Port, configuration.Credentials, configuration.ConnectionOptions, StartupConnectTimeout)
			: configuration;

		return await Discover.ConnectAsync (
			startupConfiguration,
			updateState,
			cancellationToken: cancellationToken).ConfigureAwait (false);
		}

	private void UpdateDescriptorFromConnectedDevice (KasaDevice device)
		{
		string resolvedAlias = !string.IsNullOrWhiteSpace (device.Alias)
			? device.Alias!
			: !string.IsNullOrWhiteSpace (device.SystemInfo?.Alias)
				? device.SystemInfo!.Alias!
				: _descriptor.Name;

		LogInfo ($"Light entity '{ControllerId}' UpdateDescriptorFromConnectedDevice: resolvedAlias='{resolvedAlias}', deviceAlias='{device.Alias ?? "<null>"}', systemInfoAlias='{device.SystemInfo?.Alias ?? "<null>"}', previousName='{_descriptor.Name ?? "<null>"}'.");

		_descriptor.Name = resolvedAlias;
		_descriptor.Kind = InferManagedLightKind (device, _descriptor.DiscoveredDeviceType);

		DeviceName = _descriptor.Name;
		ModelName = _descriptor.ModelName;
		SerialNumber = _descriptor.SerialNumber;
		Kind = _descriptor.Kind;
		ChildId = _descriptor.ChildId;
		LogInfo ($"Light entity '{ControllerId}' invoking descriptor update callback with name='{_descriptor.Name}', model='{_descriptor.ModelName}', serial='{_descriptor.SerialNumber}'.");
		_descriptorUpdated?.Invoke (_descriptor);
		LogInfo ($"Light entity '{ControllerId}' descriptor update callback completed.");
		}

	private void RefreshDescriptorFromConnectedDevice (KasaDevice device, string context)
		{
		string previousName = _descriptor.Name;
		string previousModel = _descriptor.ModelName;
		string previousSerial = _descriptor.SerialNumber;

		UpdateDescriptorFromConnectedDevice (device);

		if (string.Equals (previousName, _descriptor.Name, StringComparison.Ordinal)
			&& string.Equals (previousModel, _descriptor.ModelName, StringComparison.Ordinal)
			&& string.Equals (previousSerial, _descriptor.SerialNumber, StringComparison.Ordinal))
			{
			LogInfo ($"Light entity '{ControllerId}' {context}: descriptor unchanged after refresh.");
			}
		else
			{
			LogInfo ($"Light entity '{ControllerId}' {context}: descriptor changed to name='{_descriptor.Name}', model='{_descriptor.ModelName}', serial='{_descriptor.SerialNumber}'.");
			}
		}

	private void ConfigureDynamicFeatures (KasaDevice device, ManagedLightDescriptor descriptor)
		{
		if (Interlocked.Exchange (ref _dynamicFeaturesConfigured, 1) != 0)
			{
			return;
			}

		bool supportsLightControls = descriptor.Kind != ManagedLightKind.OnOff;
		DriverEntityValueRange? lightColorTemperatureRange = null;

		_supportsBrightness = supportsLightControls && HasFeature (device.Features, BRIGHTNESS_FEATURE_ID);
		_supportsFullColor = supportsLightControls && HasCurrentFullColorState (device.LightState);
		bool supportsColorTemperatureApi = false;
		if (supportsLightControls)
			{
			supportsColorTemperatureApi = TryCreateColorTemperatureRange (device.Features, out DriverEntityValueRange detectedColorTemperatureRange);
			if (supportsColorTemperatureApi)
				{
				lightColorTemperatureRange = detectedColorTemperatureRange;
				}
			}
		_supportsColorTemperature = supportsColorTemperatureApi;
		LogInfo ($"Light entity '{ControllerId}' dynamic features: descriptorKind={descriptor.Kind}, supportsLightControls={supportsLightControls}, featureCount={device.Features.Count}, supportsBrightness={_supportsBrightness}, supportsFullColor={_supportsFullColor}, supportsColorTemperatureApi={supportsColorTemperatureApi}, supportsColorTemperature={_supportsColorTemperature}, colorTemperatureRange={FormatRange (lightColorTemperatureRange)}.");

		if (_supportsBrightness)
			{
			int? brightness = device.LightState?.Brightness;
			bool isOnAtConnect = ResolvePowerState (device);
			double initialDimmerLevel = isOnAtConnect && brightness.HasValue
				? Clamp01 (brightness.Value / 100d)
				: 0d;
			_registeredDimmableMembers = new DimmableMembers (this, initialDimmerLevel);
			RegisterObjectWithAttributes (_registeredDimmableMembers);
			}
		else
			{
			_registeredOnOffMembers = new OnOffMembers (this, ResolvePowerState (device));
			RegisterObjectWithAttributes (_registeredOnOffMembers);
			}

		if (_supportsColorTemperature)
			{
			bool hasActiveColorTemperature = HasCurrentColorTemperatureState (device.LightState);
			_isColorTemperatureUiModeActive = hasActiveColorTemperature;
			_colorTemperatureRangeMinimum = (long)TryGetColorTemperatureMinimum (device.Features);

			// Full-color bulbs expose the lightTunable capability, so their colour temperature always
			// uses the real lightColorTemperature capability (the colorTemperature parameter of
			// lightTunable:setLevels references lightColorTemperature:range) and never the emulated
			// style - emulated CT would overwrite hue/saturation and pin the bulb into white mode.
			// The UI mode is signalled explicitly via lightTunable:mode, so no emulated<->plain
			// capability swap is needed. TunableWhite bulbs (CT only, no hue/saturation) keep the plain
			// lightColorTemperature capability together with its individual setLevel command.
			bool useTunableCapability = _supportsFullColor;

			// In color mode there is no active color-temperature value to report; register the device's
			// real declared range (so the UI sees an honest range) and use the range minimum as an
			// in-range placeholder level rather than an out-of-range 0.
			long initialColorTemperatureLevel = device.LightState?.ColorTemperature is int activeCt && activeCt > 0
				? activeCt
				: _colorTemperatureRangeMinimum;
			DriverEntityValueRange initialColorTemperatureMemberRange = lightColorTemperatureRange!;

			_colorTemperatureLevelRange = lightColorTemperatureRange;
			_registeredColorTemperatureMembers = useTunableCapability
				? new TunableColorTemperatureMembers (this, initialColorTemperatureMemberRange, initialColorTemperatureLevel)
				: new ColorTemperatureMembers (this, initialColorTemperatureMemberRange, initialColorTemperatureLevel);
			RegisterObjectWithAttributes (_registeredColorTemperatureMembers);
			LogInfo ($"Light entity '{ControllerId}' registered color-temperature dynamic members: useTunableCapability={useTunableCapability}, initialColorTemperatureLevel={initialColorTemperatureLevel}.");
			}

		if (!_supportsFullColor)
			{
			LogInfo ($"Light entity '{ControllerId}' full-color members not registered because full-color state is not supported.");
			}
		else
			{
			// The device reports real hue/saturation values regardless of whether color temperature is
			// currently active (color temperature only overrides the displayed color while active; it
			// does not clear or alter the stored hue/saturation), so always seed the initial members
			// from those real values.
			int? hue = device.LightState?.Hue ?? device.LightState?.Hsv?.Hue;
			int? saturation = device.LightState?.Saturation ?? device.LightState?.Hsv?.Saturation;
			double initialHue = hue.HasValue ? Clamp01 (hue.Value / HUE_MAX_DEGREES) : 0d;
			double initialSaturation = saturation.HasValue ? Clamp01 (saturation.Value / 100d) : 0d;

			_registeredFullColorMembers = new FullColorMembers (this, initialHue, initialSaturation);
			RegisterObjectWithAttributes (_registeredFullColorMembers);
			LogInfo ($"Light entity '{ControllerId}' registered full-color dynamic members: initialHue={initialHue:0.####}, initialSaturation={initialSaturation:0.####}.");

			// Color-capable bulbs also expose the authoritative Crestron Home tuning mode so the UI's
			// color-vs-white tile is driven by an explicit mode signal rather than inferred from the
			// emulated color-temperature setLevel the processor injects on power-on.
			LightTunableTuningMode initialTuningMode = ComputeTuningMode ();
			_registeredLightTunableMembers = new LightTunableMembers (this, initialTuningMode);
			RegisterObjectWithAttributes (_registeredLightTunableMembers);
			LogInfo ($"Light entity '{ControllerId}' registered lightTunable dynamic members: initialMode={initialTuningMode}.");
			}

		RaiseDefinitionChangedEvent ();
		LogDynamicFeatureEntityState ("ConfigureDynamicFeatures.AfterDefinitionChanged");

		}

	// Bulbs that support both full color and color temperature (Color-kind Kasa/Tapo bulbs) can be
	// switched between color mode and white/CT mode at any time, and Crestron Home infers which mode a
	// light is in from which color-temperature capability is currently registered rather than from the
	// bulb kind. lightEmulatedColorTemperature signals "this is a color light; CT overrides the
	// displayed color", which is correct while the bulb is in color mode. lightColorTemperature signals
	// "this light's color state is defined purely by CT", which is correct while the bulb is in
	// white/CT mode - any persisted HSV is irrelevant to the UI until the bulb returns to color mode.
	// This method re-registers the appropriate capability whenever the device's active mode has
	// flipped since the last time dynamic features were configured/reconciled.
	private void ReconcileColorTemperatureCapability (bool hasActiveColorTemperature, int? colorTemperature)
		{
		if (_registeredColorTemperatureMembers is null || _colorTemperatureLevelRange is null)
			{
			return;
			}

		// Full-color bulbs use the lightTunable capability with a fixed real lightColorTemperature
		// capability; their UI mode is signalled explicitly via lightTunable:mode, so there is no
		// emulated<->plain capability swap to perform here.
		if (_registeredColorTemperatureMembers is TunableColorTemperatureMembers)
			{
			return;
			}

		bool shouldUseEmulatedColorTemperature = _supportsFullColor && !hasActiveColorTemperature;
		bool isCurrentlyEmulated = _registeredColorTemperatureMembers is EmulatedColorTemperatureMembers;
		if (shouldUseEmulatedColorTemperature == isCurrentlyEmulated)
			{
			return;
			}

		// A capability swap unregisters the current color-temperature DTO and registers the other one.
		// If this runs while a color-temperature slider interaction is in flight, it deletes the very
		// property the UI-issued command is bound to, and Crestron's LightTunable capability then fails
		// to retrieve 'lightEmulatedColorTemperature:range'/':level' and the command times out. Defer
		// the swap: record the requested target state and re-run it once the interaction has settled
		// (see ProcessSliderCommandAsync -> TryApplyDeferredColorTemperatureReconcile). The currently
		// registered DTO stays valid for the whole interaction, so in-flight commands keep working.
		if (IsSliderInteractionActive ())
			{
			_pendingColorTemperatureReconcile = true;
			_pendingColorTemperatureReconcileHasActive = hasActiveColorTemperature;
			_pendingColorTemperatureReconcileLevel = colorTemperature;
			LogInfo ($"Light entity '{ControllerId}' deferring color-temperature capability swap until slider interaction settles: hasActiveColorTemperature={hasActiveColorTemperature}, colorTemperature={(colorTemperature.HasValue ? colorTemperature.Value.ToString () : "<none>")}.");
			return;
			}

		// Entering color mode: there is no active color-temperature value to carry over, so use the
		// range minimum as an in-range placeholder level. Entering white/CT mode: use the device's
		// real, currently-reported value. Either way the declared range stays the device's real range.
		long currentLevel = shouldUseEmulatedColorTemperature
			? _colorTemperatureRangeMinimum
			: (hasActiveColorTemperature && colorTemperature.HasValue ? colorTemperature.Value : _registeredColorTemperatureMembers.LightColorTemperatureLevel);
		DriverEntityValueRange currentRange = _colorTemperatureLevelRange;

		UnregisterObjectWithAttributes (_registeredColorTemperatureMembers);

		_registeredColorTemperatureMembers = shouldUseEmulatedColorTemperature
			? new EmulatedColorTemperatureMembers (this, currentRange, currentLevel)
			: new ColorTemperatureMembers (this, currentRange, currentLevel);
		RegisterObjectWithAttributes (_registeredColorTemperatureMembers);
		RaiseDefinitionChangedEvent ();

		_pendingColorTemperatureReconcile = false;
		_pendingColorTemperatureReconcileLevel = null;

		LogInfo ($"Light entity '{ControllerId}' switched color-temperature capability: useEmulatedColorTemperature={shouldUseEmulatedColorTemperature}, currentLevel={currentLevel}, range={FormatRange (currentRange)}.");
		}

	// Re-runs a color-temperature capability swap that was deferred because a slider interaction was
	// active when it was requested (see ReconcileColorTemperatureCapability). Called once the slider
	// interaction has settled, when it is safe to unregister/register the DTO without breaking an
	// in-flight command.
	private void TryApplyDeferredColorTemperatureReconcile (string context)
		{
		if (!_pendingColorTemperatureReconcile || IsSliderInteractionActive ())
			{
			return;
			}

		bool hasActiveColorTemperature = _pendingColorTemperatureReconcileHasActive;
		int? colorTemperature = _pendingColorTemperatureReconcileLevel;
		_pendingColorTemperatureReconcile = false;

		LogInfo ($"Light entity '{ControllerId}' applying deferred color-temperature capability swap after slider interaction settled; context='{context}'.");
		ReconcileColorTemperatureCapability (hasActiveColorTemperature, colorTemperature);
		}

	// This timeout wraps EnsureConnectedAsync (which may need to fully reconnect if the connection was
	// previously reset/dropped) plus the command itself plus an optional post-command refresh. Live
	// logs show the exact same startup-connect slowdown pattern recurring here: after ResetConnectionState
	// drops a stale connection, the ensuing reconnect-then-command sequence routinely exceeds 8 seconds
	// under the same reload/network contention conditions that motivated raising StartupConnectTimeout,
	// causing the command to time out, retry, and often fail a second time while still reconnecting.
	// Align this with StartupConnectTimeout so a reconnect triggered by a command has the same realistic
	// budget as an initial startup connect.
	private static readonly TimeSpan DeviceCommandTimeout = StartupConnectTimeout;

	// A command against a long-idle bulb must first fully reconnect (cold TPAP PAKE handshake) before
	// the command itself can run. Live logs show that after ~8h idle the gate wait + cold handshake
	// alone consumed the entire single command budget (e.g. "acquired connection gate after 15356 ms"
	// then the handshake timed out at 20s), so the actual on/off request never reached the bulb and it
	// never physically switched. Give the connect phase its OWN budget, distinct from the command
	// execution budget, so a cold reconnect gets the same realistic time as an initial startup connect
	// and does not starve the command that follows it.
	private static readonly TimeSpan DeviceConnectTimeout = StartupConnectTimeout;
	private static readonly TimeSpan DeviceCommandRetryDelay = TimeSpan.FromSeconds (1);
	private const int DEVICE_COMMAND_MAX_ATTEMPTS = 2;

	protected async Task ExecuteDeviceCommandAsync (Func<KasaDevice, CancellationToken, Task> action, bool refreshAfterCommand = true, CancellationToken cancellationToken = default, bool reassertColorModeAfterRefresh = false)
		{
		for (int attempt = 1; ; attempt++)
			{
			try
				{
				await ExecuteDeviceCommandAttemptAsync (action, refreshAfterCommand, reassertColorModeAfterRefresh, cancellationToken).ConfigureAwait (false);
				return;
				}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
				throw;
				}
			catch (Exception ex) when (attempt < DEVICE_COMMAND_MAX_ATTEMPTS)
				{
				_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' command attempt {attempt} of {DEVICE_COMMAND_MAX_ATTEMPTS} failed; retrying after {DeviceCommandRetryDelay.TotalSeconds:0} second(s): {ex}");
				await Task.Delay (DeviceCommandRetryDelay, cancellationToken).ConfigureAwait (false);
				}
			}
		}

	private async Task ExecuteDeviceCommandAttemptAsync (Func<KasaDevice, CancellationToken, Task> action, bool refreshAfterCommand, bool reassertColorModeAfterRefresh, CancellationToken cancellationToken)
		{
		try
			{
			// Phase 1: connect (its own budget). Ensuring a connection may require a full cold reconnect
			// after a long idle period; give it the same realistic budget as an initial startup connect
			// so a slow handshake does not consume the command budget below.
			KasaDevice device;
			using (CancellationTokenSource connectTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken))
				{
				connectTimeoutSource.CancelAfter (DeviceConnectTimeout);
				CancellationToken connectToken = connectTimeoutSource.Token;
				try
					{
					device = await EnsureConnectedAsync (connectToken).ConfigureAwait (false);
					}
				catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && connectToken.IsCancellationRequested)
					{
					throw new TimeoutException ($"Light entity '{ControllerId}' connect timed out after {DeviceConnectTimeout.TotalSeconds:0} seconds.");
					}
				}

			// Phase 2: command execution (separate budget), now that a live connection exists.
			using CancellationTokenSource timeoutCancellationSource = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken);
			timeoutCancellationSource.CancelAfter (DeviceCommandTimeout);
			CancellationToken timeoutToken = timeoutCancellationSource.Token;

			try
				{
				timeoutToken.ThrowIfCancellationRequested ();
				await action (device, timeoutToken).ConfigureAwait (false);

				if (refreshAfterCommand)
					{
					await device.UpdateAsync (timeoutToken).ConfigureAwait (false);
					LogReportedState ("ExecuteDeviceCommandAsync.AfterCommand", device);
					}

				ApplyState (device);
				OnlineIndicatorIsOnline = true;
				ReadyIndicatorIsReady = true;

				// On power-on, Crestron's Load layer drives the load's ColorTemp channel and dispatches
				// lightEmulatedColorTemperature:setLevel to the capability layer BEFORE our dimmer
				// power-on runs, switching the UI to white/CCT. The emulated CT value cannot be
				// retracted (see RetractEmulatedColorTemperatureAssertion), so the authoritative fix is
				// to publish lightTunable:mode=Color: PublishActiveColorModeProperties emits the explicit
				// tuning mode plus the real hue/saturation, forcing the UI back to the color mode the
				// bulb never left.
				if (reassertColorModeAfterRefresh && LightIsOn && _supportsFullColor && !IsCurrentColorTemperatureUiMode ())
						{
						PublishActiveColorModeProperties ("ExecuteDeviceCommandAsync.ReassertColorModeAfterPowerOn");
						}
				}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutToken.IsCancellationRequested)
				{
				throw new TimeoutException ($"Light entity '{ControllerId}' command timed out after {DeviceCommandTimeout.TotalSeconds:0} seconds.");
				}
			}
		catch (Exception ex)
			{
			ResetConnectionState ();
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' command failed: {ex}");
			throw;
			}
		}

	private static Task SetBrightnessAsync (KasaDevice device, double relativeLevel, CancellationToken cancellationToken)
		{
		int brightness = (int)Math.Round (relativeLevel * 100d, MidpointRounding.AwayFromZero);
		return brightness <= 0
			? device.TurnLightOffAsync (cancellationToken)
			: device.SetBrightnessAsync (brightness, cancellationToken);
		}

	private async Task ExecutePowerAsync (KasaDevice device, bool on, CancellationToken cancellationToken)
		{
		string? childId = ChildId;
		if (!string.IsNullOrWhiteSpace (childId))
			{
			string resolvedChildId = childId!;
			if (on)
				{
				await device.TurnChildOnAsync (resolvedChildId, cancellationToken).ConfigureAwait (false);
				}
			else
				{
				await device.TurnChildOffAsync (resolvedChildId, cancellationToken).ConfigureAwait (false);
				}

			return;
			}

		if (Kind == ManagedLightKind.OnOff)
			{
			if (on)
				{
				await device.TurnOnAsync (cancellationToken).ConfigureAwait (false);
				}
			else
				{
				await device.TurnOffAsync (cancellationToken).ConfigureAwait (false);
				}

			return;
			}

		if (!on)
			{
			await device.TurnLightOffAsync (cancellationToken).ConfigureAwait (false);
			return;
			}

		await device.TurnLightOnAsync (cancellationToken).ConfigureAwait (false);
		}

	private void ApplyState (KasaDevice device, bool allowDeferredDeviceStateDuringSliderInteraction = false)
		{
		if (!allowDeferredDeviceStateDuringSliderInteraction && IsSliderInteractionActive ())
			{
			_deferredDeviceStatePending = true;
			LogInfo ($"Light entity '{ControllerId}' deferring device-driven state apply while slider interaction is active.");
			return;
			}

		_deferredDeviceStatePending = false;

		// Do not force-republish state here: ApplyStateCore already publishes each property
		// individually (via SetAndNotify) only when its value actually changes. The UI is the one
		// that issued the command (e.g. on/off) and already knows all other property values from the
		// initial connect, so unconditionally re-publishing everything on every command - including a
		// plain power toggle that changes nothing else - is both unnecessary and can cause the UI to
		// misinterpret the forced re-publish order as an actual color/white mode change.
		using (PropertyChangeTracker.StartBatchedUpdate (true))
			{
			ApplyStateCore (device);
			}
		}

	private void ApplyStateCore (KasaDevice device)
		{
		bool isOn = ResolvePowerState (device);
		int? brightness = device.LightState?.Brightness;
		int? hue = device.LightState?.Hue ?? device.LightState?.Hsv?.Hue;
		int? saturation = device.LightState?.Saturation ?? device.LightState?.Hsv?.Saturation;
		int? colorTemperature = device.LightState?.ColorTemperature;
		bool hasActiveColorTemperature = HasCurrentColorTemperatureState (device.LightState);

		LightIsOn = isOn;

		// Kasa/Tapo devices report the real, current color-temperature mode regardless of power state
		// (colorTemperature is 0 while in HSV/color mode and a real Kelvin value while in white/CT
		// mode, whether the bulb is on or off), so the reconciliation always uses the live reading
		// rather than being gated on isOn. Gating this on isOn previously allowed a stale/deferred
		// state apply from an earlier command to be applied after a fresher one, causing the
		// capability to flip to the wrong shape and then immediately flip back.
		if (_supportsColorTemperature)
			{
			ReconcileColorTemperatureCapability (hasActiveColorTemperature, colorTemperature);
			}

		_isColorTemperatureUiModeActive = _supportsColorTemperature && hasActiveColorTemperature;

		if (_supportsBrightness)
			{
			LightDimmerLevel = isOn && brightness.HasValue
				? Clamp01 (brightness.Value / 100d)
				: 0d;
			}

		if (_supportsColorTemperature && hasActiveColorTemperature)
			{
			// Only update the color-temperature level when the device is actually in white/CT mode and
			// reports a real value. Kasa/Tapo devices report color_temp=0 while in HSV/color mode and,
			// unlike hue/saturation, do not retain a "last known" Kelvin value that can be read back
			// once color mode is active - so there is nothing meaningful to apply in color mode.
			// The published level is always kept within the device's real declared range (its per-device
			// minimum is used as the in-range placeholder while in color mode - see ConfigureDynamicFeatures).
			// Go through the normal SetAndNotify path (not silently) so that a genuine external change
			// to the level (e.g. changed via the Kasa app while off) is actually published to the UI;
			// SetAndNotify already no-ops when the value is unchanged, so this never causes a spurious
			// publish on its own.
			SetActiveColorTemperatureLevel (colorTemperature!.Value);
			}

		if (_supportsFullColor)
			{
			// Kasa/Tapo devices retain their hue/saturation values independently of color temperature -
			// color temperature simply overrides the displayed color while active, without clearing or
			// altering the stored hue/saturation. The device reports the real hue/saturation values
			// regardless of whether color temperature is currently active, so always surface those
			// real values here; it is the color-temperature capability's job (lightEmulatedColorTemperature
			// for Color-kind bulbs) to signal the override to the Crestron Home UI, not zeroing hue/saturation.
			if (hue.HasValue)
				{
				LightColorHue = Clamp01 (hue.Value / HUE_MAX_DEGREES);
				}

			if (saturation.HasValue)
				{
				LightColorSaturation = Clamp01 (saturation.Value / 100d);
				}
			}

		OnStateApplied (device);
		}

	private static bool HasCurrentColorTemperatureState (LightState? lightState)
		{
		if (lightState?.ColorTemperature is not int colorTemperature || colorTemperature <= 0)
			{
			return false;
			}

		return true;
		}

	private static bool HasCurrentFullColorState (LightState? lightState)
		{
		return lightState?.Hsv is not null
			|| lightState?.Hue is not null
			|| lightState?.Saturation is not null;
		}

	private bool IsCurrentColorTemperatureUiMode ()
		{
		return _supportsColorTemperature && _isColorTemperatureUiModeActive;
		}

	// The Crestron Home tuning mode is derived from the same single source of truth as the internal
	// color-temperature UI mode flag: when color temperature is the active mode the bulb is in
	// White (CCT) tuning, otherwise it is in Color (hue/saturation) tuning.
	private LightTunableTuningMode ComputeTuningMode ()
		{
		return IsCurrentColorTemperatureUiMode () ? LightTunableTuningMode.White : LightTunableTuningMode.Color;
		}

	// Publishes the authoritative lightTunable:mode so the Crestron Home UI keeps the correct
	// color-vs-white tile regardless of any color-temperature setLevel the processor injected. This
	// force-publishes (rather than relying on the change-tracked setter) because snapshot and
	// power-on reassert paths must re-emit the mode even when it has not changed.
	private void PublishTuningMode (string context)
		{
		if (_registeredLightTunableMembers is null)
			{
			return;
			}

		LightTunableTuningMode mode = ComputeTuningMode ();
		_registeredLightTunableMembers.LightTunableMode = mode;
		PublishProperty ("lightTunable:mode", CreateValueForObject (mode), context);
		}

	private void PublishCurrentLightModeProperties (string context)
		{
		if (_supportsBrightness)
			{
			PublishProperty ("lightDimmer:level", new DriverEntityValue (LightDimmerLevel), context);
			}

		PublishColorModeStateProperties (context);
		}

	private long GetActiveColorTemperatureLevel ()
		{
		if (!_supportsColorTemperature)
			{
			return 0L;
			}

		return _registeredColorTemperatureMembers!.LightColorTemperatureLevel;
		}

	// The Crestron Home UI treats any published lightEmulatedColorTemperature:level as an assertion
	// that the color-temperature override is currently the active mode, and immediately switches the
	// light to white/CCT at that Kelvin value. While a color-capable bulb is in color/HSV mode there
	// is no active color-temperature value (the device reports color_temp=0 and we only hold the
	// range-minimum placeholder), so publishing the emulated level would spuriously flip the UI to
	// white. The emulated CT level must therefore only be published while color temperature is the
	// active mode. The plain lightColorTemperature capability (TunableWhite bulbs, which have no color
	// mode at all) is always the active mode and is always published.
	private bool ShouldPublishColorTemperatureLevel (bool colorTemperatureUiModeActive)
		{
		if (!_supportsColorTemperature)
			{
			return false;
			}

		// The plain lightColorTemperature capability is only registered for TunableWhite bulbs, which
		// have no colour mode at all - colour temperature is always their active mode, so always
		// publish it. For full-colour bulbs (the emulated capability, or the lightTunable capability's
		// TunableColorTemperatureMembers) publishing a CT level asserts white mode in the UI, so only
		// publish it while colour temperature is actually the active mode. Publishing it unconditionally
		// forces every colour-mode bulb into white on connect.
		if (_registeredColorTemperatureMembers is ColorTemperatureMembers)
			{
			return true;
			}

		return colorTemperatureUiModeActive;
		}

	private void PublishColorModeStateProperties (string context)
		{
		// The Crestron Home UI infers whether a light is currently in "color" or "white/CCT" mode from
		// whichever of lightColor:*/lightColorTemperature:level was most recently published, so the
		// publish order here must reflect the device's actual active mode (see IsCurrentColorTemperatureUiMode),
		// matching the convention used by PublishActiveColorModeProperties. Publishing them in a fixed
		// order regardless of active mode causes the UI to always show the last-published mode (white)
		// on reload even when the bulb is actually in color mode.
		bool colorTemperatureUiModeActive = IsCurrentColorTemperatureUiMode ();
		bool publishColorTemperatureLevel = ShouldPublishColorTemperatureLevel (colorTemperatureUiModeActive);

		PublishTuningMode (context);

		if (colorTemperatureUiModeActive)
			{
			if (_supportsFullColor)
				{
				PublishProperty ("lightColor:saturation", new DriverEntityValue (LightColorSaturation), context);
				PublishProperty ("lightColor:hue", new DriverEntityValue (LightColorHue), context);
				}

			if (publishColorTemperatureLevel)
				{
				PublishProperty (_registeredColorTemperatureMembers!.LevelPropertyId, new DriverEntityValue (GetActiveColorTemperatureLevel ()), context);
				}
			}
		else
			{
			if (publishColorTemperatureLevel)
				{
				PublishProperty (_registeredColorTemperatureMembers!.LevelPropertyId, new DriverEntityValue (GetActiveColorTemperatureLevel ()), context);
				}

			if (_supportsFullColor)
				{
				PublishProperty ("lightColor:saturation", new DriverEntityValue (LightColorSaturation), context);
				PublishProperty ("lightColor:hue", new DriverEntityValue (LightColorHue), context);
				}
			}
		}

	private void PublishActiveColorModeProperties (string context)
		{
		PublishTuningMode (context);

		if (ShouldPublishColorTemperatureLevel (IsCurrentColorTemperatureUiMode ()))
			{
			PublishProperty (_registeredColorTemperatureMembers!.LevelPropertyId, new DriverEntityValue (_registeredColorTemperatureMembers!.LightColorTemperatureLevel), context);
			}

		if (_supportsFullColor)
			{
			PublishProperty ("lightColor:saturation", new DriverEntityValue (LightColorSaturation), context);
			PublishProperty ("lightColor:hue", new DriverEntityValue (LightColorHue), context);
			}
		}

	private void PublishProperty (string propertyId, DriverEntityValue value, string context)
		{
		LogPublishedProperty (propertyId, "value", context, value);
		NotifyPropertyChanged (propertyId, value);
		}

	private void LogDynamicFeatureEntityState (string context)
		{
		if (Interlocked.Exchange (ref _dynamicFeatureStateDiagnosticLogged, 1) != 0)
			{
			return;
			}

		LogEntityStateSnapshot (context);
		}

	private void LogEntityStateSnapshot (string context)
		{

		try
			{
			object state = GetState ();
			string stateDescription = DescribeDiagnosticObject (state, 0, new List<object> ());
			LogInfo ($"Light entity '{ControllerId}' GetState snapshot after {context}: {stateDescription}");
			}
		catch (Exception ex)
			{
			_logger?.Log (_driverLogId, LogEntryLevel.Warning, $"Light entity '{ControllerId}' failed to capture GetState snapshot after {context}: {ex.Message}");
			}
		}

	private static string DescribeDiagnosticObject (object value, int depth, IList<object> visited)
		{
		if (value == null)
			{
			return "<null>";
			}

		Type valueType = value.GetType ();

		if (value is string text)
			{
			return $"\"{text}\"";
			}

		if (IsSimpleDiagnosticType (valueType))
			{
			if (value is IFormattable formattable)
				{
				return formattable.ToString (null, CultureInfo.InvariantCulture);
				}

			return value.ToString () ?? $"<{valueType.Name}>";
			}

		if (depth >= 4)
			{
			return $"<{valueType.Name}>";
			}

		if (!valueType.IsValueType)
			{
			for (int i = 0; i < visited.Count; i++)
				{
				if (ReferenceEquals (visited[i], value))
					{
					return $"<cycle:{valueType.Name}>";
					}
				}

			visited.Add (value);
			}

		try
			{
			if (value is IDictionary dictionary)
				{
				StringBuilder dictionaryBuilder = new ();
				dictionaryBuilder.Append (valueType.Name);
				dictionaryBuilder.Append ('{');

				bool firstEntry = true;
				foreach (DictionaryEntry entry in dictionary)
					{
					if (!firstEntry)
						{
						dictionaryBuilder.Append (", ");
						}

					firstEntry = false;
					dictionaryBuilder.Append (DescribeDiagnosticObject (entry.Key, depth + 1, visited));
					dictionaryBuilder.Append ('=');
					dictionaryBuilder.Append (DescribeDiagnosticObject (entry.Value, depth + 1, visited));
					}

				dictionaryBuilder.Append ('}');
				return dictionaryBuilder.ToString ();
				}

			if (value is IEnumerable enumerable)
				{
				StringBuilder sequenceBuilder = new ();
				sequenceBuilder.Append (valueType.Name);
				sequenceBuilder.Append ('[');

				bool firstItem = true;
				int itemCount = 0;
				foreach (object item in enumerable)
					{
					if (itemCount >= 20)
						{
						if (!firstItem)
							{
							sequenceBuilder.Append (", ");
							}

						sequenceBuilder.Append ("...");
						break;
						}

					if (!firstItem)
						{
						sequenceBuilder.Append (", ");
						}

					firstItem = false;
					sequenceBuilder.Append (DescribeDiagnosticObject (item, depth + 1, visited));
					itemCount++;
					}

				sequenceBuilder.Append (']');
				return sequenceBuilder.ToString ();
				}

			PropertyInfo[] properties = valueType
				.GetProperties (BindingFlags.Instance | BindingFlags.Public)
				.Where (property => property.CanRead && property.GetIndexParameters ().Length == 0)
				.OrderBy (property => property.Name, StringComparer.Ordinal)
				.ToArray ();

			if (properties.Length == 0)
				{
				return value.ToString () ?? $"<{valueType.Name}>";
				}

			StringBuilder objectBuilder = new ();
			objectBuilder.Append (valueType.Name);
			objectBuilder.Append ('{');

			for (int i = 0; i < properties.Length; i++)
				{
				if (i > 0)
					{
					objectBuilder.Append (", ");
					}

				PropertyInfo property = properties[i];
				objectBuilder.Append (property.Name);
				objectBuilder.Append ('=');

				try
					{
					object propertyValue = property.GetValue (value, null);
					objectBuilder.Append (DescribeDiagnosticObject (propertyValue, depth + 1, visited));
					}
				catch (Exception ex)
					{
					objectBuilder.Append ($"<error:{ex.GetType ().Name}>");
					}
				}

			objectBuilder.Append ('}');
			return objectBuilder.ToString ();
			}
		finally
			{
			if (!valueType.IsValueType && visited.Count > 0 && ReferenceEquals (visited[visited.Count - 1], value))
				{
				visited.RemoveAt (visited.Count - 1);
				}
			}
		}

	private static bool IsSimpleDiagnosticType (Type valueType)
		{
		Type underlyingType = Nullable.GetUnderlyingType (valueType) ?? valueType;

		return underlyingType.IsPrimitive
			|| underlyingType.IsEnum
			|| underlyingType == typeof (decimal)
			|| underlyingType == typeof (DateTime)
			|| underlyingType == typeof (DateTimeOffset)
			|| underlyingType == typeof (TimeSpan)
			|| underlyingType == typeof (Guid);
		}

	private void PublishProperty (string propertyId, DriverEntityValueRange value, string context)
		{
		DriverEntityValue entityValue = new (value);
		LogPublishedProperty (propertyId, "range", context, entityValue);
		NotifyPropertyChanged (propertyId, entityValue);
		}

	private void LogPublishedProperty (string propertyId, string valueKind, string context, DriverEntityValue value)
		{
		if (!IsLightModeDiagnosticProperty (propertyId))
			{
			return;
			}

		LogInfo ($"Light entity '{ControllerId}' publishing propertyId='{propertyId}', valueKind='{valueKind}', value={DescribeDiagnosticObject (value, 0, new List<object> ())}, context='{context}'.");
		}

	private static bool IsLightModeDiagnosticProperty (string propertyId)
		{
		return propertyId.StartsWith ("lightColor:", StringComparison.Ordinal)
			|| propertyId.StartsWith ("lightColorTemperature:", StringComparison.Ordinal)
			|| propertyId.StartsWith ("lightEmulatedColorTemperature:", StringComparison.Ordinal);
		}

	private async Task RefreshAndApplyStateAsync (CancellationToken cancellationToken)
		{
		await ExecuteWithConnectedDeviceAsync (async device =>
			{
				cancellationToken.ThrowIfCancellationRequested ();
				await device.UpdateAsync (cancellationToken).ConfigureAwait (false);
				RefreshDescriptorFromConnectedDevice (device, "RefreshAsync.AfterUpdate");
				LogReportedState ("RefreshAsync.AfterUpdate", device);
				bool previousSuppressPropertyNotifications = _suppressPropertyNotifications;
				_suppressPropertyNotifications = true;
				try
					{
					ApplyState (device);
					}
				finally
					{
					_suppressPropertyNotifications = previousSuppressPropertyNotifications;
					}
			}, cancellationToken).ConfigureAwait (false);

		OnlineIndicatorIsOnline = true;
		ReadyIndicatorIsReady = true;
		}

	private async Task ReconnectAndRefreshAsync (TimeoutException timeoutException, CancellationToken cancellationToken)
		{
		await ExecuteWithConnectedDeviceAsync (async device =>
			{
				string host = device.Configuration.Host;
				LogInfo ($"Light entity '{ControllerId}' refresh timeout; reconnecting fresh client to host '{host}'. Original error: {timeoutException.Message}");

				KasaDevice reconnectedDevice = await ConnectFromConfigurationAsync (updateState: true, cancellationToken).ConfigureAwait (false);
				_connectedDevice = reconnectedDevice;
				RefreshDescriptorFromConnectedDevice (reconnectedDevice, "ReconnectRefresh.AfterConnect");
				LogReportedState ("ReconnectRefresh.AfterConnect", reconnectedDevice);

				bool previousSuppressPropertyNotifications = _suppressPropertyNotifications;
				_suppressPropertyNotifications = true;
				try
					{
					ApplyState (reconnectedDevice);
					}
				finally
					{
					_suppressPropertyNotifications = previousSuppressPropertyNotifications;
					}
			}, cancellationToken).ConfigureAwait (false);
		}

	private DesiredLightCommand CreateBrightnessCommand (double relativeLevel)
		{
		return new DesiredLightCommand (DesiredLightMode.Brightness, relativeLevel, 0d, 0d, 0L);
		}

	private async Task ApplyBrightnessCommandAsync (KasaDevice device, double relativeLevel, CancellationToken cancellationToken)
		{
		if (relativeLevel <= 0d)
			{
			await device.TurnLightOffAsync (cancellationToken).ConfigureAwait (false);
			return;
			}

		if (device.LightState?.IsOn != true && device.IsOn != true)
			{
			await device.TurnLightOnAsync (cancellationToken).ConfigureAwait (false);
			return;
			}

		await device.SetBrightnessAsync (ToBrightnessPercent (relativeLevel), cancellationToken).ConfigureAwait (false);
		}

	private static async Task EnsurePoweredForLevelAsync (KasaDevice device, double relativeLevel, CancellationToken cancellationToken)
		{
		if (relativeLevel <= 0d || device.LightState?.IsOn == true || device.IsOn == true)
			{
			return;
			}

		await device.TurnLightOnAsync (cancellationToken).ConfigureAwait (false);
		}

	private double GetEffectiveOnLevel () => LightDimmerLevel > 0d ? LightDimmerLevel : 1d;

	private bool ResolvePowerState (KasaDevice device)
		{
		string? childId = ChildId;
		if (!string.IsNullOrWhiteSpace (childId))
			{
			string resolvedChildId = childId!;
			return device.GetChild (resolvedChildId)?.IsOn ?? false;
			}

		return Kind == ManagedLightKind.OnOff
			? device.IsOn ?? false
			: device.LightState?.IsOn ?? device.IsOn ?? false;
		}

	private static int ToBrightnessPercent (double relativeLevel) => Math.Max (1, (int)Math.Round (Clamp01 (relativeLevel) * 100d, MidpointRounding.AwayFromZero));

	private void LogReportedState (string stage, KasaDevice device)
		{
		int? brightness = device.LightState?.Brightness;
		int? hue = device.LightState?.Hue ?? device.LightState?.Hsv?.Hue;
		int? saturation = device.LightState?.Saturation ?? device.LightState?.Hsv?.Saturation;
		int? colorTemperature = device.LightState?.ColorTemperature;
		bool? isOn = device.LightState?.IsOn ?? device.IsOn;

		LogInfo ($"Light entity '{ControllerId}' {stage} raw state: isOn={FormatNullable (isOn)}, brightness={FormatNullable (brightness)}, hue={FormatNullable (hue)}, saturation={FormatNullable (saturation)}, colorTemperature={FormatNullable (colorTemperature)}.");
		}

	private void LogCommandInvocation (string commandId, string details)
		{
		LogInfo ($"Light entity '{ControllerId}' command invoked: commandId='{commandId}', details='{details}'.");
		}

	private void LogPublishedState ()
		{
		_ = IsCurrentColorTemperatureUiMode ();
		LogInfo ($"Light entity '{ControllerId}' published snapshot: isOn={LightIsOn}, dimmer={LightDimmerLevel:0.####}, hue={LightColorHue:0.####}, saturation={LightColorSaturation:0.####}, colorTemperature={GetActiveColorTemperatureLevel ()}, colorTemperatureUiMode={IsCurrentColorTemperatureUiMode ()}.");
		}

	private string FormatDesiredLightCommand (DesiredLightCommand command)
		{
		return $"mode={command.Mode}, level={command.Level:0.####}, hue={command.Hue:0.####}, saturation={command.Saturation:0.####}, colorTemperature={command.ColorTemperature}";
		}

	private static string FormatRange (DriverEntityValueRange? range)
		{
		return range is null
			? "<none>"
			: range.ToString ()!;
		}

	[Conditional ("DEBUG")]
	private void LogInfo (string message)
		{
		_logger?.Log (_driverLogId, LogEntryLevel.Info, message);
		}

	private static string FormatNullable<T> (T? value)
		where T : struct
		{
		return value.HasValue ? value.Value.ToString ()! : "null";
		}

	protected virtual void OnStateApplied (KasaDevice device)
		{
		}

	private static bool HasFeature (IReadOnlyList<DeviceFeature> features, string featureId)
		{
		return features.Any (feature => string.Equals (feature.Id, featureId, StringComparison.Ordinal));
		}

	private static ManagedLightKind InferManagedLightKind (KasaDevice device, KasaDeviceType discoveredDeviceType)
		{
		if (discoveredDeviceType == KasaDeviceType.Plug || discoveredDeviceType == KasaDeviceType.Strip && device.LightState is null)
			{
			return ManagedLightKind.OnOff;
			}

		IReadOnlyList<DeviceFeature> features = device.Features;
		bool supportsBrightness = HasFeature (features, BRIGHTNESS_FEATURE_ID);
		bool supportsColorTemperature = HasFeature (features, COLOR_TEMPERATURE_FEATURE_ID);
		bool supportsHue = HasFeature (features, HUE_FEATURE_ID);
		bool supportsSaturation = HasFeature (features, SATURATION_FEATURE_ID);
		bool hasColorState = HasCurrentFullColorState (device.LightState);
		bool supportsFullColor = (supportsHue && supportsSaturation) || hasColorState;

		if (supportsFullColor)
			{
			return ManagedLightKind.Color;
			}

		if (supportsColorTemperature)
			{
			return ManagedLightKind.TunableWhite;
			}

		return supportsBrightness
			? ManagedLightKind.Dimmable
			: ManagedLightKind.OnOff;
		}

	private static bool TryCreateColorTemperatureRange (IReadOnlyList<DeviceFeature> features, out DriverEntityValueRange range)
		{
		DeviceFeature? feature = features.FirstOrDefault (feature => string.Equals (feature.Id, COLOR_TEMPERATURE_FEATURE_ID, StringComparison.Ordinal));
		if (feature is not null && feature.MinimumValue.HasValue && feature.MaximumValue.HasValue)
			{
			range = new DriverEntityValueRange ((long)feature.MinimumValue.Value, (long)feature.MaximumValue.Value, 1);
			return true;
			}

		range = new DriverEntityValueRange (0, 0, 1);
		return false;
		}

	private static double TryGetColorTemperatureMinimum (IReadOnlyList<DeviceFeature> features)
		{
		DeviceFeature? feature = features.FirstOrDefault (feature => string.Equals (feature.Id, COLOR_TEMPERATURE_FEATURE_ID, StringComparison.Ordinal));
		return feature?.MinimumValue ?? 0d;
		}

	private static double Clamp01 (double value)
		{
		if (value < 0)
			{
			return 0;
			}

		if (value > 1)
			{
			return 1;
			}

		return value;
		}

	private void SetAndNotify (string propertyId, bool value, ref bool field)
		{
		if (field == value)
			{
			return;
			}

		field = value;
		if (_suppressPropertyNotifications)
			{
			return;
			}

		PublishProperty (propertyId, new DriverEntityValue (value), "SetAndNotify<bool>");
		}

	private void SetAndNotify (string propertyId, double value, ref double field)
		{
		if (Math.Abs (field - value) < 0.0001d)
			{
			return;
			}

		field = value;
		if (_suppressPropertyNotifications)
			{
			return;
			}

		PublishProperty (propertyId, new DriverEntityValue (value), "SetAndNotify<double>");
		}

	private void SetAndNotify (string propertyId, long value, ref long field)
		{
		if (field == value)
			{
			return;
			}

		field = value;
		if (_suppressPropertyNotifications)
			{
			return;
			}

		PublishProperty (propertyId, new DriverEntityValue (value), "SetAndNotify<long>");
		}

	private void SetAndNotify<T> (string propertyId, T value, ref T field)
		{
		if (EqualityComparer<T>.Default.Equals (field, value))
			{
			return;
			}

		field = value;
		if (_suppressPropertyNotifications)
			{
			return;
			}

		PublishProperty (propertyId, CreateValueForObject (value!), $"SetAndNotify<{typeof (T).Name}>");
		}

	}
