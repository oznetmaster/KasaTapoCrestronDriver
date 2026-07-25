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
	/// Determines whether the emulated color-temperature capability (rather than the plain
	/// lightColorTemperature capability) should be registered for a full-color bulb.
	/// </summary>
	/// <param name="supportsFullColor">Whether the bulb reports hue/saturation state (Color-kind bulb).</param>
	public static bool ShouldUseEmulatedColorTemperature (bool supportsFullColor)
		{
		return supportsFullColor;
		}

	/// <summary>
	/// Mirrors the mode-derivation rule previously used by the removed lightTunable:setLevels
	/// command: colorTemperature present means white/CT mode is requested.
	/// </summary>
	public static bool RequestsWhiteMode (long? colorTemperature)
		{
		return colorTemperature.HasValue && colorTemperature.Value > 0L;
		}

	/// <summary>
	/// Mirrors the mode-derivation rule previously used by the removed lightTunable:setLevels
	/// command: hue or saturation present means color/HSV mode is requested.
	/// </summary>
	public static bool RequestsColorMode (double? hue, double? saturation)
		{
		return hue.HasValue || saturation.HasValue;
		}

	public static ProcessorLightTuningMode ToProcessorTuningMode (bool colorTemperatureUiModeActive)
		{
		return colorTemperatureUiModeActive
			? ProcessorLightTuningMode.White
			: ProcessorLightTuningMode.Color;
		}
	}

/// <summary>
