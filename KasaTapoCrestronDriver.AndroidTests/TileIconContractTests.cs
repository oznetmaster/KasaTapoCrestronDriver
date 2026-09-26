// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System.Drawing;
using System.Runtime.Versioning;
using KasaTapoCrestronDriver.AndroidTests;
using NUnit.Framework;

namespace KasaAppEvidenceContracts;

[TestFixture, Category("unit"), SupportedOSPlatform("windows")]
public sealed class TileIconContractTests
{
    [TestCase("icGenericDeviceOn")]
    [TestCase("icGenericDeviceOff")]
    [TestCase("icClimateRegular")]
    [TestCase("icStarOff")]
    public void ReviewedReferenceAcceptsSmallAntialiasingDifference(string name)
    {
        using var expected = TileIconVerification.Reference(name);
        using var actual = new Bitmap(expected);
        for (int x = 0; x < 10; x++)
        {
            var p = actual.GetPixel(x, 20);
            actual.SetPixel(x, 20, Color.FromArgb(Math.Max(0, p.R - 2), Math.Max(0, p.G - 2), Math.Max(0, p.B - 2)));
        }
        Assert.That(TileIconVerification.Compare(actual, expected).Passed, Is.True);
    }

    [TestCase("icGenericDeviceOn", "icGenericDeviceOff")]
    [TestCase("icGenericDeviceOn", "icClimateRegular")]
    [TestCase("icGenericDeviceOn", "icStarOff")]
    [TestCase("icClimateRegular", "icStarOff")]
    public void WrongGlyphOrStateColourFails(string actualName, string expectedName)
    {
        using var actual = TileIconVerification.Reference(actualName);
        using var expected = TileIconVerification.Reference(expectedName);
        Assert.That(TileIconVerification.Compare(actual, expected).Passed, Is.False);
    }

    [Test]
    public void MissingIconAndWrongSizeFail()
    {
        using var expected = TileIconVerification.Reference("icGenericDeviceOn");
        using var blank = new Bitmap(expected.Width, expected.Height);
        using (var graphics = Graphics.FromImage(blank)) graphics.Clear(Color.White);
        using var resized = new Bitmap(expected.Width + 1, expected.Height);
        Assert.That(TileIconVerification.Compare(blank, expected).Passed, Is.False);
        Assert.That(TileIconVerification.Compare(resized, expected).Passed, Is.False);
        Assert.Throws<InvalidDataException>(() => TileIconVerification.Reference("unreviewed"));
    }
}
