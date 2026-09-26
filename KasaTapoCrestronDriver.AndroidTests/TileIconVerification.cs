// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System.Drawing;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace KasaTapoCrestronDriver.AndroidTests;

internal static class TileIconVerification
{
    // Deliberately no automatic baseline creation, scaling or fallback. An app
    // rendering change or an unreviewed icon requires a new visual review.
    internal static void Verify(string directory, string tileName, string expectedIcon)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Icon verification requires Windows.");
        VerifyWindows(directory, tileName, expectedIcon);
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindows(string directory, string tileName, string expectedIcon)
    {
        var document = XDocument.Load(Path.Combine(directory, "hierarchy.xml"));
        var tile = document.Descendants("node").Single(n =>
            (string?)n.Attribute("package") == "com.crestron.phoenix.app" &&
            (string?)n.Attribute("content-desc") == "room_service_" + tileName);
        var icon = tile.Descendants("node").Single(n =>
            (string?)n.Attribute("resource-id") == RoomNavigation.Prefix + "serviceIcon");
        var bounds = Regex.Match((string?)icon.Attribute("bounds") ?? "", @"^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$");
        if (!bounds.Success) throw new InvalidDataException("Missing icon bounds.");
        var values = Enumerable.Range(1, 4).Select(i => int.Parse(bounds.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
        var region = Rectangle.FromLTRB(values[0], values[1], values[2], values[3]);
        using var screenshot = new Bitmap(Path.Combine(directory, "screen.png"));
        using var reference = Reference(expectedIcon);
        if (region.Width != reference.Width || region.Height != reference.Height || region.Left < 0 || region.Top < 0 ||
            region.Right > screenshot.Width || region.Bottom > screenshot.Height)
            throw new InvalidDataException("Icon bounds differ from the reviewed rendering; review the new app geometry.");
        using var actual = screenshot.Clone(region, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var comparison = Compare(actual, reference);
        using var output = new FileStream(Path.Combine(directory, "icon-verification.json"), FileMode.CreateNew);
        JsonSerializer.Serialize(output, new { ExpectedIcon = expectedIcon, Bounds = values,
            ScreenshotSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "screen.png")))),
            comparison.MeanChannelError, comparison.ChangedPixelFraction, comparison.Passed,
            Method = "Reviewed native-size RGB crop; mean channel error <= 2 and fraction of pixels with channel error > 24 <= 0.02." });
        if (!comparison.Passed) throw new InvalidDataException("Room tile icon differs from the reviewed glyph/state colour: " + expectedIcon);
    }

    [SupportedOSPlatform("windows")]
    internal static Bitmap Reference(string name)
    {
        if (name is not ("icGenericDeviceOn" or "icGenericDeviceOff" or "icClimateRegular" or "icStarOff"))
            throw new InvalidDataException("Icon has no reviewed visual reference: " + name);
        using var stream = typeof(TileIconVerification).Assembly.GetManifestResourceStream(
            "KasaTapoCrestronDriver.AndroidTests.IconReferences." + name + ".png")
            ?? throw new InvalidDataException("Reviewed icon resource is missing.");
        using var image = new Bitmap(stream);
        return new Bitmap(image);
    }

    [SupportedOSPlatform("windows")]
    internal static (bool Passed, double MeanChannelError, double ChangedPixelFraction) Compare(Bitmap actual, Bitmap expected)
    {
        if (actual.Size != expected.Size || actual.Width == 0 || actual.Height == 0)
            return (false, double.PositiveInfinity, 1);
        long total = 0, changed = 0;
        for (int y = 0; y < actual.Height; y++)
        for (int x = 0; x < actual.Width; x++)
        {
            var a = actual.GetPixel(x, y); var b = expected.GetPixel(x, y);
            int r = Math.Abs(a.R - b.R), g = Math.Abs(a.G - b.G), blue = Math.Abs(a.B - b.B);
            total += r + g + blue;
            if (Math.Max(r, Math.Max(g, blue)) > 24) changed++;
        }
        double pixels = actual.Width * actual.Height;
        double mean = total / (pixels * 3), fraction = changed / pixels;
        return (mean <= 2 && fraction <= 0.02, mean, fraction);
    }
}
