namespace KasaTapoCrestronDriver;

/// <summary>
/// Pure decision logic extracted from <see cref="KasaLightEntity"/> for the color-temperature DTO
/// shape selection and tuning-mode/request derivation rules. These rules previously lived only as
/// private logic inline in <see cref="KasaLightEntity"/>, which made them impossible to unit test in
/// isolation (they required a live/connected <c>KasaDevice</c>). Extracting them here as static,
/// side-effect-free methods lets the regression-prone rules (which capability - lightColorTemperature,
/// lightEmulatedColorTemperature, or the lightTunable-paired real lightColorTemperature - should be
/// registered/kept, and whether a command requests white/color mode) be exercised directly by tests
/// without changing any runtime behavior. <see cref="KasaLightEntity"/> calls into these methods; it
/// does not duplicate the rules.
/// </summary>
internal static class LightTuningDecisions
	{
	/// <summary>
	/// Identifies which color-temperature capability shape should be registered for a bulb, given its
	/// declared capabilities. Full-color bulbs that expose the lightTunable capability always use the
	/// real lightColorTemperature capability (paired with lightTunable:setLevels) and never the emulated
	/// shape or an individually-settable lightColorTemperature - see the remarks on
	/// <see cref="KasaLightEntity.ConfigureDynamicFeatures"/> for the full rationale.
	/// </summary>
	/// <param name="supportsFullColor">Whether the bulb reports hue/saturation state (Color-kind bulb).</param>
	/// <returns><c>true</c> when the lightTunable-paired real lightColorTemperature capability
	/// (<see cref="ColorTemperatureCapabilityShape.TunableColorTemperature"/>) should be used.</returns>
	public static ColorTemperatureCapabilityShape SelectInitialColorTemperatureShape (bool supportsFullColor)
		{
		return supportsFullColor
			? ColorTemperatureCapabilityShape.TunableColorTemperature
			: ColorTemperatureCapabilityShape.PlainColorTemperature;
		}

	/// <summary>
	/// Determines whether the emulated color-temperature capability (rather than the plain
	/// lightColorTemperature capability) should be registered for a bulb that does NOT expose the
	/// lightTunable capability (i.e. <see cref="ColorTemperatureCapabilityShape.TunableColorTemperature"/>
	/// is not in play - see <see cref="KasaLightEntity.ReconcileColorTemperatureCapability"/>, which
	/// returns immediately without swapping when the tunable capability is registered).
	/// </summary>
	/// <param name="supportsFullColor">Whether the bulb reports hue/saturation state (Color-kind bulb).</param>
	/// <param name="hasActiveColorTemperature">Whether the device is currently reporting an active
	/// (greater-than-zero) color-temperature value.</param>
	public static bool ShouldUseEmulatedColorTemperature (bool supportsFullColor, bool hasActiveColorTemperature)
		{
		return supportsFullColor && !hasActiveColorTemperature;
		}

	/// <summary>
	/// Derives the Crestron Home lightTunable:mode value from the same single source of truth used
	/// throughout <see cref="KasaLightEntity"/>: when color temperature is the active mode the bulb is
	/// in White (CCT) tuning, otherwise it is in Color (hue/saturation) tuning.
	/// </summary>
	public static LightTunableTuningMode ComputeTuningMode (bool isColorTemperatureUiModeActive)
		{
		return isColorTemperatureUiModeActive ? LightTunableTuningMode.White : LightTunableTuningMode.Color;
		}

	/// <summary>
	/// Mirrors the mode-derivation rule in <c>LightTunableSetLevels</c>: the tuning parameter present on
	/// a lightTunable:setLevels command signals the requested mode - colorTemperature present means
	/// white/CT mode is requested.
	/// </summary>
	public static bool RequestsWhiteMode (long? colorTemperature)
		{
		return colorTemperature.HasValue && colorTemperature.Value > 0L;
		}

	/// <summary>
	/// Mirrors the mode-derivation rule in <c>LightTunableSetLevels</c>: the tuning parameter present on
	/// a lightTunable:setLevels command signals the requested mode - hue or saturation present means
	/// color/HSV mode is requested.
	/// </summary>
	public static bool RequestsColorMode (double? hue, double? saturation)
		{
		return hue.HasValue || saturation.HasValue;
		}
	}

/// <summary>
/// The mutually-exclusive color-temperature capability shapes a bulb can register - see the Crestron
/// Lights API docs referenced throughout <see cref="KasaLightEntity"/> for the semantics of each.
/// </summary>
internal enum ColorTemperatureCapabilityShape
	{
	/// <summary>The plain lightColorTemperature capability, with its own lightColorTemperature:setLevel
	/// command. Used for TunableWhite-kind bulbs (CT only, no hue/saturation).</summary>
	PlainColorTemperature,

	/// <summary>The lightEmulatedColorTemperature capability, with its own
	/// lightEmulatedColorTemperature:setLevel command. Used for full-color bulbs while in color mode
	/// (i.e. not currently reporting an active color-temperature value).</summary>
	EmulatedColorTemperature,

	/// <summary>The real lightColorTemperature capability without its own setLevel command; all CT
	/// changes arrive through lightTunable:setLevels instead. Used for full-color bulbs that expose the
	/// lightTunable capability.</summary>
	TunableColorTemperature,
	}
