extern alias driverassembly;

using driverassembly::KasaTapoCrestronDriver;

namespace KasaTapoCrestronDriver.Tests;

/// <summary>
/// Regression tests for the pure color-temperature/tuning-mode decision logic extracted from
/// <c>KasaLightEntity</c> into <see cref="LightTuningDecisions"/>. These rules are the exact site of a
/// prior regression involving the colorTemperature vs emulatedColorTemperature DTO shapes: a full-color
/// (lightTunable-capable) bulb must always use the real, lightTunable-paired lightColorTemperature
/// capability and must never fall back to the emulated shape, since emulated CT overwrites hue/saturation
/// and would pin the bulb permanently into white mode.
/// </summary>
[TestClass]
public sealed class LightTuningDecisionsTests
	{
	[TestMethod]
	public void SelectInitialColorTemperatureShape_FullColorBulb_UsesTunableCapability ()
		{
		ColorTemperatureCapabilityShape shape = LightTuningDecisions.SelectInitialColorTemperatureShape (supportsFullColor: true);

		Assert.AreEqual (ColorTemperatureCapabilityShape.TunableColorTemperature, shape);
		}

	[TestMethod]
	public void SelectInitialColorTemperatureShape_TunableWhiteBulb_UsesPlainCapability ()
		{
		ColorTemperatureCapabilityShape shape = LightTuningDecisions.SelectInitialColorTemperatureShape (supportsFullColor: false);

		Assert.AreEqual (ColorTemperatureCapabilityShape.PlainColorTemperature, shape);
		}

	[TestMethod]
	public void ShouldUseEmulatedColorTemperature_FullColorBulbWithoutActiveColorTemperature_ReturnsTrue ()
		{
		bool result = LightTuningDecisions.ShouldUseEmulatedColorTemperature (supportsFullColor: true, hasActiveColorTemperature: false);

		Assert.IsTrue (result);
		}

	[TestMethod]
	public void ShouldUseEmulatedColorTemperature_FullColorBulbWithActiveColorTemperature_ReturnsFalse ()
		{
		bool result = LightTuningDecisions.ShouldUseEmulatedColorTemperature (supportsFullColor: true, hasActiveColorTemperature: true);

		Assert.IsFalse (result);
		}

	[TestMethod]
	public void ShouldUseEmulatedColorTemperature_TunableWhiteBulb_NeverEmulated ()
		{
		// A TunableWhite (CT-only) bulb never supports full color, so it must never select the
		// emulated shape regardless of whether color temperature is currently active.
		Assert.IsFalse (LightTuningDecisions.ShouldUseEmulatedColorTemperature (supportsFullColor: false, hasActiveColorTemperature: false));
		Assert.IsFalse (LightTuningDecisions.ShouldUseEmulatedColorTemperature (supportsFullColor: false, hasActiveColorTemperature: true));
		}

	[TestMethod]
	public void ComputeTuningMode_ColorTemperatureUiModeActive_ReturnsWhite ()
		{
		LightTunableTuningMode mode = LightTuningDecisions.ComputeTuningMode (isColorTemperatureUiModeActive: true);

		Assert.AreEqual (LightTunableTuningMode.White, mode);
		}

	[TestMethod]
	public void ComputeTuningMode_ColorTemperatureUiModeInactive_ReturnsColor ()
		{
		LightTunableTuningMode mode = LightTuningDecisions.ComputeTuningMode (isColorTemperatureUiModeActive: false);

		Assert.AreEqual (LightTunableTuningMode.Color, mode);
		}

	[TestMethod]
	public void RequestsWhiteMode_PositiveColorTemperature_ReturnsTrue ()
		{
		Assert.IsTrue (LightTuningDecisions.RequestsWhiteMode (colorTemperature: 3000L));
		}

	[TestMethod]
	public void RequestsWhiteMode_NullColorTemperature_ReturnsFalse ()
		{
		Assert.IsFalse (LightTuningDecisions.RequestsWhiteMode (colorTemperature: null));
		}

	[TestMethod]
	public void RequestsWhiteMode_ZeroColorTemperature_ReturnsFalse ()
		{
		// colorTemperature==0 signals colour/HSV mode for these bulbs, not white/CT mode.
		Assert.IsFalse (LightTuningDecisions.RequestsWhiteMode (colorTemperature: 0L));
		}

	[TestMethod]
	public void RequestsColorMode_HueOnly_ReturnsTrue ()
		{
		Assert.IsTrue (LightTuningDecisions.RequestsColorMode (hue: 0.5d, saturation: null));
		}

	[TestMethod]
	public void RequestsColorMode_SaturationOnly_ReturnsTrue ()
		{
		Assert.IsTrue (LightTuningDecisions.RequestsColorMode (hue: null, saturation: 0.5d));
		}

	[TestMethod]
	public void RequestsColorMode_NeitherPresent_ReturnsFalse ()
		{
		Assert.IsFalse (LightTuningDecisions.RequestsColorMode (hue: null, saturation: null));
		}

	[TestMethod]
	public void RequestsWhiteAndRequestsColor_MutuallyExclusiveOnActualLightTunableSetLevelsInputs ()
		{
		// Brightness-only change (neither tuning parameter present): retains current mode.
		Assert.IsFalse (LightTuningDecisions.RequestsWhiteMode (colorTemperature: null));
		Assert.IsFalse (LightTuningDecisions.RequestsColorMode (hue: null, saturation: null));

		// White/CT change.
		Assert.IsTrue (LightTuningDecisions.RequestsWhiteMode (colorTemperature: 2700L));
		Assert.IsFalse (LightTuningDecisions.RequestsColorMode (hue: null, saturation: null));

		// Color/HSV change.
		Assert.IsFalse (LightTuningDecisions.RequestsWhiteMode (colorTemperature: null));
		Assert.IsTrue (LightTuningDecisions.RequestsColorMode (hue: 0.25d, saturation: 0.75d));
		}
	}
