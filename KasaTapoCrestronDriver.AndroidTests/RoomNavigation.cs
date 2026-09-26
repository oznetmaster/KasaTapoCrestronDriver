// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System.Globalization;
using System.Xml.Linq;
using CrestronHomeNUnit.Android;

namespace KasaTapoCrestronDriver.AndroidTests;

internal static class RoomNavigation
{
    internal const string Prefix = CrestronHomePages.ResourcePrefix;
    internal static XElement Node(AndroidHierarchy h, string resource) => XDocument.Parse(h.MaskedXml).Descendants("node")
        .Single(n => (string?)n.Attribute("package") == "com.crestron.phoenix.app" && (string?)n.Attribute("resource-id") == Prefix + resource);

    internal static async Task Tab(bool rooms, CancellationToken token)
    {
        var session = SensorSession.Current!;
        var h = await session.Device.CaptureAsync(token);
        if (rooms) CrestronHomePages.RequireHome(h, session.Context.Profile.ExpectedHomeText);
        else CrestronHomePages.RequireRooms(h);
        var bar = Node(h, "bottomNavigationView");
        var choices = bar.Elements("node").ToArray();
        if (choices.Length != 2 || choices.Any(n => (string?)n.Attribute("clickable") != "true" ||
            n.Descendants().Count(c => (string?)c.Attribute("resource-id") == Prefix + "itemBottomNavigationIcon") != 1))
            throw new InvalidDataException("Unexpected bottom navigation.");
        static int[] Bounds(XElement n)
        {
            var m = System.Text.RegularExpressions.Regex.Match((string?)n.Attribute("bounds") ?? "", @"^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$");
            if (!m.Success || (string?)n.Attribute("enabled") != "true") throw new InvalidDataException("Unusable tab.");
            return Enumerable.Range(1, 4).Select(i => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
        }
        var b = Bounds(choices[rooms ? 1 : 0]); var outer = Bounds(bar);
        if (b[0] < outer[0] || b[1] < outer[1] || b[2] > outer[2] || b[3] > outer[3] || b[2] <= b[0] || b[3] <= b[1])
            throw new InvalidDataException("Tab is outside the observed navigation bar.");
        var profile = session.Context.Profile;
        await new AdbCommandTransport(profile.AdbExecutable, profile.DeviceSerial, TimeSpan.FromSeconds(25))
            .ExecuteAsync(["shell", "input", "tap", ((b[0]+b[2])/2).ToString(CultureInfo.InvariantCulture),
                ((b[1]+b[3])/2).ToString(CultureInfo.InvariantCulture)], token);
    }

    internal static async Task Open(string room, CancellationToken token)
    {
        var session = SensorSession.Current!;
        await Tab(true, token);
        await session.Device.TapAsync(new(AndroidSelectorKind.Text, room), h =>
        {
            CrestronHomePages.RequireRooms(h);
            var choice = h.RequireUnique(new(AndroidSelectorKind.Text, room));
            var list = h.RequireUnique(CrestronHomePages.Resource("rooms_roomsList"));
            var footer = h.RequireUnique(CrestronHomePages.Resource("bottomNavigationView"));
            if (choice.Top < list.Top || choice.Bottom > Math.Min(list.Bottom, footer.Top))
                throw new InvalidDataException("Selected room requires explicit scrolling.");
        }, token);
    }

    internal static void RequireVisibleRoomControl(AndroidHierarchy h, string room, AndroidSelector selector)
    {
        CrestronHomePages.RequireRoom(h, room);
        var control = h.RequireUnique(selector);
        var top = h.RequireUnique(CrestronHomePages.Resource("room_back"));
        var bottom = h.RequireUnique(CrestronHomePages.Resource("bottomNavigationView"));
        if (control.Top < top.Bottom || control.Bottom > bottom.Top)
            throw new InvalidDataException("Control is outside the visible room viewport.");
    }

    internal static async Task Restore(string room, string? extensionTitle, CancellationToken token)
    {
        var session = SensorSession.Current!;
        var h = await session.Device.CaptureAsync(token);
        bool Has(string id) => XDocument.Parse(h.MaskedXml).Descendants("node").Any(n => (string?)n.Attribute("resource-id") == Prefix + id);
        if (Has("customdevices_toolbarClose") && extensionTitle != null)
            await session.Device.TapAsync(CrestronHomePages.Resource("customdevices_toolbarClose"),
                page => CrestronHomePages.RequireExtensionPage(page, extensionTitle), token);
        h = await session.Device.CaptureAsync(token);
        if (Has("room_back")) await session.Device.TapAsync(CrestronHomePages.Resource("room_back"), page => CrestronHomePages.RequireRoom(page, room), token);
        h = await session.Device.CaptureAsync(token);
        if (Has("fragmentRoomsTitle")) await Tab(false, token);
        await session.CaptureAsync("outlet.home-restored-" + Guid.NewGuid().ToString("N"),
            page => CrestronHomePages.RequireHome(page, session.Context.Profile.ExpectedHomeText), token);
    }
}
