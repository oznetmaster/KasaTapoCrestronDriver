// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using KasaTapoCrestronDriver.AndroidTests;
using NUnit.Framework;

namespace KasaAppEvidenceContracts;

[TestFixture, Category("unit")]
public sealed class PowerInterruptionContractTests
{
    static OutletTarget Subject => new("selected", 1, "plug", "Test Plug", "Test Room", 1, "subject-discovery", "subject-auth", null, true, true);
    static PowerInterruptionSettings Plan => new("selected", new("supply-discovery", "supply-auth", "socket", true),
        [new("light-discovery", "light-auth", null, true)]);

    [Test]
    public void IndependentAuthorizedSupplyAndCollateralLightAreAccepted() =>
        Assert.DoesNotThrow(() => PowerInterruptionTests.Validate(Plan, Subject));

    [Test]
    public void SupplyCannotBeTheSubjectOrAnEntireUnspecifiedStrip()
    {
        Assert.Throws<InvalidDataException>(() => PowerInterruptionTests.Validate(Plan with { Supply = Plan.Supply with { DiscoveryId = Subject.DiscoveryId } }, Subject));
        Assert.Throws<InvalidDataException>(() => PowerInterruptionTests.Validate(Plan with { Supply = Plan.Supply with { ChildId = null } }, Subject));
    }

    [Test]
    public void EveryPhysicalMutationRequiresExplicitAuthorization()
    {
        Assert.Throws<InvalidDataException>(() => PowerInterruptionTests.Validate(Plan, Subject with { ControlsAuthorized = false }));
        Assert.Throws<InvalidDataException>(() => PowerInterruptionTests.Validate(Plan with { Supply = Plan.Supply with { ControlsAuthorized = false } }, Subject));
        Assert.Throws<InvalidDataException>(() => PowerInterruptionTests.Validate(Plan with { CollateralLights = [Plan.CollateralLights[0] with { ControlsAuthorized = false }] }, Subject));
    }

    [Test]
    public void DuplicateOrOverlappingCollateralTargetsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => PowerInterruptionTests.Validate(Plan with { CollateralLights = [Plan.CollateralLights[0], Plan.CollateralLights[0]] }, Subject));
        Assert.Throws<InvalidDataException>(() => PowerInterruptionTests.Validate(Plan with { CollateralLights = [new("other", Subject.AuthenticatedId, null, true)] }, Subject));
        Assert.Throws<InvalidDataException>(() => PowerInterruptionTests.Validate(Plan with { CollateralLights = [new("other", Plan.Supply.AuthenticatedId, null, true)] }, Subject));
    }

    [Test]
    public async Task DiscoveryRetriesOnlyMissingResponsesAndRecordsEachAttempt()
    {
        int calls = 0;
        var observed = new List<(int Attempt, int Matches, int Hosts)>();
        var selected = await PowerInterruptionTests.DiscoverUnique<string>(_ =>
            Task.FromResult<IReadOnlyList<string>>(++calls == 1 ? [] : ["selected-host", "selected-host"]),
            h => h, (a, m, h) => { observed.Add((a, m, h)); return Task.CompletedTask; }, CancellationToken.None);
        Assert.That(selected, Is.EqualTo("selected-host"));
        Assert.That(observed, Is.EqualTo(new[] { (1, 0, 0), (2, 2, 1) }));
    }

    [Test]
    public void ConflictingHostsAreRejectedWithoutRetry()
    {
        int calls = 0;
        Assert.ThrowsAsync<InvalidDataException>(async () => await PowerInterruptionTests.DiscoverUnique<string>(_ =>
        { calls++; return Task.FromResult<IReadOnlyList<string>>(["host-one", "host-two"]); },
            h => h, (_, _, _) => Task.CompletedTask, CancellationToken.None));
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public void MissingResponsesStopAfterThreeAttempts()
    {
        int calls = 0;
        Assert.ThrowsAsync<InvalidDataException>(async () => await PowerInterruptionTests.DiscoverUnique<string>(_ =>
        { calls++; return Task.FromResult<IReadOnlyList<string>>([]); },
            h => h, (_, _, _) => Task.CompletedTask, CancellationToken.None));
        Assert.That(calls, Is.EqualTo(3));
    }

    [Test]
    public void CancellationStopsBeforeAnotherDiscovery()
    {
        using var cancel = new CancellationTokenSource();
        int calls = 0;
        Assert.ThrowsAsync<OperationCanceledException>(async () => await PowerInterruptionTests.DiscoverUnique<string>(_ =>
        { calls++; cancel.Cancel(); return Task.FromResult<IReadOnlyList<string>>([]); },
            h => h, (_, _, _) => Task.CompletedTask, cancel.Token));
        Assert.That(calls, Is.EqualTo(1));
    }
}
