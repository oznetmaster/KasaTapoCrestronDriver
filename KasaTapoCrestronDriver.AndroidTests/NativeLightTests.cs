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
        var original = await Read(ct); await Record("original", original);
        bool entered = false;
        try
        {
            await RoomNavigation.Open(target.Room, ct);
            await session.Device.TapAsync(CrestronHomePages.Resource("twoButtonsTile_title"), h => {
                CrestronHomePages.RequireRoom(h, target.Room);
                Assert.That(h.RequireUnique(CrestronHomePages.Resource("twoButtonsTile_title")).Text, Is.EqualTo("Lights"));
            }, ct);
            entered = true; SensorSession.PhysicalRestorationConfirmed = false;
            var toggle = new AndroidSelector(AndroidSelectorKind.ContentDescription, "toggle_" + target.Name);
            await session.Device.TapAsync(toggle, List, ct);
            await Wait(s => s.On != original.On, "power-changed", ct);
            await session.CaptureAsync("native.power-changed", List, ct);
            await session.Device.TapAsync(toggle, List, ct);
            await Wait(s => s.On == original.On, "power-returned", ct);
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
            await session.CaptureAsync("native.brightness-changed", List, ct);
            await session.Device.TapAsync(new(AndroidSelectorKind.ResourceId, RoomNavigation.Prefix+"lights_lightsDetails_colorPreviewSwatch") { SiblingText=target.Name }, List, ct);
            async Task Adjust(string resource, string display, double minimum, double maximum, double position)
            {
                var page = await session.Device.CaptureAsync(ct); Tuning(page);
                var slider = page.RequireUnique(CrestronHomePages.Resource(resource));
                if (slider.Right <= slider.Left || slider.Bottom <= slider.Top)
                    throw new InvalidDataException("Unusable tuning slider.");
                double current = (Display(page, display)-minimum)/(maximum-minimum);
                if (current < 0 || current > 1) throw new InvalidDataException("Tuning value is outside its range.");
                int radius = (slider.Bottom-slider.Top)/2;
                int start = slider.Left+radius, travel = slider.Right-slider.Left-2*radius;
                await transport.ExecuteAsync(["shell", "input", "swipe",
                    ((int)(start+travel*current)).ToString(CultureInfo.InvariantCulture),
                    ((slider.Top+slider.Bottom)/2).ToString(CultureInfo.InvariantCulture),
                    ((int)(start+travel*position)).ToString(CultureInfo.InvariantCulture),
                    ((slider.Top+slider.Bottom)/2).ToString(CultureInfo.InvariantCulture), "450"], ct);
            }
            int Display(AndroidHierarchy page, string resource) => int.Parse(System.Text.RegularExpressions.Regex.Match(
                page.RequireUnique(CrestronHomePages.Resource(resource)).Text ?? "", @"\d+").Value, CultureInfo.InvariantCulture);
            await Adjust("whiteTuning_colorTempSlider", "whiteTuning_colorTempDisplay", 2500, 6500, .55);
            h = await session.Device.CaptureAsync(ct); Tuning(h);
            int kelvin = Display(h, "whiteTuning_colorTempDisplay");
            Assert.That(kelvin, Is.GreaterThan(2500).And.LessThan(6500));
            await Wait(s => s.On && Math.Abs(s.Temperature-kelvin) <= 50, "white-changed", ct);
            await session.CaptureAsync("native.white", Tuning, ct);
            h = await session.Device.CaptureAsync(ct); Tuning(h);
            var tabs = RoomNavigation.Node(h,"lightLoadTuning_tabLayout").Descendants("node")
                .Where(n => (string?)n.Attribute("class") == "androidx.appcompat.app.ActionBar$Tab").ToArray();
            if(tabs.Length!=2 || tabs.Any(n => (string?)n.Attribute("clickable")!="true")) throw new InvalidDataException("Unexpected tuning tabs.");
            var m=System.Text.RegularExpressions.Regex.Match((string?)tabs[1].Attribute("bounds")??"", @"^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$");
            if(!m.Success) throw new InvalidDataException("Missing tuning tab bounds.");
            int[] b=Enumerable.Range(1,4).Select(i=>int.Parse(m.Groups[i].Value,CultureInfo.InvariantCulture)).ToArray();
            await transport.ExecuteAsync(["shell","input","tap",((b[0]+b[2])/2).ToString(CultureInfo.InvariantCulture),((b[1]+b[3])/2).ToString(CultureInfo.InvariantCulture)], ct);
            await Adjust("fullColorTuning_hueSlider", "fullColorTuning_hueTextDisplay", 0, 360, .35);
            h = await session.Device.CaptureAsync(ct); Tuning(h);
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
                if(Has("lightLoadTuning_title")) await session.Device.TapAsync(CrestronHomePages.Resource("lightLoadTuning_backButton"),Tuning,cleanup.Token);
                h=await session.Device.CaptureAsync(cleanup.Token);
                if(Has("lights_lightsDetails_title")) await session.Device.TapAsync(CrestronHomePages.Resource("lights_lightsDetails_closeButton"),List,cleanup.Token);
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
    }
}
