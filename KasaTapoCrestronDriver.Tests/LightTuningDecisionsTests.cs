extern alias driverassembly;

using driverassembly::KasaTapoCrestronDriver;

namespace KasaTapoCrestronDriver.Tests;

/// <summary>
/// Regression tests for the pure color-temperature/tuning-mode decision logic extracted from
/// <c>KasaLightEntity</c> into <see cref="LightTuningDecisions"/>. These rules are the exact site of a
/// prior regression involving the colorTemperature vs emulatedColorTemperature DTO shapes: a full-color
/// bulb must always expose the emulated color-temperature capability so Crestron Home has one stable
/// LightTunable contract, independent of whether HSV or CT is currently active.
/// </summary>
[TestClass]
public sealed class LightTuningDecisionsTests
	{
	[TestMethod]
	public void ShouldUseEmulatedColorTemperature_FullColorBulb_ReturnsTrue ()
		{
		bool result = LightTuningDecisions.ShouldUseEmulatedColorTemperature (supportsFullColor: true);

		Assert.IsTrue (result);
		}

	[TestMethod]
	public void ShouldUseEmulatedColorTemperature_TunableWhiteBulb_NeverEmulated ()
		{
		// A TunableWhite (CT-only) bulb never supports full color, so it must never select the
		// emulated shape.
		Assert.IsFalse (LightTuningDecisions.ShouldUseEmulatedColorTemperature (supportsFullColor: false));
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
	public void ToProcessorTuningMode_ColorTemperatureActive_ReturnsWhite ()
		{
		Assert.AreEqual (ProcessorLightTuningMode.White, LightTuningDecisions.ToProcessorTuningMode (colorTemperatureUiModeActive: true));
		}

	[TestMethod]
	public void ToProcessorTuningMode_ColorActive_ReturnsColor ()
		{
		Assert.AreEqual (ProcessorLightTuningMode.Color, LightTuningDecisions.ToProcessorTuningMode (colorTemperatureUiModeActive: false));
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
