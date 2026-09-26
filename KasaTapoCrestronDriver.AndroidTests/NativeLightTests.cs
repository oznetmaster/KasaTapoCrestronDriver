// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using CrestronHomeNUnit.Android;
using KasaTapoClient;
using NUnit.Framework;

namespace KasaTapoCrestronDriver.AndroidTests;

[TestFixture, NonParallelizable]
public sealed class NativeLightTests
{
    sealed record State(bool On, int Brightness, int Temperature, int Hue, int Saturation);

    [Test]
    public async Task IndividualNativeLightPowerBrightnessWhiteAndColor()
    {
        var started = DateTimeOffset.UtcNow;
        var session = SensorSession.Current!;
        var settings = SensorSession.Settings!;
        if (settings.Light == null) Assert.Ignore("No explicitly authorized native light binding supplied.");
        var target = settings.Light!;
        if (!target.ControlsAuthorized || target.DeviceId <= 0 || target.WrapperId <= 0 ||
            string.IsNullOrWhiteSpace(target.DiscoveryId) || string.IsNullOrWhiteSpace(target.AuthenticatedId) ||
            settings.DeviceCredentialsFile == null || !Path.IsPathFullyQualified(settings.DeviceCredentialsFile))
            throw new InvalidDataException("Explicit private light authorization and credentials required.");
        if (!SensorSession.PhysicalRestorationConfirmed) throw new InvalidOperationException("Earlier restoration is unresolved.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        string dir = Path.Combine(session.Context.EvidenceDirectory, "native-light"); Directory.CreateDirectory(dir);
        int sequence = 0;
        async Task Record(string phase, State state) => await File.WriteAllTextAsync(Path.Combine(dir, (++sequence).ToString("D3")+"-"+phase+".json"),
            JsonSerializer.Serialize(new { phase, target.DeviceId, target.DiscoveryId, state, Utc = DateTimeOffset.UtcNow }));
        async Task Identity(CancellationToken token)
        {
            var api = SensorSession.Api!;
            var load = await api.GetDeviceAsync(target.DeviceId, token) ?? throw new InvalidDataException("Load missing.");
            var wrapper = await api.GetDeviceAsync(target.WrapperId, token) ?? throw new InvalidDataException("Wrapper missing.");
            var root = await api.GetDeviceAsync(session.Context.InstalledDriverId, token) ?? throw new InvalidDataException("Platform missing.");
            if (load.ParentDeviceId != target.WrapperId || wrapper.ParentDeviceId != session.Context.InstalledDriverId || load.Name != target.Name ||
                load.Model != target.Model || load.LocationId != target.LocationId || wrapper.Model != target.Model ||
                Version.Parse(root.PropertyValues["cp.driverInformation:version"].GetString()!) != Version.Parse(session.Context.DriverVersion))
                throw new InvalidDataException("Native load identity changed.");
        }
        async Task<KasaDevice> Connect(CancellationToken token)
        {
            await Identity(token);
            try {
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(settings.DeviceCredentialsFile!, token));
                var auth = json.RootElement.GetProperty("credentials");
                var found = (await Discover.DiscoverAsync(TimeSpan.FromSeconds(3), cancellationToken: token))
                    .Where(d => string.Equals(d.DeviceId, target.DiscoveryId, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (found.Select(d => d.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1) throw new InvalidDataException();
                var selected = found.OrderByDescending(d => d.TpapPreferred == true || d.TpapMetadata != null).First();
                var device = await Discover.ConnectAsync(Discover.CreateConfiguration(selected,
                    new DeviceCredentials(auth.GetProperty("userName").GetString(), auth.GetProperty("password").GetString()), TimeSpan.FromSeconds(15)), token);
                if (!string.Equals(device.SystemInfo?.DeviceId, target.AuthenticatedId, StringComparison.OrdinalIgnoreCase)) { device.Dispose(); throw new InvalidDataException(); }
                return device;
            } catch (OperationCanceledException) { throw; }
            catch (Exception error) { throw new InvalidDataException("Independent light connection failed (" + error.GetType().Name + "); inspect private bindings and connectivity."); }
        }
        async Task<State> Read(CancellationToken token)
        {
            using var device = await Connect(token);
            var l = device.LightState ?? throw new InvalidDataException("Missing native light state.");
            return new(l.IsOn ?? throw new InvalidDataException(), l.Brightness ?? throw new InvalidDataException(),
                l.ColorTemperature ?? throw new InvalidDataException(), l.Hue ?? throw new InvalidDataException(), l.Saturation ?? throw new InvalidDataException());
        }
        async Task<State> Wait(Func<State,bool> matches, string phase, CancellationToken token)
        {
            using var step = CancellationTokenSource.CreateLinkedTokenSource(token);
            step.CancelAfter(TimeSpan.FromSeconds(75));
            token = step.Token;
            State? last = null; int count = 0;
            while (count < 2) {
                var current = await Read(token); await Record(phase, current);
                count = matches(current) && current == last ? count+1 : matches(current) ? 1 : 0; last = current;
                if (count < 2) await Task.Delay(700, token);
            }
            return last!;
        }
        void List(AndroidHierarchy h)
        {
            h.RequireAbsent(CrestronHomePages.Resource("lightLoadTuning_title"));
            Assert.That(h.RequireUnique(CrestronHomePages.Resource("lights_lightsDetails_title")).Text, Is.EqualTo("Lights"));
            Assert.That(h.RequireUnique(new(AndroidSelectorKind.Text, target.Name)).ResourceId,
                Is.EqualTo(RoomNavigation.Prefix+"lights_lightsDetails_lightLoadTitle"));
        }
        void Tuning(AndroidHierarchy h) => Assert.That(h.RequireUnique(CrestronHomePages.Resource("lightLoadTuning_title")).Text, Is.EqualTo(target.Name));
        void ListState(AndroidHierarchy page, State state)
        {
            List(page);
            var root = XDocument.Parse(page.MaskedXml).Descendants("node").Single(n =>
                (string?)n.Attribute("resource-id") == RoomNavigation.Prefix + "lights_lightsDetails_lightLoadRoot" &&
                n.Descendants("node").Any(c => (string?)c.Attribute("resource-id") == RoomNavigation.Prefix + "lights_lightsDetails_lightLoadTitle" &&
                    (string?)c.Attribute("text") == target.Name));
            var toggleNode = root.Descendants("node").Single(n => (string?)n.Attribute("content-desc") == "toggle_" + target.Name);
            Assert.That((string?)toggleNode.Attribute("checked"), Is.EqualTo(state.On ? "true" : "false"));
            if (state.On)
            {
                string status = (string?)root.Descendants("node").Single(n =>
                    (string?)n.Attribute("resource-id") == RoomNavigation.Prefix + "lights_lightsDetails_statusText").Attribute("text") ?? "";
                var percent = System.Text.RegularExpressions.Regex.Match(status, @"(\d+)%");
                Assert.That(percent.Success, Is.True, "Displayed native brightness");
                Assert.That(int.Parse(percent.Groups[1].Value, CultureInfo.InvariantCulture), Is.EqualTo(state.Brightness).Within(2));
            }
        }
        var original = await Read(ct); await Record("original", original);
        var originalAt = DateTimeOffset.UtcNow;
        var actionAt = originalAt;
        bool entered = false;
        try
        {
            await RoomNavigation.Open(target.Room, ct);
            await session.Device.TapAsync(CrestronHomePages.Resource("twoButtonsTile_title"), h => {
                CrestronHomePages.RequireRoom(h, target.Room);
                Assert.That(h.RequireUnique(CrestronHomePages.Resource("twoButtonsTile_title")).Text, Is.EqualTo("Lights"));
            }, ct);
            entered = true; SensorSession.PhysicalRestorationConfirmed = false;
            actionAt = DateTimeOffset.UtcNow;
            var toggle = new AndroidSelector(AndroidSelectorKind.ContentDescription, "toggle_" + target.Name);
            await session.Device.TapAsync(toggle, List, ct);
            var toggled = await Wait(s => s.On != original.On, "power-changed", ct);
            await session.CaptureAsync("native.power-changed", h => ListState(h, toggled), ct);
            await session.Device.TapAsync(toggle, List, ct);
            var returned = await Wait(s => s.On == original.On, "power-returned", ct);
            await session.CaptureAsync("native.power-returned", h => ListState(h, returned), ct);
            // Drag this load's observed thumb; its native ghost track does not handle taps.
            var h = await session.Device.CaptureAsync(ct); List(h);
            var track = h.RequireUnique(new(AndroidSelectorKind.ContentDescription, "ghost_track_"+target.Name));
            var thumb = h.RequireUnique(new(AndroidSelectorKind.ContentDescription, "ghost_slider_"+target.Name));
            if (thumb.Left < track.Left || thumb.Right > track.Right || thumb.Top < track.Top || thumb.Bottom > track.Bottom)
                throw new InvalidDataException("Light thumb is outside its track.");
            double fraction = original.Brightness is >=35 and <=65 ? 0.25 : 0.50;
            var profile = session.Context.Profile;
            var transport = new AdbCommandTransport(profile.AdbExecutable, profile.DeviceSerial, TimeSpan.FromSeconds(25));
            int halfThumb=(thumb.Right-thumb.Left)/2;
            await transport.ExecuteAsync(["shell","input","swipe", ((thumb.Left+thumb.Right)/2).ToString(CultureInfo.InvariantCulture),
                ((thumb.Top+thumb.Bottom)/2).ToString(CultureInfo.InvariantCulture),
                ((int)(track.Left+halfThumb+(track.Right-track.Left-2*halfThumb)*fraction)).ToString(CultureInfo.InvariantCulture),
                ((track.Top+track.Bottom)/2).ToString(CultureInfo.InvariantCulture),"400"], ct);
            var changed = await Wait(s => s.On && s.Brightness > 0 && s.Brightness < 100 && Math.Abs(s.Brightness-original.Brightness)>5, "brightness-changed", ct);
            var load = await SensorSession.Api!.GetDeviceAsync(target.DeviceId, ct);
            Assert.That(load!.PropertyValues["lightDimmer:level"].GetDouble(), Is.EqualTo(changed.Brightness/100.0).Within(.02));
            await session.CaptureAsync("native.brightness-changed", page => ListState(page, changed), ct);
            async Task DragBrightness(string phase, params double[] positions)
            {
                AndroidWorkflowSession.VerifyContext(session.Context);
                var page = await session.Device.CaptureAsync(ct); List(page);
                var t = page.RequireUnique(new(AndroidSelectorKind.ContentDescription, "ghost_track_" + target.Name));
                var knob = page.RequireUnique(new(AndroidSelectorKind.ContentDescription, "ghost_slider_" + target.Name));
                int radius = (knob.Right - knob.Left) / 2;
                if (!t.Enabled || !knob.Enabled || knob.Left < t.Left || knob.Right > t.Right || knob.Top < t.Top || knob.Bottom > t.Bottom ||
                    t.Right - t.Left <= 2 * radius || positions.Any(p => p <= 0 || p >= 1))
                    throw new InvalidDataException("Invalid selected brightness control geometry.");
                int x = (knob.Left + knob.Right) / 2, y = (knob.Top + knob.Bottom) / 2;
                var inputStarted = DateTimeOffset.UtcNow;
                foreach (double position in positions)
                {
                    int end = (int)(t.Left + radius + (t.Right - t.Left - 2 * radius) * position);
                    AndroidWorkflowSession.VerifyContext(session.Context);
                    await transport.ExecuteAsync(["shell", "input", "swipe", x.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture),
                        end.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture), "240"], ct);
                    x = end;
                }
                await File.WriteAllTextAsync(Path.Combine(dir, (++sequence).ToString("D3") + "-" + phase + "-inputs.json"),
                    JsonSerializer.Serialize(new { phase, positions, inputStarted, inputFinished = DateTimeOffset.UtcNow }), ct);
            }
            async Task MatchBrightness(State state, string check)
            {
                var d = await SensorSession.Api!.GetDeviceAsync(target.DeviceId, ct);
                Assert.That(d!.PropertyValues["lightDimmer:level"].GetDouble(), Is.EqualTo(state.Brightness / 100.0).Within(.02));
                await session.CaptureAsync(check, page => ListState(page, state), ct);
            }
            await DragBrightness("brightness-up", .80);
            var higher = await Wait(s => s.On && s.Brightness > changed.Brightness + 10, "brightness-up", ct);
            await MatchBrightness(higher, "native.brightness-up");
            await DragBrightness("brightness-down", .20);
            var lower = await Wait(s => s.On && s.Brightness < higher.Brightness - 10, "brightness-down", ct);
            await MatchBrightness(lower, "native.brightness-down");
            // Consecutive gestures use this load's observed track, with no
            // hierarchy captures or device acknowledgements between gestures.
            await DragBrightness("brightness-burst", .40, .75, .60);
            var burst = await Wait(s => s.On && Math.Abs(s.Brightness - 60) <= 8, "brightness-burst", ct);
            await MatchBrightness(burst, "native.brightness-burst");
            await session.Device.TapAsync(new(AndroidSelectorKind.ResourceId, RoomNavigation.Prefix+"lights_lightsDetails_colorPreviewSwatch") { SiblingText=target.Name }, List, ct);
            async Task SelectTuningTab(int index)
            {
                var page = await session.Device.CaptureAsync(ct); Tuning(page);
                var tabBar = page.RequireUnique(CrestronHomePages.Resource("lightLoadTuning_tabLayout"));
                var choices = RoomNavigation.Node(page, "lightLoadTuning_tabLayout").Descendants("node")
                    .Where(n => (string?)n.Attribute("class") == "androidx.appcompat.app.ActionBar$Tab").ToArray();
                if (choices.Length != 2 || choices.Any(n => (string?)n.Attribute("clickable") != "true" || (string?)n.Attribute("enabled") != "true"))
                    throw new InvalidDataException("Unexpected tuning selectors.");
                var bounds = System.Text.RegularExpressions.Regex.Match((string?)choices[index].Attribute("bounds") ?? "", @"^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$");
                if (!bounds.Success) throw new InvalidDataException("Missing tuning selector bounds.");
                int[] points = Enumerable.Range(1,4).Select(i => int.Parse(bounds.Groups[i].Value,CultureInfo.InvariantCulture)).ToArray();
                if (points[0] < tabBar.Left || points[1] < tabBar.Top || points[2] > tabBar.Right || points[3] > tabBar.Bottom || points[2] <= points[0] || points[3] <= points[1])
                    throw new InvalidDataException("Tuning selector is outside its tab bar.");
                AndroidWorkflowSession.VerifyContext(session.Context);
                await transport.ExecuteAsync(["shell", "input", "tap", ((points[0]+points[2])/2).ToString(CultureInfo.InvariantCulture),
                    ((points[1]+points[3])/2).ToString(CultureInfo.InvariantCulture)], ct);
                await session.CaptureAsync(index == 0 ? "native.white-selected" : "native.color-selected", captured => {
                    Tuning(captured);
                    var tabs = RoomNavigation.Node(captured,"lightLoadTuning_tabLayout").Descendants("node")
                        .Where(n => (string?)n.Attribute("class") == "androidx.appcompat.app.ActionBar$Tab").ToArray();
                    Assert.That(tabs,Has.Length.EqualTo(2));
                    Assert.That((string?)tabs[index].Attribute("selected"),Is.EqualTo("true"));
                    Assert.That((string?)tabs[1-index].Attribute("selected"),Is.EqualTo("false"));
                },ct);
            }
            await SelectTuningTab(0);
            async Task Adjust(string resource, string display, double minimum, double maximum, double position)
            {
                AndroidHierarchy? retained = null;
                await session.CaptureAsync("native.before-"+resource,captured => { Tuning(captured); retained=captured; },ct);
                var page = retained!;
                // Feedback can reflow this page. Require two observed snapshots
                // to agree before deriving input coordinates; never retry input.
                bool settled = false;
                for(int attempt=0;attempt<3;attempt++) {
                    var next=await session.Device.CaptureAsync(ct); Tuning(next);
                    var previousControl=page.RequireUnique(CrestronHomePages.Resource(resource));
                    var nextControl=next.RequireUnique(CrestronHomePages.Resource(resource));
                    settled=previousControl==nextControl && Display(page,display)==Display(next,display);
                    page=next;
                    if(settled)break;
                }
                if(!settled)throw new InvalidDataException("Tuning control did not settle before input.");
                var slider = page.RequireUnique(CrestronHomePages.Resource(resource));
                if (slider.Right <= slider.Left || slider.Bottom <= slider.Top)
                    throw new InvalidDataException("Unusable tuning slider.");
                double current = (Display(page, display)-minimum)/(maximum-minimum);
                if (current < 0 || current > 1) throw new InvalidDataException("Tuning value is outside its range.");
                // The app remembers tuning values even when the bulb is in the
                // other color mode. A near-zero drag may emit no command.
                if (Math.Abs(current-position) < .20) position = current < .50 ? .75 : .25;
                int radius = (slider.Bottom-slider.Top)/2;
                int start = slider.Left+radius, travel = slider.Right-slider.Left-2*radius;
                await File.WriteAllTextAsync(Path.Combine(dir,(++sequence).ToString("D3")+"-tuning-intent.json"),
                    JsonSerializer.Serialize(new { resource, display, minimum, maximum, current, position, slider, Utc=DateTimeOffset.UtcNow }),ct);
                AndroidWorkflowSession.VerifyContext(session.Context);
                await transport.ExecuteAsync(["shell", "input", "swipe",
                    ((int)(start+travel*current)).ToString(CultureInfo.InvariantCulture),
                    ((slider.Top+slider.Bottom)/2).ToString(CultureInfo.InvariantCulture),
                    ((int)(start+travel*position)).ToString(CultureInfo.InvariantCulture),
                    ((slider.Top+slider.Bottom)/2).ToString(CultureInfo.InvariantCulture), "450"], ct);
                AndroidHierarchy? adjusted = null;
                await session.CaptureAsync("native.after-"+resource,captured => {Tuning(captured);adjusted=captured;},ct);
                Assert.That(Math.Abs((Display(adjusted!,display)-minimum)/(maximum-minimum)-current),
                    Is.GreaterThan(.10), "The selected tuning slider must visibly move before waiting for hardware feedback.");
            }
            int Display(AndroidHierarchy page, string resource) => int.Parse(System.Text.RegularExpressions.Regex.Match(
                page.RequireUnique(CrestronHomePages.Resource(resource)).Text ?? "", @"\d+").Value, CultureInfo.InvariantCulture);
            await Adjust("whiteTuning_colorTempSlider", "whiteTuning_colorTempDisplay", 2500, 6500, .55);
            await session.CaptureAsync("native.white-requested",Tuning,ct);
            h = await session.Device.CaptureAsync(ct); Tuning(h);
            Assert.That(h.RequireUnique(CrestronHomePages.Resource("whiteTuning_title")).Text, Is.EqualTo("Tunable white"));
            int kelvin = Display(h, "whiteTuning_colorTempDisplay");
            Assert.That(kelvin, Is.GreaterThan(2500).And.LessThan(6500));
            await Wait(s => s.On && Math.Abs(s.Temperature-kelvin) <= 50, "white-changed", ct);
            await session.CaptureAsync("native.white", Tuning, ct);
            await SelectTuningTab(1);
            await Adjust("fullColorTuning_hueSlider", "fullColorTuning_hueTextDisplay", 0, 360, .35);
            h = await session.Device.CaptureAsync(ct); Tuning(h);
            Assert.That(h.RequireUnique(CrestronHomePages.Resource("fullColorTuning_title")).Text, Is.EqualTo("Full color tuning"));
            int hue = Display(h, "fullColorTuning_hueTextDisplay");
            await Wait(s => s.On && s.Temperature == 0 && Math.Abs(s.Hue-hue) <= 2, "hue-changed", ct);
            await Adjust("fullColorTuning_saturationSlider", "fullColorTuning_saturationTextDisplay", 0, 100, .55);
            h = await session.Device.CaptureAsync(ct); Tuning(h);
            int saturation = Display(h, "fullColorTuning_saturationTextDisplay");
            Assert.That(saturation, Is.GreaterThan(0).And.LessThan(100));
            await Wait(s => s.On && s.Temperature == 0 && Math.Abs(s.Hue-hue) <= 2 && Math.Abs(s.Saturation-saturation) <= 2, "saturation-changed", ct);
            await session.CaptureAsync("native.palette", Tuning, ct);
        }
        finally
        {
            using var cleanup=new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try {
                var h=await session.Device.CaptureAsync(cleanup.Token);
                bool Has(string id)=>XDocument.Parse(h.MaskedXml).Descendants("node").Any(n=>(string?)n.Attribute("resource-id")==RoomNavigation.Prefix+id);
                if(Has("lightLoadTuning_title")) {
                    await session.Device.TapAsync(CrestronHomePages.Resource("lightLoadTuning_backButton"),Tuning,cleanup.Token);
                    await session.CaptureAsync("native.tuning-returned",List,cleanup.Token);
                }
                h=await session.Device.CaptureAsync(cleanup.Token);
                if(Has("lights_lightsDetails_title")) {
                    await session.Device.TapAsync(CrestronHomePages.Resource("lights_lightsDetails_closeButton"),List,cleanup.Token);
                    await session.CaptureAsync("native.room-returned",page => CrestronHomePages.RequireRoom(page,target.Room),cleanup.Token);
                }
            }
            finally {
              try { if(entered) {
                    // Direct device calls here are recovery only, never evidence of driver control success.
                    await Wait(_=>true,"settled-before-restore",cleanup.Token);
                    using var device=await Connect(cleanup.Token);
                    await Record("restore-intent",original);
                    await device.SetHsvAsync(original.Hue,original.Saturation,original.Brightness,cleanup.Token);
                    if(original.Temperature>0) await device.SetColorTemperatureAsync(original.Temperature,cleanup.Token);
                    if(original.On) await device.TurnLightOnAsync(cleanup.Token); else await device.TurnLightOffAsync(cleanup.Token);
                    await Wait(s=>s==original,"restored",cleanup.Token);
                    SensorSession.PhysicalRestorationConfirmed=true;
                } }
              finally { await RoomNavigation.Restore(target.Room,null,cleanup.Token); }
            }
        }
        AppEvidence.Write("native-light",
            "Verified native load/wrapper/platform identity; operated individual power, brightness, white-temperature, hue and saturation controls; matched physical reads and displayed tuning values; restored original power and colour state and returned Home. Direct physical commands were restoration only. No initial tuning-mode, outage or quantified response-time assertion.",
            started, originalAt, actionAt, Directory.GetFiles(dir, "*-original.json").Single(),
            Directory.GetFiles(dir, "*-restored.json").Order(StringComparer.Ordinal).Last());
        foreach(var assertion in new[] {
            ("slider", "Verified individual native load name, toggles, upward/downward brightness and three consecutive slider gestures, with final physical/API/display agreement and original-state restoration. No fixed gesture rate or response deadline asserted."),
            ("buttons", "Operated the selected native load toggle in both directions and verified physical power and displayed switch state. Room-wide actions were not used; original state restored."),
            ("selectors", "Operated the selected light's white and colour tuning controls; verified page labels and physical colour-temperature/hue/saturation against displayed values; restored original state. Tab glyph semantics are not asserted."),
            ("subpages", "Opened and exercised the selected light's white and colour tuning subpages, returned to its light list and Room, restored original physical state and returned Home.") })
            AppEvidence.Write("native-light." + assertion.Item1, assertion.Item2, started, originalAt, actionAt,
                Directory.GetFiles(dir, "*-original.json").Single(), Directory.GetFiles(dir, "*-restored.json").Order(StringComparer.Ordinal).Last());
    }
}
