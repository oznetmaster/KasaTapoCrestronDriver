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

internal partial class KasaLightEntity : ReflectedAttributeDriverEntity, IKasaManagedLightEntity
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

		public ColorTemperatureMembers (KasaLightEntity owner, DriverEntityValueRange range, long level)
			{
			_owner = owner;
			LightColorTemperatureRange = range;
			LightColorTemperatureLevel = level;
			}

		string IColorTemperatureLevelHolder.LevelPropertyId => "lightColorTemperature:level";

		[EntityProperty (Id = "lightColorTemperature:range", Units = "Kelvin")]
		public DriverEntityValueRange LightColorTemperatureRange { get; set; } = new DriverEntityValueRange (0, 0, 1);

		[EntityProperty (Id = "lightColorTemperature:level", RangeProperty = "lightColorTemperature:range", Units = "Kelvin")]
		public long LightColorTemperatureLevel
			{
			get;
			set => _owner.SetAndNotify ("lightColorTemperature:level", value, ref field);
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

		public EmulatedColorTemperatureMembers (KasaLightEntity owner, DriverEntityValueRange range, long level)
			{
			_owner = owner;
			LightEmulatedColorTemperatureRange = range;
			LightColorTemperatureLevel = level;
			}

		string IColorTemperatureLevelHolder.LevelPropertyId => "lightEmulatedColorTemperature:level";

		[EntityProperty (Id = "lightEmulatedColorTemperature:range", Units = "Kelvin")]
		public DriverEntityValueRange LightEmulatedColorTemperatureRange { get; set; } = new DriverEntityValueRange (0, 0, 1);

		[EntityProperty (Id = "lightEmulatedColorTemperature:level", RangeProperty = "lightEmulatedColorTemperature:range", Units = "Kelvin")]
		public long LightColorTemperatureLevel
			{
			get;
			set => _owner.SetAndNotify ("lightEmulatedColorTemperature:level", value, ref field);
			}

		[EntityCommand (Id = "lightEmulatedColorTemperature:setLevel")]
		public void SetColorTemperatureLevel ([EntityParameter (RangeProperty = "lightEmulatedColorTemperature:range", Units = "Kelvin")] long level)
			{
			_owner.SetColorTemperatureLevel ("lightEmulatedColorTemperature:setLevel", level);
			}
		}

	private sealed class FullColorMembers
		{
		private readonly KasaLightEntity _owner;

		public FullColorMembers (KasaLightEntity owner, double hue, double saturation)
			{
			_owner = owner;
			LightColorHue = hue;
			LightColorSaturation = saturation;
			}

		[EntityProperty (Id = "lightColor:hueRange")]
		public DriverEntityValueRelativeRange LightColorHueRange { get; } = new (1d / HUE_MAX_DEGREES);

		[EntityProperty (Id = "lightColor:hue", RelativeRangeProperty = "lightColor:hueRange")]
		public double LightColorHue
			{
			get;
			set => _owner.SetAndNotify ("lightColor:hue", value, ref field);
			}

		[EntityProperty (Id = "lightColor:saturationRange")]
		public DriverEntityValueRelativeRange LightColorSaturationRange { get; } = new (0.01);

		[EntityProperty (Id = "lightColor:saturation", RelativeRangeProperty = "lightColor:saturationRange")]
		public double LightColorSaturation
			{
			get;
			set => _owner.SetAndNotify ("lightColor:saturation", value, ref field);
			}

		// Every UI-settable property must have a paired command (see lightTunable:setMode above for the
		// rationale). Crestron Home's classic per-channel lighting engine (IRpcLights.SetTunableChannelState)
		// drives hue and saturation independently, one channel at a time, rather than through the
		// consolidated lightTunable:setLevels command - without these individual commands Crestron Home has
		// no way to invoke a hue/saturation-only change and silently resolves the channel state entirely
		// inside its own lighting engine, never calling into the driver at all.
		[EntityCommand (Id = "lightColor:setHue")]
		public void LightColorSetHue ([EntityParameter (RelativeRangeProperty = "lightColor:hueRange")] double level)
			{
			_owner.LightColorSetHue (level);
			}

		[EntityCommand (Id = "lightColor:setSaturation")]
		public void LightColorSetSaturation ([EntityParameter (RelativeRangeProperty = "lightColor:saturationRange")] double level)
			{
			_owner.LightColorSetSaturation (level);
			}
		}

	// Registered only for lights that report brightness support; mutually exclusive with
	// OnOffMembers - see ConfigureDynamicFeatures. Kept as its own dynamic object (rather than static
	// reflected members on the owner) so nothing ever needs to be added/removed after connect.
	private sealed class DimmableMembers
		{
		private readonly KasaLightEntity _owner;

		public DimmableMembers (KasaLightEntity owner, double initialLevel)
			{
			_owner = owner;
			LightDimmerLevel = initialLevel;
			}

		[EntityProperty (Id = "lightDimmer:levelRange")]
		public DriverEntityValueRelativeRange LightDimmerLevelRange { get; } = new (0.01);

		[EntityProperty (Id = "lightDimmer:level", RelativeRangeProperty = "lightDimmer:levelRange")]
		public double LightDimmerLevel
			{
			get;
			set => _owner.SetAndNotify ("lightDimmer:level", value, ref field);
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

		public OnOffMembers (KasaLightEntity owner, bool initialIsOn)
			{
			_owner = owner;
			LightIsOn = initialIsOn;
			}

		[EntityProperty (Id = "light:isOn")]
		public bool LightIsOn
			{
			get;
			set => _owner.SetAndNotify ("light:isOn", value, ref field);
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
	// because the Crestron lightDimmer reflected entity model has no dedicated power on/off member
	// for such lights: the UI/entity layer infers power state purely from lightDimmer:level (0 =
	// off, >0 = on). This is strictly a Crestron entity-model/UI convention, not a limitation of the
	// physical device or of KasaDevice: the bulb (and KasaDevice's independent TurnLightOnAsync /
	// TurnLightOffAsync / SetBrightnessAsync calls) tracks power and brightness as separate,
	// independently-retained values - confirmed by device status showing State=Off with a real,
	// nonzero Brightness. Without this fallback, LightIsOn would always read false for these bulbs,
	// which previously caused IgnoreValueCommandWhileOff to believe the light was always off and
	// force-republish/drop color commands even while genuinely on, producing a spurious color/white
	// mode flip in the UI.
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
	// This gate throttles the startup connect burst across ALL entities to relieve that contention.
	// It intentionally wraps ONLY the startup connect path, so steady-state per-device reconnects and
	// polling remain fully independent.
	private const int STARTUP_CONNECT_MAX_CONCURRENCY = 3;
	private static readonly SemaphoreSlim StartupConnectConcurrencyGate = new (STARTUP_CONNECT_MAX_CONCURRENCY, STARTUP_CONNECT_MAX_CONCURRENCY);

	private object _sliderGate { get; } = new ();
	private DriverControllerLogger _logger { get; }
	private string _driverLogId { get; }
	private Action<ManagedLightDescriptor>? _descriptorUpdated { get; }
	private IPlatformSharedConfiguration _sharedConfiguration { get; }
	private Func<string, ProcessorLightTuningMode, double, double, double, long, CancellationToken, Task>? _synchronizeProcessorBaselineAsync { get; }
	private CancellationTokenSource _lifetimeCancellationSource { get; } = new ();

	private ManagedLightDescriptor _descriptor { get; set; } = null!;
	private DeviceConfiguration? _configuration { get; set; }
	private KasaDevice? _connectedDevice;

	// Crestron Home dispatches overlapping commands against the same connected device during
	// power-on (e.g. lightDimmer:setLevel:on and lightEmulatedColorTemperature:setLevel both fire
	// from the Load layer). KasaDevice already serializes these internally, so one can queue long
	// enough for the OTHER'S own command timeout to elapse first. When that happens the failing
	// operation must NOT dispose the device out from under the operation still actively using it -
	// doing so previously produced an ObjectDisposedException cascade that left the light dead and
	// the UI offline. This counter tracks how many commands currently hold a live reference to
	// _connectedDevice's Phase 2 (post-connect) execution window, and _deviceAwaitingDisposal defers
	// actual disposal of a superseded device until the last active operation against it completes.
	private int _activeDeviceOperationCount;
	private KasaDevice? _deviceAwaitingDisposal;
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

	// Immediately after InitializeConnectedStateAsync publishes the bulb's real (already on, at
	// whatever brightness it actually was) state, Crestron Home's own Load layer has been observed
	// (on-disk log evidence) to independently dispatch its own lightDimmer:setLevel commands against
	// the freshly-(re)started entity - typically level=0 followed within a second or two by a
	// near-zero level (0.01) - with no genuine user interaction involved. Forwarding those to the
	// device as real commands was turning a bulb that was already on (at ANY real brightness,
	// including already-low ones like 1%) off, and then back on at the wrong brightness (100%, since
	// GetEffectiveOnLevel falls back to full brightness once LightDimmerLevel has been zeroed by the
	// spurious "off"). This grace window lets LightDimmerSetLevel recognize and ignore that specific
	// replay pattern (a near-zero level arriving very soon after we just published that the bulb was
	// genuinely on) instead of applying it to the device - regardless of what the real on-level
	// happened to be, since the replay is a Load-layer artifact unrelated to the bulb's actual level.
	private static readonly TimeSpan StartupDimmerReplayGuardWindow = TimeSpan.FromSeconds (5);
	private const double STARTUP_DIMMER_REPLAY_SUSPECT_LEVEL_THRESHOLD = 0.02;
	private DateTime? _startupDimmerReplayGuardUntilUtc { get; set; }
	private double _startupDimmerReplayRealLevel { get; set; }

	// The processor console synchronization dispatched by DispatchProcessorBaselineSynchronization
	// only issues a real off->on SetLoadState toggle against the load (see
	// ProcessorBaselineCoordinator.SynchronizeCoreAsync) when the load's stored/active console
	// tuning mode actually needs correcting - it is a no-op otherwise, e.g. when a power-off is
	// requested while the tuning mode already matches. This toggle is only ever dispatched from the
	// awaited startup path (DispatchProcessorBaselineSynchronizationAsync, called from
	// InitializeConnectedStateAsync) - it is the only way to correct the processor's console-level
	// TunableChannelStates.TuningMode before the entity ever reports online/ready, and Crestron
	// Home's own Load layer reacts to it by relaying BOTH halves of that toggle back to this entity
	// as genuine light:off then light:on (or dimmer) commands, indistinguishable from real user
	// actions. This class-level flag is the single source of truth for that startup window: it is
	// set immediately before the awaited startup coordinator call and cleared in a finally
	// immediately after, so ExecutePowerAsync can suppress both relayed power calls from reaching
	// the physical device during the deterministic span in which they can arrive, without guessing
	// at any fixed time window or threading a separate flag through the coordinator. Suppressing
	// only the "on" half is not sufficient: the relayed "off" half would otherwise reach the
	// physical device and turn off a bulb that was genuinely on before the reload, even though the
	// bulb itself was never touched by the console-only toggle. Outside this startup window, a
	// genuine user-requested power command (either direction) must always reach the device
	// immediately. Status queries (device.UpdateAsync) and UI-facing property updates are
	// unaffected and continue normally.
	private bool _isInitializingProcessorBaseline { get; set; }

	// While a color-capable bulb is in color/HSV mode, Kasa/Tapo devices report color_temp=0. This
	// is the device's supported inactive-mode sentinel, so the emulated CT range includes zero and
	// publishes zero in color mode. Retaining a Kelvin value in that state makes Crestron Home restore
	// White mode after a color-mode power cycle. The mode itself is kept stable by dispatching power-on/off directly
	// instead of through the deferred slider path - see LightDimmerSetLevel - not by the CT value.
	private long _colorTemperatureRangeMinimum { get; set; }
	private DimmableMembers? _registeredDimmableMembers { get; set; }
	private OnOffMembers? _registeredOnOffMembers { get; set; }

	// LightDimmerLevel is forced to 0 while the bulb is off (see ApplyStateCore) because, for
	// dimmable-only bulbs, lightDimmer:level also doubles as the Crestron power-state signal (0 =
	// off, >0 = on - see the DimmableMembers/LightIsOn comment above). Legacy KL130-style bulbs do,
	// however, genuinely retain their own last brightness while off (confirmed via device status:
	// State=Off, Brightness=50) - it is our own zeroing of LightDimmerLevel that previously lost
	// track of it, not a device limitation. This field mirrors the device's last-reported brightness
	// independent of power state so GetEffectiveOnLevel can restore it on the next power-on instead
	// of falling back to full brightness.
	private double? _lastKnownDeviceBrightnessLevel { get; set; }

	private int _stopState;
	private int _pollingGeneration;
	private bool _disposed { get; set; }

	public KasaLightEntity (
		string controllerId,
		ManagedLightDescriptor descriptor,
		DeviceConfiguration configuration,
			Action<ManagedLightDescriptor>? descriptorUpdated,
		IPlatformSharedConfiguration sharedConfiguration,
		Func<string, ProcessorLightTuningMode, double, double, double, long, CancellationToken, Task>? synchronizeProcessorBaselineAsync,
		DriverImplementationResources resources,
		DriverControllerLogger logger,
		string driverLogId)
		: base (controllerId)
		{
		_descriptorUpdated = descriptorUpdated;
		_sharedConfiguration = sharedConfiguration;
		_synchronizeProcessorBaselineAsync = synchronizeProcessorBaselineAsync;
		_logger = logger;
		_driverLogId = driverLogId;

		UpdateDescriptor (descriptor, configuration);
		_suppressPropertyNotifications = false;
		LogInfo ($"Light entity '{ControllerId}' created in passive discovered state; awaiting child configuration callback before activation.");
		}

	public string DeviceName { get; private set; } = string.Empty;

	public string ModelName { get; private set; } = string.Empty;

	public string SerialNumber { get; private set; } = string.Empty;

	}
