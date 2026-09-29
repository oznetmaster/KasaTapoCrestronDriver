// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System.Net;
using CrestronHomeDevTools;
using KasaTapoCrestronDriver.AndroidTests;
using NUnit.Framework;

namespace KasaAppEvidenceContracts;

[TestFixture, Category("unit")]
public sealed class PreparedProcessorSessionTests
{
    [Test]
    public async Task ExpiredSessionIsDiscardedBeforeFreshBaselineRead()
    {
        var expired = new Connection(true);
        var fresh = new Connection(false);
        int authentications = 0;
        await using var api = await PreparedProcessorSession.RenewAsync(new(expired, true), ct =>
        {
            Assert.That(expired.Disposed, Is.True);
            authentications++;
            return Task.FromResult(new ConfigurationClient(fresh, true));
        }, CancellationToken.None);
        var baseline = await api.GetDeviceAsync(42);
        Assert.That(baseline!.Id, Is.EqualTo(42));
        Assert.That(authentications, Is.EqualTo(1));
        Assert.That(expired.Reads, Is.Zero);
        Assert.That(fresh.Reads, Is.EqualTo(1));
    }

    [Test]
    public async Task AuthenticationFailureDoesNotFallBackToExpiredSessionOrRetry()
    {
        var expired = new Connection(true);
        int attempts = 0;
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await PreparedProcessorSession.RenewAsync(new(expired, true), ct =>
            {
                attempts++;
                throw new HttpRequestException("Authentication unavailable");
            }, CancellationToken.None));
        Assert.That(attempts, Is.EqualTo(1));
        Assert.That(expired.Disposed, Is.True);
        Assert.That(expired.Reads, Is.Zero);
    }

    [Test]
    public async Task CancellationDisposesOldSessionWithoutConnecting()
    {
        var expired = new Connection(true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await PreparedProcessorSession.RenewAsync(new(expired, true), ct =>
                throw new AssertionException("Must not reconnect after cancellation"), cancellation.Token));
        Assert.That(expired.Disposed, Is.True);
        Assert.That(expired.Reads, Is.Zero);
    }

    private sealed class Connection(bool expired) : IConfigurationConnection
    {
        internal bool Disposed;
        internal int Reads;
        public Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken = default)
        {
            Reads++;
            if(expired) throw new ProcessorApiException("Session expired", HttpStatusCode.BadRequest);
            Assert.That(Disposed, Is.False);
            Assert.That(path, Is.EqualTo("v2/Devices/42"));
            return Task.FromResult((T?)(object)new DeviceInfo { Id = 42 });
        }
        public Task<T?> ExecuteAsync<T>(int deviceId, string commandName, object? parameters = null,
            CancellationToken cancellationToken = default) => throw new AssertionException("No mutation permitted");
        public Task<OperationResult> WaitForOperationAsync(string id, TimeSpan timeout,
            CancellationToken cancellationToken = default) => throw new AssertionException("No operation permitted");
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
