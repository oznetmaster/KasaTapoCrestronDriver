// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

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
[TestFixture]
public sealed class LightTuningDecisionsTests
	{
	[Test]
	public void ShouldUseEmulatedColorTemperature_FullColorBulb_ReturnsTrue ()
		{
		bool result = LightTuningDecisions.ShouldUseEmulatedColorTemperature (supportsFullColor: true);

		Assert.That (result, Is.True);
		}

	[Test]
	public void ShouldUseEmulatedColorTemperature_TunableWhiteBulb_NeverEmulated ()
		{
		// A TunableWhite (CT-only) bulb never supports full color, so it must never select the
		// emulated shape.
		Assert.That (LightTuningDecisions.ShouldUseEmulatedColorTemperature (supportsFullColor: false), Is.False);
		}

	[Test]
	public void RequestsWhiteMode_PositiveColorTemperature_ReturnsTrue ()
		{
		Assert.That (LightTuningDecisions.RequestsWhiteMode (colorTemperature: 3000L), Is.True);
		}

	[Test]
	public void RequestsWhiteMode_NullColorTemperature_ReturnsFalse ()
		{
		Assert.That (LightTuningDecisions.RequestsWhiteMode (colorTemperature: null), Is.False);
		}

	[Test]
	public void RequestsWhiteMode_ZeroColorTemperature_ReturnsFalse ()
		{
		// colorTemperature==0 signals colour/HSV mode for these bulbs, not white/CT mode.
		Assert.That (LightTuningDecisions.RequestsWhiteMode (colorTemperature: 0L), Is.False);
		}

	[Test]
	public void RequestsColorMode_HueOnly_ReturnsTrue ()
		{
		Assert.That (LightTuningDecisions.RequestsColorMode (hue: 0.5d, saturation: null), Is.True);
		}

	[Test]
	public void RequestsColorMode_SaturationOnly_ReturnsTrue ()
		{
		Assert.That (LightTuningDecisions.RequestsColorMode (hue: null, saturation: 0.5d), Is.True);
		}

	[Test]
	public void RequestsColorMode_NeitherPresent_ReturnsFalse ()
		{
		Assert.That (LightTuningDecisions.RequestsColorMode (hue: null, saturation: null), Is.False);
		}

	[Test]
	public void ToProcessorTuningMode_ColorTemperatureActive_ReturnsWhite ()
		{
		Assert.That (LightTuningDecisions.ToProcessorTuningMode (colorTemperatureUiModeActive: true), Is.EqualTo (ProcessorLightTuningMode.White));
		}

	[Test]
	public void ToProcessorTuningMode_ColorActive_ReturnsColor ()
		{
		Assert.That (LightTuningDecisions.ToProcessorTuningMode (colorTemperatureUiModeActive: false), Is.EqualTo (ProcessorLightTuningMode.Color));
		}

	[Test]
	public void RequestsWhiteAndRequestsColor_MutuallyExclusiveOnActualLightTunableSetLevelsInputs ()
		{
		// Brightness-only change (neither tuning parameter present): retains current mode.
		Assert.That (LightTuningDecisions.RequestsWhiteMode (colorTemperature: null), Is.False);
		Assert.That (LightTuningDecisions.RequestsColorMode (hue: null, saturation: null), Is.False);

		// White/CT change.
		Assert.That (LightTuningDecisions.RequestsWhiteMode (colorTemperature: 2700L), Is.True);
		Assert.That (LightTuningDecisions.RequestsColorMode (hue: null, saturation: null), Is.False);

		// Color/HSV change.
		Assert.That (LightTuningDecisions.RequestsWhiteMode (colorTemperature: null), Is.False);
		Assert.That (LightTuningDecisions.RequestsColorMode (hue: 0.25d, saturation: 0.75d), Is.True);
		}
	}