// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Crestron.DeviceDrivers.SDK;
using KasaTapoClient;
using KasaTapoClient.Internal;
using KasaTapoCrestronDriver;

namespace KasaTapoCrestronDriver.Tests;

public sealed partial class LiveDriverEntityTests
{
    [Test, Category("MixedOutletLoad")]
    public async Task TwoRealOutletsAndEighteenTcpSimulatorsKeepCadenceAndCommandFeedback()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        CancellationToken token = timeout.Token;
        using var plug = await Connect("plug");
        using var strip = await Connect("strip");
        string childId = Target("strip").ChildDeviceId ?? throw new InvalidDataException("Select a strip socket.");
        Assert.That(plug.SystemInfo?.Alias?.IndexOf("demo", StringComparison.OrdinalIgnoreCase), Is.GreaterThanOrEqualTo(0));
        Assert.That(strip.SystemInfo?.Alias?.IndexOf("demo", StringComparison.OrdinalIgnoreCase) >= 0
            || strip.GetChild(childId)?.Alias?.IndexOf("demo", StringComparison.OrdinalIgnoreCase) >= 0,
            Is.True, "Only the named demo strip/socket is permitted.");
        Assert.That(plug.IsOn, Is.Not.Null);
        Assert.That(strip.GetChild(childId)?.IsOn, Is.Not.Null);
        bool plugWasOn = plug.IsOn!.Value, stripWasOn = strip.GetChild(childId)!.IsOn!.Value;
        Assert.That(plug.Energy.IsAvailable, Is.True, "The real standalone plug must report energy.");

        var observations = new ConcurrentQueue<(int Slot, string Kind, long Milliseconds)>();
        var schedule = new OutletPollCoordinator((slot, kind, ms) => observations.Enqueue((slot, kind, ms)));
        using var logger = new DriverLogger("mixed-outlet-load");
        var resources = new DriverImplementationResources { Logger = logger, InitLogger = logger.GetComponentLogger("test", "mixed") };
        var settings = Settings();
        using var stripPoller = new ManagedParentDevicePoller("mixed-strip", strip.Configuration, settings, null,
            "mixed", true, (_, _) => Task.FromResult(strip), (d, ct) => d.UpdateAsync(ct),
            (delay, ct) => Task.Delay(delay, ct), schedule);
        var entities = new List<KasaOutletEntity>();
        var simulators = new List<TcpOutlet>();
        var devices = new List<KasaDevice>();
        KasaOutletEntity Add(KasaDevice device, string id, string? socket = null)
        {
            var info = device.SystemInfo!;
            var descriptor = new ManagedLightDescriptor(id, device.Configuration.Host, device.DeviceType,
                info.Alias ?? id, info.Model ?? "", info.DeviceId ?? "", ManagedLightKind.OnOff,
                childId: socket, childKind: ManagedChildKind.Outlet);
            var entity = new KasaOutletEntity(id, descriptor, device.Configuration, null, settings, resources,
                null!, "mixed", DriverTestPaths.DataDirectory, socket is null ? null : stripPoller, schedule);
            entities.Add(entity);
            Assert.That(entity.TryAttachConnectedDevice(device, "mixed"), Is.True);
            return entity;
        }
        TimeSpan? CpuTime()
        {
            try { using var process = Process.GetCurrentProcess(); return process.TotalProcessorTime; }
            catch (Exception) { return null; }
        }
        try
        {
            var realPlug = Add(plug, "mixed-real-standalone");
            var realStrip = Add(strip, "mixed-real-strip", childId);
            for (int i=0; i<18; i++)
            {
                var simulator = new TcpOutlet(i);
                simulators.Add(simulator);
                var device = new KasaDevice(new DeviceConfiguration("127.0.0.1", simulator.Port, connectionOptions: new DeviceConnectionOptions(DeviceTransportKind.LegacyXor)));
                devices.Add(device);
                await device.UpdateAsync(token);
                Add(device, $"mixed-simulator-{i}");
            }
            await Task.WhenAll(entities.Select(e => e.SetConfiguredAsync(true,"mixed",token)));
            foreach (var e in entities)
            {
                e.NotifyChildRunning("mixed");
                e.NotifyHostStateRead();
                Assert.That(e.OnlineIndicatorIsOnline, Is.True);
            }
            var external = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var commanded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            entities[2].OutletTurnedOn += (_, _) => external.TrySetResult(true);
            entities[3].OutletTurnedOn += (_, _) => commanded.TrySetResult(true);
            var cpuBefore = CpuTime();
            long start = schedule.ElapsedMilliseconds;
            var before = simulators.Select(s => s.Counters).ToArray();
            var old = settings.Snapshot();
            settings.Update("", "", TimeSpan.FromSeconds(5), true, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), false, "", "", "");
            var current = settings.Snapshot();
            foreach(var e in entities) e.ApplyRuntimeConfiguration(old,current);
            stripPoller.ApplyRuntimeConfiguration(old,current);
            await Task.Delay(22000, token);
            var externalDelay = Stopwatch.StartNew();
            simulators[0].TurnOnExternally();
            await external.Task.WaitForTestAsync(TimeSpan.FromSeconds(12));
            externalDelay.Stop();
            long remaining = 132000 - (schedule.ElapsedMilliseconds - start);
            if (remaining > 0) await Task.Delay((int)remaining, token);
            long end = schedule.ElapsedMilliseconds;
            var cpuAfter = CpuTime();
            var steady = observations.Where(x=>x.Milliseconds>=start && x.Milliseconds<end).ToArray();
            var slots = steady.Where(x=>x.Kind.EndsWith("-success",StringComparison.Ordinal)).GroupBy(x=>x.Slot).ToArray();
            Assert.That(slots.Length, Is.EqualTo(20));
            foreach(var slot in slots)
                Assert.That(slot.Count(), Is.InRange(12,14), $"Successful state refreshes for slot {slot.Key}");
            var gaps = slots.SelectMany(g =>
            {
                var times=g.Select(x=>x.Milliseconds).OrderBy(x=>x).ToArray();
                return times.Zip(times.Skip(1),(a,b)=>b-a);
            }).ToArray();
            Assert.That(gaps.Max(), Is.LessThan(12500), "No device missed a healthy ten-second poll.");
            var energy = steady.Where(x=>x.Kind=="energy-success").GroupBy(x=>x.Slot).ToArray();
            Assert.That(energy.Length, Is.EqualTo(19));
            Assert.That(energy.All(g=>g.Count()==2), Is.True, "Two energy reads per standalone plug, no per-socket energy reads.");
            var energyGaps = energy.Select(g => g.Last().Milliseconds-g.First().Milliseconds).ToArray();
            Assert.That(energyGaps.All(ms=>ms>=57500&&ms<=62500), Is.True);
            var starts=steady.Where(x=>x.Kind.EndsWith("-start",StringComparison.Ordinal)).OrderBy(x=>x.Milliseconds).ToArray();
            int peak100=starts.Max(x=>starts.Count(y=>y.Milliseconds>=x.Milliseconds&&y.Milliseconds<x.Milliseconds+100));
            Assert.That(peak100, Is.LessThanOrEqualTo(3));
            var after = simulators.Select(s=>s.Counters).ToArray();
            for(int i=0;i<18;i++)
            {
                Assert.That(after[i].Modules-before[i].Modules, Is.EqualTo(2), $"Full module batches for simulated outlet {i}");
                Assert.That(after[i].Reads-before[i].Reads, Is.InRange(12,14));
                Assert.That(after[i].Writes-before[i].Writes, Is.Zero);
            }
            Assert.That(entities.All(e=>e.OnlineIndicatorIsOnline), Is.True);
            Assert.That(realPlug.OutletIsOn, Is.EqualTo(plugWasOn));
            Assert.That(realStrip.OutletIsOn, Is.EqualTo(stripWasOn));

            // A command must confirm even while all background permits are occupied.
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var full = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int blocked=0;
            var holders = Enumerable.Range(0,3).Select(_=>schedule.RunAsync(async () =>
            {
                if(Interlocked.Increment(ref blocked)==3) full.TrySetResult(true);
                await release.Task;
            },token)).ToArray();
            bool fourth = false;
            long commandMs = 0;
            try
            {
                await full.Task.WaitForTestAsync(TimeSpan.FromSeconds(5));
                var fourthPoll = schedule.RunAsync(() => { Volatile.Write(ref fourth,true); return Task.CompletedTask; }, token);
                var watch=Stopwatch.StartNew();
                entities[3].OutletOn();
                await commanded.Task.WaitForTestAsync(TimeSpan.FromSeconds(2));
                commandMs = watch.ElapsedMilliseconds;
                Assert.That(simulators[1].IsOn && entities[3].OutletIsOn, Is.True);
                Assert.That(Volatile.Read(ref fourth), Is.False);
                release.TrySetResult(true);
                await fourthPoll;
            }
            finally { release.TrySetResult(true); await Task.WhenAll(holders); }
            Assert.That(schedule.PeakConcurrency, Is.EqualTo(3));
            TestContext.Out.WriteLine($"MIXED OUTLETS real=2 simulatedTcp=18 windowMs={end-start} polls={slots.Sum(g=>g.Count())} maxGapMs={gaps.Max()} energyPolls={energy.Sum(g=>g.Count())} minEnergyGapMs={energyGaps.Min()} maxEnergyGapMs={energyGaps.Max()} peakStarts100ms={peak100} maxConcurrent={schedule.PeakConcurrency} externalFeedbackMs={externalDelay.ElapsedMilliseconds} saturatedCommandFeedbackMs={commandMs} cpuMs={(cpuAfter-cpuBefore)?.TotalMilliseconds} physicalCommands=0");
        }
        finally
        {
            // Mark the simulated servers as shutting down before disposal of a
            // connected device can close an in-flight TCP frame. Unexpected EOF
            // during the observation window must still fail the server task.
            foreach (var simulator in simulators) simulator.Stop();
            foreach (var e in entities) e.Stop();
            foreach (var e in entities) e.Dispose();
            foreach (var device in devices) device.Dispose();
            foreach (var simulator in simulators) simulator.Dispose();
        }
    }

    // Real TCP round trips and legacy XOR framing. No device discovery/broadcasts.
    private sealed class TcpOutlet : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        private readonly int _id;
        private int _reads, _modules, _writes, _isOn;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public bool IsOn => Volatile.Read(ref _isOn)!=0;
        public (int Reads,int Modules,int Writes) Counters => (Volatile.Read(ref _reads),Volatile.Read(ref _modules),Volatile.Read(ref _writes));
        public void TurnOnExternally() => Volatile.Write(ref _isOn,1);
        public TcpOutlet(int id) { _id=id; _listener.Start(); _server=ServeAsync(); }
        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var client=await _listener.AcceptTcpClientAsync();
                    using var stream=client.GetStream();
                    var header=await ReadAsync(stream,4,_stop.Token);
                    int size=(header[0]<<24)|(header[1]<<16)|(header[2]<<8)|header[3];
                    if(size<1||size>8192)throw new IOException("Invalid simulation frame.");
                    var request=JsonNode.Parse(KasaCipher.Decrypt(await ReadAsync(stream,size,_stop.Token)))!;
                    string response;
                    if(request["system"]?["set_relay_state"] is JsonObject relay)
                    {
                        Interlocked.Increment(ref _writes);
                        Volatile.Write(ref _isOn,relay["state"]!.GetValue<int>());
                        response="{\"system\":{\"set_relay_state\":{\"err_code\":0}}}";
                    }
                    else if(request["system"]?["get_sysinfo"] is not null)
                    {
                        Interlocked.Increment(ref _reads);
                        response="{\"system\":{\"get_sysinfo\":{\"err_code\":0,\"alias\":\"Simulation "+_id+"\",\"model\":\"KP115(UK)\",\"deviceId\":\"sim-"+_id+"\",\"relay_state\":"+(IsOn?1:0)+"}}}";
                    }
                    else if(request["emeter"] is not null)
                    {
                        int n=Interlocked.Increment(ref _modules);
                        response="{\"emeter\":{\"get_realtime\":{\"err_code\":0,\"voltage_mv\":"+(240000+n*200)+",\"current_ma\":500,\"power_mw\":120000,\"total_wh\":5}}}";
                    }
                    else throw new IOException("Unexpected simulation command.");
                    await Task.Delay(45,_stop.Token);
                    byte[] data=KasaCipher.EncryptWithHeader(response);
                    await stream.WriteAsync(data,0,data.Length,_stop.Token);
                }
                catch(Exception) when (_stop.IsCancellationRequested) { return; }
                catch(IOException) { if(_stop.IsCancellationRequested)return; throw; }
            }
        }
        private static async Task<byte[]> ReadAsync(Stream stream,int count,CancellationToken token)
        {
            var data=new byte[count];
            for(int at=0;at<count;)
            {
                int n=await stream.ReadAsync(data,at,count-at,token);
                if(n==0)throw new EndOfStreamException();
                at+=n;
            }
            return data;
        }
        public void Stop()
        {
            _stop.Cancel();
            _listener.Stop();
        }
        public void Dispose()
        {
            Stop();
            _server.GetAwaiter().GetResult();
            _stop.Dispose();
        }
    }
}
