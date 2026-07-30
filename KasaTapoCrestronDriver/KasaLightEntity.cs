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

		// KasaClient's Discover now guarantees a single shared, persistent KasaDevice per
		// Host:Port, so there is no longer a distinct-instance race to guard against here - the
		// only remaining concern is not clobbering an already-attached device on this entity.
		// A lock-free compare-and-set is sufficient in place of the old connection gate. Because
		// Discover can hand back the SAME shared instance this entity already has attached (e.g.
		// alias-enrichment resolving to the entity's own live device), treat that case as an
		// already-satisfied attach rather than a failure - the caller (EnrichDiscoveryAliasCacheAsync)
		// disposes the device on a false return, which must never happen to a device this entity is
		// actively using.
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
		LogInfo ($"Light entity '{ControllerId}' adopted connected device from context='{context}'.");
		return true;
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

	// Individual channel command for full-color bulbs, paired with the lightColor:hue property. Crestron
	// Home's classic per-channel lighting engine (IRpcLights.SetTunableChannelState) invokes this when
	// the color wheel is dragged. Switches the bulb into color/HSV mode and re-dispatches the full
	// hue/saturation/brightness state.
	private void LightColorSetHue (double hue)
		{
		double hueLevel = Clamp01 (hue);
		LogCommandInvocation ("lightColor:setHue", $"hue={hueLevel:0.####}");

		if (!LightIsOn)
			{
			// While off, this can be a passive Crestron Home Load-layer replay of a stored hue value
			// against a bulb that is genuinely in white/CT mode (mirroring the dimmer replay pattern
			// documented elsewhere in this file), not a genuine user color selection. The driver must
			// never publish anything to the UI while the bulb is off - the only permitted off-state
			// UI action is the baseline sync that happens immediately after a genuine on->off
			// transition (see SynchronizeProcessorBaselineForPowerOff) - so only update this entity's
			// own internal tracking fields here, silently (no SetAndNotify/publish), and do not touch
			// the processor baseline.
			bool previousSuppressPropertyNotifications = _suppressPropertyNotifications;
			_suppressPropertyNotifications = true;
			try
				{
				_isColorTemperatureUiModeActive = false;
				LightColorHue = hueLevel;
				}
			finally
				{
				_suppressPropertyNotifications = previousSuppressPropertyNotifications;
				}
			return;
			}

		// While white/CT mode is active, ApplyStateCore intentionally leaves LightColorSaturation at
		// its neutral placeholder (0) rather than the device's real retained value (see its remarks).
		// If the user switches into color mode via the hue channel while that placeholder is still in
		// effect, dispatching hue alongside saturation=0 produces a fully desaturated (i.e. white/gray)
		// HSV command - the bulb never visibly changes color no matter what hue is selected, because
		// zero saturation IS "no color". Seed a full-saturation default in that specific transition so
		// the requested hue is actually visible; a genuine subsequent lightColor:setSaturation command
		// from the UI still overrides this default normally. This only applies to a live, powered-on
		// interaction (never the whileOff snapshot path above) so a passive replay while off cannot
		// fabricate a color state for a bulb that is really in CT mode.
		double saturationLevel = LightColorSaturation;
		if (_isColorTemperatureUiModeActive && saturationLevel <= 0d)
			{
			saturationLevel = 1d;
			}

		// Baseline synchronization is intentionally not performed here: SynchronizeProcessorBaseline
		// itself is a no-op whenever the light is already on (see its remarks), and this branch only
		// ever runs once LightIsOn has been confirmed true above, so any call here would always be
		// dead code. The console-level baseline is corrected at startup and immediately before the
		// next genuine power-off instead (see SynchronizeProcessorBaselineForPowerOff).
		_isColorTemperatureUiModeActive = false;
		LightColorHue = hueLevel;
		LightColorSaturation = saturationLevel;
		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Hsv, LightDimmerLevel, hueLevel, saturationLevel, 0L));
		}

	private void LightColorSetSaturation (double saturation)
		{
		double saturationLevel = Clamp01 (saturation);
		LogCommandInvocation ("lightColor:setSaturation", $"saturation={saturationLevel:0.####}");

		if (!LightIsOn)
			{
			// See the identical remark in LightColorSetHue's whileOff branch: the driver must never
			// publish anything to the UI while the bulb is off, so only update internal tracking
			// fields silently here.
			bool previousSuppressPropertyNotifications = _suppressPropertyNotifications;
			_suppressPropertyNotifications = true;
			try
				{
				_isColorTemperatureUiModeActive = false;
				LightColorSaturation = saturationLevel;
				}
			finally
				{
				_suppressPropertyNotifications = previousSuppressPropertyNotifications;
				}
			return;
			}

		// See the identical remark in LightColorSetHue: this branch only runs once LightIsOn is
		// already confirmed true, so SynchronizeProcessorBaseline would always be a no-op here.
		_isColorTemperatureUiModeActive = false;
		LightColorSaturation = saturationLevel;
		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Hsv, LightDimmerLevel, LightColorHue, saturationLevel, 0L));
		}

	private void LightDimmerSetLevel (double level)
		{
		double relativeLevel = Clamp01 (level);
		LogCommandInvocation ("lightDimmer:setLevel", $"level={relativeLevel:0.####}");

		// Detect and ignore Crestron Home's own post-reload Load-layer replay of a stale remembered
		// level (observed on-disk as level=0 then level=0.01 arriving within a couple of seconds of
		// InitializeConnectedStateAsync publishing the bulb's real, already-on, higher-brightness
		// state) - see the guard fields' remarks above. Applying these to the device was what dimmed
		// an already-on/high-brightness bulb down to 1% immediately after every reload.
		//
		// Both halves of that replay pair must be caught here, not just the second: the
		// lightDimmer:level slider representation cannot express "brightness while off" (level 0
		// always means off in that UI/entity model - see the DimmableMembers/LightIsOn comment
		// above), so the very fact that Crestron follows up level=0 with level=0.01 a moment later
		// proves the first level=0 was never a real user power-off either - it is simply the first
		// half of the same stale replay. Checking LightIsOn
		// here (rather than requiring it to still be true) intentionally also matches immediately
		// after we ourselves already let a replayed level=0 through and flipped LightIsOn to false, so
		// this must key off the guard window rather than the current on/off state.
		if (_startupDimmerReplayGuardUntilUtc is DateTime guardUntilUtc)
			{
			if (DateTime.UtcNow <= guardUntilUtc && relativeLevel <= STARTUP_DIMMER_REPLAY_SUSPECT_LEVEL_THRESHOLD)
				{
				LogInfo ($"Light entity '{ControllerId}' ignoring suspected post-startup Load-layer dimmer replay: level={relativeLevel:0.####}, realLevel={_startupDimmerReplayRealLevel:0.####}.");
				LightDimmerLevel = _startupDimmerReplayRealLevel;
				LightIsOn = true;
				PublishCurrentLightModeProperties ("lightDimmer:setLevel:ignoredStartupReplay", synchronizeProcessorBaseline: false);
				return;
				}

			_startupDimmerReplayGuardUntilUtc = null;
			}

		// Capture whether the bulb was on BEFORE mutating LightDimmerLevel below: for dimmable-only
		// bulbs (no OnOffMembers - see the LightIsOn getter's remarks), LightIsOn is derived live
		// from LightDimmerLevel itself (> 0 = on), so assigning LightDimmerLevel first would make
		// LightIsOn already read true by the time the off->on transition is checked below, causing
		// a genuine level=1 power-on request to be misidentified as "already on" and fall through
		// to the brightness dispatch path, which then explicitly sets the device to 100% brightness
		// instead of preserving whatever brightness the bulb already retained while off.
		bool wasOnBeforeThisCommand = LightIsOn;

		// For dimmable bulbs the extreme dimmer levels are the power commands: level 0 = power off,
		// level 1 = power on - but only genuinely when that represents an actual off->on transition.
		// If the bulb is already on and the user simply drags the slider to full brightness, level 1
		// is a real 100% brightness request, not a power command: routing it through ExecutePowerAsync
		// (which only calls TurnLightOnAsync and never touches brightness) leaves the device at
		// whatever brightness it already had, and the follow-up refreshAfterCommand then reads back
		// that unchanged (lower) brightness and republishes it - visibly "retreating" the slider/bulb
		// right after the drag reached 100%. So the power-on shortcut below only applies while the
		// bulb is confirmed off; otherwise level 1 falls through to the normal slider/brightness
		// dispatch path so the device is actually set to full brightness.
		//
		// These must NEVER be routed through the debounced slider path when they really are power
		// commands - even if they arrive during an in-flight slider drag - because that path marks the
		// subsequent device-driven state re-apply as deferred. During power-on Crestron's own Load
		// layer drives the load's ColorTemp channel (SetMultipleChannels ... ColorTemp), and deferring
		// our re-apply lets that Load-layer drive be the last word, flipping a color-mode bulb to
		// white/CCT in the UI. Cancel any pending slider interaction and dispatch the extremes directly
		// as power commands (identical to light:on / light:off), so the settled device state is applied
		// immediately and authoritatively.
		if (relativeLevel <= 0d)
			{
			LightDimmerLevel = relativeLevel;
			CancelSliderInteraction ();
			LightIsOn = false;

			// Only a genuine on->off transition warrants baseline synchronization: the processor's
			// stored baseline already reflects whatever mode was last actually applied, so replaying
			// (or re-sending) an off command while the bulb is already confirmed off - e.g. pressing
			// the UI off button again minutes later, or a Load-layer replay - has no real state change
			// to synchronize and must not re-invoke the baseline coordinator at all.
			string? powerOffContext = wasOnBeforeThisCommand ? "lightDimmer:setLevel:off" : null;
			StartBackgroundOperation (() => ExecuteDeviceCommandAsync ((device, cancellationToken) => ExecutePowerAsync (device, false, cancellationToken), refreshAfterCommand: true, _lifetimeCancellationSource.Token, synchronizeProcessorBaselineForPowerOffContext: powerOffContext), "lightDimmer:setLevel:off");
			return;
			}

		if (relativeLevel >= 1d && !wasOnBeforeThisCommand)
			{
			// The incoming level=1 here is Crestron's own power-on gesture value (its dimmer slider
			// UI always sends the "full" extreme to mean "turn on"), not a genuine 100% brightness
			// request - see the remarks above. ExecutePowerAsync only calls TurnLightOnAsync and
			// never touches brightness, so the device is actually about to come on at whatever
			// brightness it already retained while off. Publishing the raw level=1 here would
			// optimistically flash the UI to 100% for the ~1-2 seconds until refreshAfterCommand
			// reads the real (lower) brightness back and republishes it - exactly the visible delay
			// being reported. Publish the retained last-known real brightness instead so the UI
			// reflects the true settled state immediately, with no flash.
			LightDimmerLevel = GetEffectiveOnLevel ();
			CancelSliderInteraction ();
			LightIsOn = true;
			StartBackgroundOperation (() => ExecuteDeviceCommandAsync ((device, cancellationToken) => ExecutePowerAsync (device, true, cancellationToken), refreshAfterCommand: true, _lifetimeCancellationSource.Token, reassertColorModeAfterRefresh: true), "lightDimmer:setLevel:on");
			return;
			}

		LightDimmerLevel = relativeLevel;
		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.Brightness, relativeLevel, 0d, 0d, 0L));
		}

	// Registered only for simple on/off bulbs (see OnOffMembers/ConfigureDynamicFeatures) - those
	// bulbs have no color/CT mode and no processor console baseline concept at all, so this must
	// never touch SynchronizeProcessorBaselineForPowerOff.
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
		long temperatureLevel = Math.Max (1L, level);
		if (!LightIsOn)
			{
			// The UI sends this immediately before its dimmer power-on command, as an artifact of
			// Crestron's own Load layer driving the load's ColorTemp channel - not a genuine
			// user-requested mode change. The bulb's actual last-known state may still be full
			// color, so do NOT synchronize the processor baseline to White here: doing so caches a
			// White baseline before the device is ever confirmed to have changed mode, which then
			// causes the baseline coordinator to see a false "already matches" on the subsequent
			// power-on and skip the real color-mode correction.
			//
			// Likewise, do NOT flip _isColorTemperatureUiModeActive (and therefore do NOT publish
			// this as an active white-mode UI property) when the bulb's last confirmed real state
			// (from ApplyStateCore, reflecting the actual device) is genuinely in color mode. This
			// artifact fires unconditionally regardless of the bulb's real mode, so blindly trusting
			// it here would incorrectly flip the UI to White for a bulb that is really in Color mode
			// - the subsequent ExecuteDeviceCommandAsync.ReassertColorModeAfterPowerOn already
			// restores the real mode once the device is queried after power-on, so there is nothing
			// to gain by asserting White mode from this pre-power-on artifact alone. Only cache the
			// UI-facing level for later use; the mode/baseline is settled once the light is actually
			// turned on and its real state is read back.
			//
			// Crucially, do NOT call SetActiveColorTemperatureLevel here when confirmed color mode is
			// active either: that call directly sets the lightEmulatedColorTemperature:level property
			// via SetAndNotify, which pushes the new Kelvin value straight to the Crestron UI and is
			// enough on its own to flip the on-screen slider to White/CCT - independent of whatever
			// this driver's own _isColorTemperatureUiModeActive flag says afterward. Skipping the
			// level update entirely (in addition to skipping the mode flag and publish) is required
			// to fully suppress this artifact while the bulb is confirmed to be in color mode.
			// The driver must never publish anything to the UI while the bulb is off - the only
			// permitted off-state UI action is the baseline sync that runs immediately after a
			// genuine on->off transition (see SynchronizeProcessorBaselineForPowerOff). Crestron's
			// own Load layer optimistically renders its own UI slider from this artifact
			// independent of anything this driver does, so racing to counter-publish here gains
			// nothing: the real, settled color/CT mode is authoritatively restored once the light
			// actually turns on, via ExecuteDeviceCommandAsync.ReassertColorModeAfterPowerOn
			// re-applying it from live device state. Only cache the level internally, silently,
			// for later use when the bulb is confirmed to be in white/CT mode.
			if (_isColorTemperatureUiModeActive)
				{
				bool previousSuppressPropertyNotifications = _suppressPropertyNotifications;
				_suppressPropertyNotifications = true;
				try
					{
					SetActiveColorTemperatureLevel (temperatureLevel);
					}
				finally
					{
					_suppressPropertyNotifications = previousSuppressPropertyNotifications;
					}
				}
			return;
			}

		_isColorTemperatureUiModeActive = true;
		SetActiveColorTemperatureLevel (temperatureLevel);

		QueueSliderCommand (new DesiredLightCommand (DesiredLightMode.ColorTemperature, GetEffectiveOnLevel (), 0d, 0d, temperatureLevel));
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
		// This must genuinely hand off to the thread pool via Task.Run rather than invoking
		// operation() directly here: some downstream work (e.g. ProcessorBaselineCoordinator's
		// console round-trip, which blocks the calling thread with Thread.Sleep while polling for
		// an SSH response) is not written to yield at a real async boundary until well after it has
		// already done its blocking work. Invoking operation() inline therefore ran that blocking
		// work synchronously on the caller's thread - stalling e.g. LightDimmerSetLevel's off-path
		// for the full multi-second SSH round-trip before it could even reach the line that starts
		// the actual physical power-off dispatch. Task.Run guarantees the delegate (and any
		// synchronous blocking prefix within it) runs on a thread-pool thread instead, so callers
		// of StartBackgroundOperation return immediately regardless of what the operation does
		// before its first real await.
		Task task = Task.Run (() => operation ());

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

		if (_connectedDevice is null)
			{
			LogInfo ($"Light entity '{ControllerId}' child reached Running before connected device state was available; deferring current state snapshot; context='{context}'.");
			return;
			}

		LogInfo ($"Light entity '{ControllerId}' child reached Running with connected device state available; republishing current state snapshot; context='{context}'.");
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

		// Even when full state polling is disabled (so external/out-of-band changes to the
		// device are intentionally not detected), a light that has gone offline while in use
		// (e.g. the device was unplugged/lost power/lost Wi-Fi) still needs some way to recover
		// automatically once it comes back, rather than staying offline forever until a user
		// happens to issue another command against it. RunPollingCycleAsync always runs on this
		// same timer, but only performs a reconnect-and-refresh attempt when EnableLightPolling
		// is off if the entity is currently offline; it does nothing else in that case, so no
		// external state changes are surfaced while the device remains reachable.
		_pollingTask = RunPollingCycleAsync (generation);
		LogInfo ($"Light entity '{ControllerId}' RestartPolling: enabled={_sharedConfiguration.EnableLightPolling}, generation={generation}, intervalMs={_sharedConfiguration.LightPollInterval.TotalMilliseconds:0}.");
		}

	private void ResetConnectionState (bool flipIndicatorsOffline = true)
		{
		KasaDevice? staleDevice = _connectedDevice;
		LogInfo ($"Light entity '{ControllerId}' ResetConnectionState: hadConnectedDevice={staleDevice is not null}, flipIndicatorsOffline={flipIndicatorsOffline}.");
		_connectedDevice = null;
		_pendingStartupSnapshotAfterConnectedState = false;

		// A single failed attempt that will be immediately retried (see ExecuteDeviceCommandAsync)
		// should not visibly flap the UI to offline - a transient one-off TCP connect hiccup (e.g.
		// a dropped SYN during the device's own state transition) can resolve itself on the very
		// next attempt a second later. Only surface offline/not-ready once retries are exhausted.
		if (flipIndicatorsOffline)
			{
			OnlineIndicatorIsOnline = false;
			ReadyIndicatorIsReady = false;
			}

		if (staleDevice is null)
			{
			return;
			}

		// Do not dispose while another in-flight command is still actively executing against this
		// same device instance (see _activeDeviceOperationCount remarks above) - disposing here would
		// pull the transport/semaphore out from under that other operation's still-running command.
		// Defer disposal until the last active operation against this device finishes.
		if (Volatile.Read (ref _activeDeviceOperationCount) > 0)
			{
			LogInfo ($"Light entity '{ControllerId}' ResetConnectionState: deferring disposal, {_activeDeviceOperationCount} operation(s) still active on the stale device.");
			_deviceAwaitingDisposal = staleDevice;
			return;
			}

		try
			{
			staleDevice.Dispose ();
			}
		catch (Exception ex)
			{
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' failed to dispose stale connected device: {ex}");
			}
		}

	private void ReleaseDeviceOperation ()
		{
		if (Interlocked.Decrement (ref _activeDeviceOperationCount) > 0)
			{
			return;
			}

		KasaDevice? deferredDevice = Interlocked.Exchange (ref _deviceAwaitingDisposal, null);
		if (deferredDevice is null)
			{
			return;
			}

		try
			{
			deferredDevice.Dispose ();
			}
		catch (Exception ex)
			{
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' failed to dispose deferred stale connected device: {ex}");
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

		// Diagnostic: unlike LogDynamicFeatureEntityState (which only ever logs once, before this
		// startup snapshot is published), capture the full GetState() payload - the actual combined
		// definition/value contract Crestron Home receives - immediately after this deferred
		// snapshot so a mismatched initial UI mode can be correlated against exactly what was sent,
		// not just the individual PublishProperty calls logged elsewhere.
		LogEntityStateSnapshot ($"TryPublishDeferredStartupSnapshot.{context}");
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
				|| generation != Volatile.Read (ref _pollingGeneration))
				{
				return;
				}

			if (_sharedConfiguration.EnableLightPolling)
				{
				await RefreshAsync (_lifetimeCancellationSource.Token).ConfigureAwait (false);
				PublishStateSnapshot ();
				}
			else if (!OnlineIndicatorIsOnline)
				{
				// Polling is disabled (no external/out-of-band device-state detection), but an
				// offline device still needs a periodic chance to recover automatically - e.g. it
				// was unplugged and has since been plugged back in - rather than staying offline
				// indefinitely until a user happens to issue another command against it. Attempt
				// a lightweight reconnect-and-refresh here; ReconnectAndRefreshAsync already
				// restores OnlineIndicatorIsOnline/ReadyIndicatorIsReady and republishes the
				// current device state on success.
				try
					{
					await RefreshAsync (_lifetimeCancellationSource.Token).ConfigureAwait (false);
					PublishStateSnapshot ();
					}
				catch (OperationCanceledException)
					{
					throw;
					}
				catch (Exception ex)
					{
					LogInfo ($"Light entity '{ControllerId}' offline-recovery attempt failed; will retry on the next cycle: {ex.Message}");
					}
				}

			if (_disposed
				|| Volatile.Read (ref _stopState) != 0
				|| generation != Volatile.Read (ref _pollingGeneration))
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

		// Synchronize the processor baseline to the light's actual reported mode and AWAIT it to
		// completion before this entity reports itself online/ready. Live processor log evidence
		// (2026-07-26.log, 08:10:02-08:10:05) proved that dispatching this as a fire-and-forget
		// background operation loses the race on every reload: Crestron Home's own Load-layer
		// reconnect handshake renders its initial UI tile from the load's live
		// TunableChannelStates the moment the entity reports ready, which happens well before the
		// SSH console round-trip that corrects that stale state has a chance to land. Awaiting it
		// here closes that race deterministically. Without this, the baseline coordinator's cached
		// tuning mode can also go stale/out-of-sync with the bulb's true state across
		// reconnects/reloads (e.g. left over from a prior session), so a later spurious
		// pre-power-on mode command is wrongly treated as "already matches" and the real correction
		// is skipped.
		if (!string.IsNullOrWhiteSpace (DeviceName))
			{
			await DispatchProcessorBaselineSynchronizationAsync (LightTuningDecisions.ToProcessorTuningMode (IsCurrentColorTemperatureUiMode ()), "InitializeConnectedStateAsync.BeforeOnlineReady").ConfigureAwait (false);
			}

		OnlineIndicatorIsOnline = true;
		ReadyIndicatorIsReady = true;
		PublishCurrentLightModeProperties ("InitializeConnectedStateAsync.AfterOnlineReady", synchronizeProcessorBaseline: false);

		// Arm the startup dimmer-replay guard (see remarks on the fields above) whenever the bulb is
		// actually reported on, regardless of its real brightness level - even a genuinely low level
		// like 1% must be protected, since the replay pattern is a Load-layer artifact unrelated to
		// what the real level happens to be. If Crestron Home's Load layer replays a stale near-zero
		// level against this entity within the guard window, LightDimmerSetLevel will detect and
		// ignore it instead of turning the real bulb off and later back on at the wrong brightness.
		if (LightIsOn)
			{
			_startupDimmerReplayRealLevel = LightDimmerLevel;
			_startupDimmerReplayGuardUntilUtc = DateTime.UtcNow + StartupDimmerReplayGuardWindow;
			}
		else
			{
			_startupDimmerReplayGuardUntilUtc = null;
			}

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
		// This entity connects via Discover.GetOrConnectSharedAsync (see
		// ConnectFromConfigurationAsync), which shares a single, long-lived KasaDevice per
		// Host:Port across all coordinated callers (this entity's command path and
		// PlatformDriver's alias-enrichment path). Some other caller of that same shared device
		// - e.g. alias-enrichment on a different controller, or another entity that resolved to
		// the same host - can dispose it independently of anything this entity did. Unlike a
		// purely local cache, this entity's own ResetConnectionState is not the only path that
		// can leave _connectedDevice stale, so IsDisposed must be checked here too before
		// reusing it, exactly like Discover's own shared-instance cache does for its entries.
		if (existingDevice is not null && !existingDevice.IsDisposed)
			{
			return existingDevice;
			}

		if (existingDevice is not null && existingDevice.IsDisposed)
			{
			LogInfo ($"Light entity '{ControllerId}' EnsureConnectedAsync detected a disposed shared device instance; reconnecting.");
			_ = Interlocked.CompareExchange (ref _connectedDevice, null, existingDevice);
			}

		cancellationToken.ThrowIfCancellationRequested ();
		_lifetimeCancellationSource.Token.ThrowIfCancellationRequested ();

		// KasaClient's Discover now owns the single-shared-device-per-Host:Port guarantee (it
		// coalesces concurrent connects for the same key and returns the same live KasaDevice
		// instance to every caller), so there is no longer a need to serialize connect attempts
		// behind a local gate here. If two commands race past the null check above, both calls
		// below resolve to the very same KasaDevice instance from Discover, so the final
		// assignment to _connectedDevice is idempotent regardless of which caller wins the race.
		using var connectCancellationSource = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken, _lifetimeCancellationSource.Token);
		connectCancellationSource.CancelAfter (DeviceConnectTimeout);
		Stopwatch connectStopwatch = Stopwatch.StartNew ();
		KasaDevice connectedDevice;
		try
			{
			connectedDevice = await ConnectFromConfigurationAsync (updateState: true, connectCancellationSource.Token).ConfigureAwait (false);
			}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && connectCancellationSource.IsCancellationRequested)
			{
			throw new TimeoutException ($"Light entity '{ControllerId}' connect timed out after {DeviceConnectTimeout.TotalSeconds:0} seconds.");
			}
		connectStopwatch.Stop ();

		LogInfo ($"Light entity '{ControllerId}' connect succeeded after {connectStopwatch.Elapsed.TotalMilliseconds:0} ms: deviceAlias='{connectedDevice.Alias ?? "<null>"}', systemInfoAlias='{connectedDevice.SystemInfo?.Alias ?? "<null>"}', model='{connectedDevice.SystemInfo?.Model ?? "<null>"}', deviceId='{connectedDevice.SystemInfo?.DeviceId ?? "<null>"}'.");

		_connectedDevice = connectedDevice;
		UpdateDescriptorFromConnectedDevice (connectedDevice);
		return connectedDevice;
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

		// Use the explicit, opt-in shared-connection API: this entity's own command path and
		// PlatformDriver's alias-enrichment path are coordinated call sites that both target the
		// same device identity (host/port) and are expected to reuse one live connection rather
		// than each dialing their own, since some devices reject or reset additional concurrent
		// sessions. Discover.ConnectAsync (plain) no longer shares instances across separate
		// calls as of KasaClient 1.2.3.
		return await Discover.GetOrConnectSharedAsync (
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

		// ManagedLightDescriptor.Kind's setter already no-ops when the recomputed value matches what
		// is already assigned, and only throws if it would actually change after being resolved once
		// (see its setter) - by design, recomputing it here on every connect/reconnect is intentional
		// so a light whose Kind has not yet been resolved (still Unknown) gets classified as soon as
		// real device state is available. That contract only holds because InferManagedLightKind
		// itself must be deterministic for a given bulb's fixed hardware capability - it must never
		// depend on transient state (e.g. which mode happens to be active right now), since two
		// different connect attempts reading different transient states would then race to assign
		// two different Kind values and the second one would trip the immutability guard.
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

			// In color mode there is no active color-temperature value to report; register the device's
			// declared Kelvin range and retain an in-range placeholder level for the reflected member.
			// The processor UI must not receive this value while HSV mode is active.
			bool useEmulatedColorTemperature = LightTuningDecisions.ShouldUseEmulatedColorTemperature (_supportsFullColor);
			long initialColorTemperatureLevel = device.LightState?.ColorTemperature is int activeCt && activeCt > 0
				? activeCt
				: _colorTemperatureRangeMinimum;
			DriverEntityValueRange initialColorTemperatureMemberRange = lightColorTemperatureRange!;

			// Capability identity is fixed for an entity's lifetime. A color-capable bulb always exposes
			// lightEmulatedColorTemperature, whether CT or HSV is presently active; a CT-only bulb always
			// exposes lightColorTemperature. Changing the reflected property ID after Home binds its built-in
			// LightTunable UI to it leaves that UI with a stale command/property contract.
			_registeredColorTemperatureMembers = useEmulatedColorTemperature
				? new EmulatedColorTemperatureMembers (this, initialColorTemperatureMemberRange, initialColorTemperatureLevel)
				: new ColorTemperatureMembers (this, initialColorTemperatureMemberRange, initialColorTemperatureLevel);
			RegisterObjectWithAttributes (_registeredColorTemperatureMembers);
			LogInfo ($"Light entity '{ControllerId}' registered color-temperature dynamic members: useEmulatedColorTemperature={useEmulatedColorTemperature}, initialColorTemperatureLevel={initialColorTemperatureLevel}.");
			}

		if (!_supportsFullColor)
			{
			LogInfo ($"Light entity '{ControllerId}' full-color members not registered because full-color state is not supported.");
			}
		else
			{
			// The device retains real hue/saturation values internally regardless of whether color
			// temperature is currently active (color temperature only overrides the displayed color
			// while active; it does not clear or alter the stored hue/saturation). However, just like
			// the color-temperature level uses an in-range placeholder while color mode is active (see
			// above), the initial lightColor:hue/saturation members must not surface the real retained
			// values while white/CT mode is active - Crestron Home infers the light is in color mode
			// purely from these properties being non-placeholder, regardless of publish order. Seed
			// them from the real values only while color mode is genuinely active; otherwise use
			// neutral placeholders (0).
			int? hue = device.LightState?.Hue ?? device.LightState?.Hsv?.Hue;
			int? saturation = device.LightState?.Saturation ?? device.LightState?.Hsv?.Saturation;
			bool seedRealHueSaturation = !_isColorTemperatureUiModeActive;
			double initialHue = seedRealHueSaturation && hue.HasValue ? Clamp01 (hue.Value / HUE_MAX_DEGREES) : 0d;
			double initialSaturation = seedRealHueSaturation && saturation.HasValue ? Clamp01 (saturation.Value / 100d) : 0d;

			_registeredFullColorMembers = new FullColorMembers (this, initialHue, initialSaturation);
			RegisterObjectWithAttributes (_registeredFullColorMembers);
			LogInfo ($"Light entity '{ControllerId}' registered full-color dynamic members: initialHue={initialHue:0.####}, initialSaturation={initialSaturation:0.####}, seedRealHueSaturation={seedRealHueSaturation}.");
			}

		RaiseDefinitionChangedEvent ();
		LogDynamicFeatureEntityState ("ConfigureDynamicFeatures.AfterDefinitionChanged");

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

	protected async Task ExecuteDeviceCommandAsync (Func<KasaDevice, CancellationToken, Task> action, bool refreshAfterCommand = true, CancellationToken cancellationToken = default, bool reassertColorModeAfterRefresh = false, string? synchronizeProcessorBaselineForPowerOffContext = null)
		{
		for (int attempt = 1; ; attempt++)
			{
			bool isFinalAttempt = attempt >= DEVICE_COMMAND_MAX_ATTEMPTS;
			try
				{
				await ExecuteDeviceCommandAttemptAsync (action, refreshAfterCommand, reassertColorModeAfterRefresh, synchronizeProcessorBaselineForPowerOffContext, isFinalAttempt, cancellationToken).ConfigureAwait (false);
				return;
				}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
				throw;
				}
			catch (Exception ex) when (!isFinalAttempt)
				{
				_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' command attempt {attempt} of {DEVICE_COMMAND_MAX_ATTEMPTS} failed; retrying after {DeviceCommandRetryDelay.TotalSeconds:0} second(s): {ex}");
				await Task.Delay (DeviceCommandRetryDelay, cancellationToken).ConfigureAwait (false);
				}
			}
		}

	private async Task ExecuteDeviceCommandAttemptAsync (Func<KasaDevice, CancellationToken, Task> action, bool refreshAfterCommand, bool reassertColorModeAfterRefresh, string? synchronizeProcessorBaselineForPowerOffContext, bool isFinalAttempt, CancellationToken cancellationToken)
		{
		try
			{
			// Phase 1: connect. EnsureConnectedAsync itself only arms the DeviceConnectTimeout
			// around the actual connect attempt, AFTER the connection gate has been acquired, so
			// contention from other concurrently-dispatched commands (e.g. the paired
			// lightEmulatedColorTemperature:setLevel and lightDimmer:setLevel:on commands fired
			// together for a single power-on gesture) waiting on the same gate no longer eats into
			// the connect budget. Do not additionally timebox the gate wait here.
			KasaDevice device = await EnsureConnectedAsync (cancellationToken).ConfigureAwait (false);

			// Phase 2: command execution (separate budget), now that a live connection exists.
			using CancellationTokenSource timeoutCancellationSource = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken);
			timeoutCancellationSource.CancelAfter (DeviceCommandTimeout);
			CancellationToken timeoutToken = timeoutCancellationSource.Token;

			// Hold a reference count against this device instance for the duration of Phase 2 so that
			// if another concurrently-dispatched command (e.g. the paired lightDimmer/lightEmulated
			// ColorTemperature commands fired together during power-on) times out and calls
			// ResetConnectionState() while THIS operation is still executing against the same device,
			// the device is not disposed until this operation also finishes (see ReleaseDeviceOperation).
			Interlocked.Increment (ref _activeDeviceOperationCount);
			try
				{
				try
					{
					timeoutToken.ThrowIfCancellationRequested ();
					await action (device, timeoutToken).ConfigureAwait (false);

					if (refreshAfterCommand)
						{
						await device.UpdateAsync (timeoutToken).ConfigureAwait (false);
						LogReportedState ("ExecuteDeviceCommandAsync.AfterCommand", device);
						}

					// The driver must never publish anything to the UI while the bulb is off - the only
					// permitted off-state UI action is the baseline sync dispatched below. This refresh
					// following a genuine power-off reads the bulb's real, settled state purely so that
					// baseline sync has accurate live data to work with; it must not surface as UI
					// property notifications, since in the overwhelming common case nothing about the
					// bulb's retained brightness/hue/saturation genuinely changed just because it was
					// turned off.
					bool isGenuinePowerOffRefresh = synchronizeProcessorBaselineForPowerOffContext is not null;
					bool previousSuppressPropertyNotifications = _suppressPropertyNotifications;
					if (isGenuinePowerOffRefresh)
						{
						_suppressPropertyNotifications = true;
						}

					try
						{
						ApplyState (device);
						}
					finally
						{
						if (isGenuinePowerOffRefresh)
							{
							_suppressPropertyNotifications = previousSuppressPropertyNotifications;
							}
						}

					OnlineIndicatorIsOnline = true;
					ReadyIndicatorIsReady = true;

					// On power-on, Crestron's Load layer drives the load's ColorTemp channel and dispatches
					// lightEmulatedColorTemperature:setLevel to the capability layer BEFORE our dimmer
					// power-on runs, switching the UI to white/CCT. PublishActiveColorModeProperties then
					// restores the full-color state when the bulb remains in color mode. The processor's
					// console-level baseline is intentionally NOT touched here: baseline synchronization
					// only ever occurs at startup and immediately before a genuine power-off (see
					// SynchronizeProcessorBaselineForPowerOff) - never during a power-on - so there is
					// nothing to correct on the console mid-power-on, and SynchronizeProcessorBaseline
					// itself is a no-op anyway while the light is on.
					if (reassertColorModeAfterRefresh && LightIsOn && _supportsFullColor && !IsCurrentColorTemperatureUiMode ())
						{
						PublishActiveColorModeProperties ("ExecuteDeviceCommandAsync.ReassertColorModeAfterPowerOn");
						}

					// Baseline synchronization for a genuine power-off must be based on the live state the
					// bulb itself reports AFTER it has actually been turned off (per the user's explicit
					// requirement), not on cached hue/saturation/color-temperature properties captured
					// before the physical off call ran. ApplyState (device) above has already refreshed
					// those properties from the just-completed device.UpdateAsync response, so dispatching
					// here reads exactly that live, settled state. This still runs through the normal
					// fire-and-forget StartBackgroundOperation dispatch, so it never delays returning
					// control back to the caller of the (already-completed) physical off command.
					if (synchronizeProcessorBaselineForPowerOffContext is string powerOffContext)
						{
						SynchronizeProcessorBaselineForPowerOff (powerOffContext);
						}
					}
				catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutToken.IsCancellationRequested)
					{
					throw new TimeoutException ($"Light entity '{ControllerId}' command timed out after {DeviceCommandTimeout.TotalSeconds:0} seconds.");
					}
				}
			finally
				{
				ReleaseDeviceOperation ();
				}
			}
		catch (Exception ex)
			{
			// Do not flip the UI to offline for an attempt that is about to be quietly retried - only
			// once the retry budget is exhausted does this become a real, user-visible offline state.
			// The stale connection is still cleared either way so the next attempt reconnects fresh.
			ResetConnectionState (flipIndicatorsOffline: isFinalAttempt);
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
		// Both halves of a genuine physical power command may never be suppressed, delayed, or
		// silently dropped here - EXCEPT for the deterministic duration of the awaited startup
		// processor baseline synchronization: that window is the only time the processor console
		// workaround ever performs its own off->on SetLoadState toggle, and Crestron's own Load
		// layer relays BOTH halves of that toggle back to this entity as indistinguishable genuine
		// light:off/light:on (or dimmer) commands. Nothing the entity has done yet during that
		// window is a real user action - the entity has not even published its initial state - so
		// both relayed halves must be suppressed, not just the "on" half: letting the relayed "off"
		// half reach the physical device would actually turn off a bulb that was genuinely on before
		// the reload, even though the toggle that produced it never touched the real bulb itself.
		// The flag's narrow, startup-only scope means this can never suppress an actual
		// user-requested power command outside that window.
		if (_isInitializingProcessorBaseline)
			{
			LogInfo ($"Light entity '{ControllerId}' suppressing physical device power-{(on ? "on" : "off")} call relayed from the startup processor baseline synchronization toggle.");
			return;
			}

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

		_isColorTemperatureUiModeActive = _supportsColorTemperature && hasActiveColorTemperature;

		if (_supportsBrightness)
			{
			if (brightness.HasValue)
				{
				// Track the device's real reported brightness independent of power state: legacy
				// bulbs genuinely retain their own last brightness while off (confirmed via device
				// status showing State=Off with a real Brightness value), so this must not be
				// cleared just because the bulb is off.
				_lastKnownDeviceBrightnessLevel = Clamp01 (brightness.Value / 100d);
				}

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
			// altering the stored hue/saturation. However, surfacing those real retained values to the
			// UI while white/CT mode is genuinely active asserts color mode to Crestron Home regardless
			// of publish order (mirroring how publishing a CT level asserts white mode - see
			// ShouldPublishColorTemperatureLevel), so only apply the real values while color mode is
			// genuinely active; otherwise leave the published hue/saturation at their neutral
			// placeholders until the bulb actually returns to color mode.
			if (!hasActiveColorTemperature)
				{
				if (hue.HasValue)
					{
					LightColorHue = Clamp01 (hue.Value / HUE_MAX_DEGREES);
					}

				if (saturation.HasValue)
					{
					LightColorSaturation = Clamp01 (saturation.Value / 100d);
					}
				}
			}

		OnStateApplied (device);
		}

	private static bool HasCurrentColorTemperatureState (LightState? lightState)
		{
		// To the bulb itself, color_temp > 0 is the sole and authoritative signal that white/CT
		// mode is active; color_temp == 0 always means color/HSV mode, regardless of the reported
		// saturation value (some devices report a stale/meaningless saturation of 0 while still
		// genuinely in color mode). Do not infer white mode from saturation.
		return lightState?.ColorTemperature is int colorTemperature && colorTemperature > 0;
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

	private void PublishCurrentLightModeProperties (string context, bool synchronizeProcessorBaseline = true)
		{
		if (_supportsBrightness)
			{
			PublishProperty ("lightDimmer:level", new DriverEntityValue (LightDimmerLevel), context);
			}

		PublishColorModeStateProperties (context);
		if (synchronizeProcessorBaseline)
			{
			SynchronizeProcessorBaseline (LightTuningDecisions.ToProcessorTuningMode (IsCurrentColorTemperatureUiMode ()), context);
			}
		}

	private void SynchronizeProcessorBaseline (ProcessorLightTuningMode mode, string context)
		{
		// The processor baseline workaround exists solely to correct confusion between the two
		// tuning modes the processor's own Load layer can render (Color vs White/CT) - a bulb that
		// supports full color but has no white/CT mode at all (_supportsColorTemperature is false)
		// can only ever be in Color mode, so there is nothing for the baseline to ever disagree
		// about and this must be skipped entirely for such bulbs.
		if (!_supportsFullColor || !_supportsColorTemperature || !_sharedConfiguration.EnableProcessorBaselineWorkaround || _synchronizeProcessorBaselineAsync is null || string.IsNullOrWhiteSpace (DeviceName))
			{
			return;
			}

		// The processor only ever asserts BaselineSetting.TuningMode into the Crestron Home UI on an
		// actual off->on power transition (see ProcessorBaselineCoordinator.SynchronizeCoreAsync),
		// which is the only reason the coordinator forces a real SetLoadState off/on toggle in the
		// first place. While the light is already on, nothing currently rendered depends on the
		// console-level baseline, so synchronizing now would only force that toggle - and the
		// several-second physical flicker that comes with it - for no visible benefit. Skip it
		// entirely here; SynchronizeProcessorBaselineForPowerOff recomputes the actual mode from live
		// state and synchronizes right before the light is actually turned off instead.
		if (LightIsOn)
			{
			return;
			}

		DispatchProcessorBaselineSynchronization (mode, context);
		}

	// Synchronizes the processor's console-level baseline using the light's actual current mode,
	// called immediately before the light is commanded off. There is nothing to cache: by the time
	// the light is genuinely being turned off, LightColorHue/LightColorSaturation/color-temperature
	// already reflect the real, settled mode, so it can simply be read and applied here rather than
	// tracked speculatively while the light was on.
	private void SynchronizeProcessorBaselineForPowerOff (string context)
		{
		if (!_supportsFullColor || !_supportsColorTemperature)
			{
			return;
			}

		DispatchProcessorBaselineSynchronization (LightTuningDecisions.ToProcessorTuningMode (IsCurrentColorTemperatureUiMode ()), context);
		}

	private void DispatchProcessorBaselineSynchronization (ProcessorLightTuningMode mode, string context)
		{
		if (!_supportsFullColor || !_supportsColorTemperature || !_sharedConfiguration.EnableProcessorBaselineWorkaround || _synchronizeProcessorBaselineAsync is null || string.IsNullOrWhiteSpace (DeviceName))
			{
			return;
			}

		// Capture the current level/hue/saturation/color-temperature synchronously here, rather than
		// reading them lazily from inside the queued background delegate below. StartBackgroundOperation
		// only starts running once the console round-trip to resolve/initialize the processor baseline
		// load completes (can take several seconds). Only this driver process restarts on a reload;
		// Crestron Home's own Load/UI layer keeps running and performs its own reconnect handshake
		// against the freshly-restarted entity, re-asserting its last-known level/mode as genuine
		// commands against the load (e.g. lightDimmer:setLevel level=0 then 0.01 immediately after
		// InitializeConnectedStateAsync publishes the real state - the same documented Load-layer
		// channel-driving behavior seen elsewhere, e.g. around power-on ColorTemp commands). Reading
		// the properties lazily would race against that handshake and send the processor a stale value
		// that no longer matches what was actually true when this synchronization was requested - which
		// is exactly what produced a spurious low-brightness SetLoadState during initialization.
		double dimmerLevel = LightDimmerLevel;
		double hue = LightColorHue;
		double saturation = LightColorSaturation;
		long colorTemperature = GetActiveColorTemperatureLevel ();
		StartBackgroundOperation (
			() => InvokeProcessorBaselineSynchronization (mode, dimmerLevel, hue, saturation, colorTemperature),
			$"ProcessorBaseline:{context}");
		}

	// Awaited counterpart of DispatchProcessorBaselineSynchronization, used only from
	// InitializeConnectedStateAsync. Live processor log evidence (2026-07-26.log, 08:10:02-08:10:05)
	// showed the fire-and-forget background dispatch losing the race every time on a reload: the SSH
	// console round-trip that corrects the processor's stale TunableChannelStates.TuningMode does not
	// land until ~1-2 seconds after OnlineIndicatorIsOnline/ReadyIndicatorIsReady are published, and
	// Crestron Home's own Load-layer reconnect handshake renders its initial UI tile from the load's
	// live TunableChannelStates immediately once the entity reports ready - well before that toggle
	// commits. Awaiting the synchronization here, before the entity is marked online/ready, closes
	// that race deterministically instead of leaving the initial UI tile dependent on background
	// timing. Physical device power calls are still suppressed for the duration via
	// RunProcessorBaselineSynchronizationSuppressingPowerCalls.
	private async Task DispatchProcessorBaselineSynchronizationAsync (ProcessorLightTuningMode mode, string context)
		{
		if (!_supportsFullColor || !_supportsColorTemperature || !_sharedConfiguration.EnableProcessorBaselineWorkaround || _synchronizeProcessorBaselineAsync is null || string.IsNullOrWhiteSpace (DeviceName))
			{
			return;
			}

		double dimmerLevel = LightDimmerLevel;
		double hue = LightColorHue;
		double saturation = LightColorSaturation;
		long colorTemperature = GetActiveColorTemperatureLevel ();
		try
			{
			await RunProcessorBaselineSynchronizationSuppressingPowerCalls (mode, dimmerLevel, hue, saturation, colorTemperature).ConfigureAwait (false);
			}
		catch (Exception ex)
			{
			_logger?.Log (_driverLogId, LogEntryLevel.Error, $"Light entity '{ControllerId}' awaited processor baseline synchronization '{context}' failed: {ex}");
			}
		}

	// Wraps the actual console round-trip with the deterministic power-call suppression flag: the
	// off->on SetLoadState toggle performed inside _synchronizeProcessorBaselineAsync (see
	// ProcessorBaselineCoordinator.SynchronizeCoreAsync) is what causes Crestron's Load layer to
	// relay a power command back at this entity, so the flag only ever needs to be armed for the
	// exact duration of this awaited call - no timeout guess required. This wrapper is only ever
	// invoked from the awaited startup path (DispatchProcessorBaselineSynchronizationAsync), so
	// _isInitializingProcessorBaseline is armed only for that startup window. The fire-and-forget
	// post-off/off-state synchronizations dispatched from DispatchProcessorBaselineSynchronization
	// call InvokeProcessorBaselineSynchronization directly instead, without arming the flag - a
	// genuine, user-requested power-on arriving while one of those background syncs happens to still
	// be in flight must never be suppressed.
	private async Task RunProcessorBaselineSynchronizationSuppressingPowerCalls (ProcessorLightTuningMode mode, double dimmerLevel, double hue, double saturation, long colorTemperature)
		{
		_isInitializingProcessorBaseline = true;
		try
			{
			await InvokeProcessorBaselineSynchronization (mode, dimmerLevel, hue, saturation, colorTemperature).ConfigureAwait (false);
			}
		finally
			{
			_isInitializingProcessorBaseline = false;
			}
		}

	private Task InvokeProcessorBaselineSynchronization (ProcessorLightTuningMode mode, double dimmerLevel, double hue, double saturation, long colorTemperature)
		{
		return _synchronizeProcessorBaselineAsync! (DeviceName, mode, dimmerLevel, hue, saturation, colorTemperature, _lifetimeCancellationSource.Token);
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
	// that the color-temperature override is currently active. While a color-capable bulb is in HSV
	// mode, do not publish its device-level color_temp=0 sentinel through this UI property.
	private bool ShouldPublishColorTemperatureLevel (bool colorTemperatureUiModeActive)
		{
		if (!_supportsColorTemperature)
			{
			return false;
			}

		// The plain lightColorTemperature capability is only registered for TunableWhite bulbs, which
		// have no colour mode at all. For full-colour bulbs, only an active Kelvin value is publishable
		// without asserting White mode in the processor UI.
		if (_registeredColorTemperatureMembers is ColorTemperatureMembers)
			{
			return true;
			}

		return colorTemperatureUiModeActive;
		}

	private void PublishColorModeStateProperties (string context)
		{
		bool colorTemperatureUiModeActive = IsCurrentColorTemperatureUiMode ();
		bool publishColorTemperatureLevel = ShouldPublishColorTemperatureLevel (colorTemperatureUiModeActive);

		if (_supportsFullColor && !colorTemperatureUiModeActive)
			{
			PublishProperty ("lightColor:hue", new DriverEntityValue (LightColorHue), context);
			PublishProperty ("lightColor:saturation", new DriverEntityValue (LightColorSaturation), context);
			}

		if (publishColorTemperatureLevel)
			{
			PublishProperty (_registeredColorTemperatureMembers!.LevelPropertyId, new DriverEntityValue (GetActiveColorTemperatureLevel ()), context);
			}
		}

	private void PublishActiveColorModeProperties (string context)
		{
		bool colorTemperatureUiModeActive = IsCurrentColorTemperatureUiMode ();
		if (ShouldPublishColorTemperatureLevel (colorTemperatureUiModeActive))
			{
			PublishProperty (_registeredColorTemperatureMembers!.LevelPropertyId, new DriverEntityValue (_registeredColorTemperatureMembers!.LightColorTemperatureLevel), context);
			}

		if (_supportsFullColor && !colorTemperatureUiModeActive)
			{
			PublishProperty ("lightColor:hue", new DriverEntityValue (LightColorHue), context);
			PublishProperty ("lightColor:saturation", new DriverEntityValue (LightColorSaturation), context);
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
			|| propertyId.StartsWith ("lightEmulatedColorTemperature:", StringComparison.Ordinal)
			|| propertyId.StartsWith ("lightTunable:", StringComparison.Ordinal);
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

	private double GetEffectiveOnLevel ()
		{
		if (LightDimmerLevel > 0d)
			{
			return LightDimmerLevel;
			}

		if (_lastKnownDeviceBrightnessLevel is double lastKnownLevel && lastKnownLevel > 0d)
			{
			return lastKnownLevel;
			}

		return 1d;
		}

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

		// ManagedLightDescriptor.Kind is a one-time/locked classification (see
		// ManagedLightDescriptor.Kind's setter) that PlatformDriver uses to select the managed
		// entity type and identity for this controllerId for the entity's entire lifetime - it must
		// therefore reflect the bulb's fixed hardware *capability*, never its transient currently
		// active mode. A full-color-capable bulb (e.g. KL130) always reports retained hue/saturation
		// values from the device, even while it is currently operating in white/color-temperature
		// mode - the bulb keeps its last HSV color in memory purely so it can be restored later - so
		// supportsFullColor already reflects the real, permanent capability on its own. Gating this on
		// whichever mode happens to be active at the moment this particular connect attempt reads the
		// device is a race: a bulb sitting in white mode would infer TunableWhite here, but if an
		// earlier or later reconnect within the same startup retry loop reads it while in color mode
		// it would infer Color instead, and the second assignment then throws (Kind cannot change
		// after initialization), aborting startup.
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
