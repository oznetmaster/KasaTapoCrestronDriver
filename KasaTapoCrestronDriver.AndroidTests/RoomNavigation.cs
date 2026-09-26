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
        await RevealRoom(room, token);
        await session.Device.TapAsync(new(AndroidSelectorKind.Text, room), h =>
        {
            CrestronHomePages.RequireRooms(h);
            var choice = h.RequireUnique(new(AndroidSelectorKind.Text, room));
            var list = h.RequireUnique(CrestronHomePages.Resource("rooms_roomsList"));
            var footer = h.RequireUnique(CrestronHomePages.Resource("bottomNavigationView"));
            int top = Math.Max(list.Top, h.RequireUnique(CrestronHomePages.Resource("fragmentRoomsTitle")).Bottom);
            if (choice.ResourceId != Prefix + "itemRoomTitle" || choice.Left < list.Left || choice.Right > list.Right ||
                choice.Top < top || choice.Bottom > Math.Min(list.Bottom, footer.Top))
                throw new InvalidDataException("Selected room requires explicit scrolling.");
        }, token);
    }

    // The app retains the Rooms list's scroll position between visits.
    // Search the observed viewport rather than assuming the selected room is on screen.
    private static async Task RevealRoom(string room, CancellationToken token)
    {
        var session = SensorSession.Current!;
        AndroidElement Viewport(AndroidHierarchy h)
        {
            CrestronHomePages.RequireRooms(h);
            var list = h.RequireUnique(CrestronHomePages.Resource("rooms_roomsList"));
            int top = Math.Max(list.Top, h.RequireUnique(CrestronHomePages.Resource("fragmentRoomsTitle")).Bottom);
            int bottom = Math.Min(list.Bottom, h.RequireUnique(CrestronHomePages.Resource("bottomNavigationView")).Top);
            if (!list.Enabled || bottom - top < 80) throw new InvalidDataException("No usable room-list viewport.");
            return list with { Top = top, Bottom = bottom };
        }
        string Signature(AndroidHierarchy h) => string.Join("|", Node(h, "rooms_roomsList").Descendants("node")
            .Where(n => (string?)n.Attribute("resource-id") == Prefix + "itemRoomTitle")
            .Select(n => (string?)n.Attribute("text") + (string?)n.Attribute("bounds")));
        foreach (bool down in new[] { true, false })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int step = 0; step <= 12; step++)
            {
                AndroidWorkflowSession.VerifyContext(session.Context);
                var h = await session.Device.CaptureAsync(token);
                var view = Viewport(h);
                int count = XDocument.Parse(h.MaskedXml).Descendants("node").Count(n =>
                    (string?)n.Attribute("package") == "com.crestron.phoenix.app" && (string?)n.Attribute("text") == room);
                if (count > 1) throw new InvalidDataException("Ambiguous room title.");
                var matches = count == 1 ? new[] { h.RequireUnique(new(AndroidSelectorKind.Text, room)) } : Array.Empty<AndroidElement>();
                if (matches.Length > 1 || matches.Length == 1 && (!matches[0].Enabled || matches[0].ResourceId != Prefix + "itemRoomTitle"))
                    throw new InvalidDataException("Ambiguous or unusable room title.");
                if (matches.Length == 1 && matches[0].Top >= view.Top && matches[0].Bottom <= view.Bottom &&
                    matches[0].Left >= view.Left && matches[0].Right <= view.Right) return;
                string signature = Signature(h);
                if (step == 12 || !seen.Add(signature)) break;
                void Guard(AndroidHierarchy current)
                {
                    _ = Viewport(current);
                    if (Signature(current) != signature) throw new InvalidDataException("Room list changed before scrolling.");
                }
                AndroidWorkflowSession.VerifyContext(session.Context);
                Guard(await session.Device.CaptureAsync(token));
                int x = (view.Left + view.Right) / 2;
                int low = view.Bottom - (view.Bottom - view.Top) / 5, high = view.Top + (view.Bottom - view.Top) / 5;
                var profile = session.Context.Profile;
                await new AdbCommandTransport(profile.AdbExecutable, profile.DeviceSerial, TimeSpan.FromSeconds(25))
                    .ExecuteAsync(["shell", "input", "swipe", x.ToString(CultureInfo.InvariantCulture),
                        (down ? low : high).ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture),
                        (down ? high : low).ToString(CultureInfo.InvariantCulture), "450"], token);
            }
        }
        throw new InvalidDataException("Selected room was not visible within the bounded room-list search.");
    }

    internal static AndroidSelector Tile(string name) => new(AndroidSelectorKind.ContentDescription, "room_service_" + name);

    internal static XElement TileNode(AndroidHierarchy hierarchy, string name) => XDocument.Parse(hierarchy.MaskedXml).Descendants("node")
        .Single(n => (string?)n.Attribute("package") == "com.crestron.phoenix.app" && (string?)n.Attribute("content-desc") == "room_service_" + name);

    // Use only the observed scrolling viewport. The generic navigation helper opens
    // the page directly; this fixture also needs an unobscured tile observation first.
    internal static async Task RevealTile(string room, string name, CancellationToken token)
    {
        var session = SensorSession.Current!;
        var profile = session.Context.Profile;
        var transport = new AdbCommandTransport(profile.AdbExecutable, profile.DeviceSerial, TimeSpan.FromSeconds(25));
        foreach (bool down in new[] { true, false })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int step = 0; step < 12; step++)
            {
                AndroidWorkflowSession.VerifyContext(session.Context);
                var h = await session.Device.CaptureAsync(token);
                CrestronHomePages.RequireRoom(h, room);
                var container = h.RequireUnique(CrestronHomePages.Resource("room_scrollView"));
                int top = Math.Max(container.Top, h.RequireUnique(CrestronHomePages.Resource("room_back")).Bottom);
                int bottom = Math.Min(container.Bottom, h.RequireUnique(CrestronHomePages.Resource("bottomNavigationView")).Top);
                if (!container.Enabled || bottom - top < 100 || container.Right - container.Left < 80)
                    throw new InvalidDataException("No usable room viewport.");
                int count = XDocument.Parse(h.MaskedXml).Descendants("node").Count(n =>
                    (string?)n.Attribute("package") == "com.crestron.phoenix.app" &&
                    (string?)n.Attribute("content-desc") == "room_service_" + name);
                if (count > 1) throw new InvalidDataException("Ambiguous Room tile.");
                if (count == 1)
                {
                    var match = h.RequireUnique(Tile(name));
                    if (match.Enabled && match.Top >= top && match.Bottom <= bottom &&
                        match.Left >= container.Left && match.Right <= container.Right) return;
                }
                string signature = string.Join("|", XDocument.Parse(h.MaskedXml).Descendants("node")
                    .Where(n => ((string?)n.Attribute("content-desc"))?.StartsWith("room_service_", StringComparison.Ordinal) == true)
                    .Select(n => (string?)n.Attribute("content-desc") + (string?)n.Attribute("bounds")));
                if (!seen.Add(signature)) break;
                int x = (container.Left + container.Right) / 2;
                int low = bottom - (bottom - top) / 5, high = top + (bottom - top) / 5;
                AndroidWorkflowSession.VerifyContext(session.Context);
                await transport.ExecuteAsync(["shell", "input", "swipe", x.ToString(CultureInfo.InvariantCulture),
                    (down ? low : high).ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture),
                    (down ? high : low).ToString(CultureInfo.InvariantCulture), "450"], token);
            }
        }
        throw new InvalidDataException("Selected tile was not found within bounded observed scrolling.");
    }

    internal static void InspectTile(AndroidHierarchy h, string room, string name, bool ellipsis, string? expectedStatus = null)
    {
        RequireVisibleRoomControl(h, room, Tile(name));
        var tile = TileNode(h, name);
        XElement[] Nodes(string id) => tile.Descendants("node").Where(n => (string?)n.Attribute("resource-id") == Prefix + id).ToArray();
        var titles = Nodes("serviceTitle");
        if (titles.Length != 1 || (string?)titles[0].Attribute("text") != name || Nodes("serviceIcon").Length != 1 ||
            Nodes("serviceDots").Length != (ellipsis ? 1 : 0)) throw new InvalidDataException("Room tile title, icon presence or navigation affordance differs.");
        if (expectedStatus != null)
        {
            var status = Nodes("serviceSubtitle");
            if (status.Length != 1 || !string.Equals((string?)status[0].Attribute("text"), expectedStatus, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Room tile status differs from the observed device state.");
        }
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
