// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the project root.

using System.Diagnostics;
using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.EntityModel.Data.DeviceConfiguration;
using Crestron.DeviceDrivers.EntityModel.Logging;
using Crestron.DeviceDrivers.SDK.EntityModel;

namespace KasaTapoCrestronDriver;

// Diagnostic pass-through at the host-facing API. Internal cached replay bypasses
// this controller, so these calls can be distinguished from driver self-calls.
internal sealed class RegistrationTracingController : DispatchingDeviceController
{
    private readonly Action<string> _trace;
    private long _requestSequence;

    internal RegistrationTracingController(ConfigurableDriverEntity root, DriverControllerCreationArgs args,
        Action<string>? trace = null) : base(root, args, null)
    {
        _trace = trace ?? (message => args.Logger.Log(args.DriverId, LogEntryLevel.Info, message));
        ControllerIdsChanged += OnControllerIdsChanged;
        Write(() => $"created; registry=[{Keys(base.ControllerIds)}]");
    }

    public override ICollection<string> ControllerIds => Observe("ControllerIds", "*",
        () => base.ControllerIds, ids => $"ids=[{Keys(ids)}]");

    public override DriverControllerStatus GetStatus(string controllerId) => Observe("GetStatus", controllerId,
        () => base.GetStatus(controllerId), status => $"status={status}");

    public override DriverEntityState GetState(string controllerId) => Observe("GetState", controllerId,
        () => base.GetState(controllerId), DescribeState);

    public override ConfigurationStep GetFirstConfigurationStep(string controllerId) => Observe("GetFirstConfigurationStep", controllerId,
        () => base.GetFirstConfigurationStep(controllerId), step => $"step='{step?.Id}'");

    public override ApplyConfigurationResult ApplyConfiguration(string controllerId, IDictionary<string, string> values) => Observe("ApplyConfiguration", controllerId,
        () => base.ApplyConfiguration(controllerId, values), result => $"errorKeys=[{Keys(result?.ConfigurationErrorsByItemId?.Keys)}]");

    public override ApplyConfigurationStepResult ApplyConfigurationStep(string controllerId, string stepId, IDictionary<string, string> values) => Observe("ApplyConfigurationStep", controllerId,
        () => base.ApplyConfigurationStep(controllerId, stepId, values), result => $"step='{stepId}'; next='{result?.NextConfigurationStep?.Id}'; errorKeys=[{Keys(result?.ConfigurationErrorsByItemId?.Keys)}]");

    private T Observe<T>(string method, string controllerId, Func<T> operation, Func<T, string> describe)
    {
        long request = Interlocked.Increment(ref _requestSequence);
        var timer = Stopwatch.StartNew();
        Write(() => $"request={request}; method={method}; controllerId='{controllerId}'; begin; registryContainsId={TryGetController(controllerId, out _)}");
        T result;
        try
        {
            result = operation();
        }
        catch (Exception ex)
        {
            // Preserve the original exception; omit message/payload values from logs.
            Write(() => $"request={request}; method={method}; controllerId='{controllerId}'; threw={ex.GetType().FullName}; elapsedMs={timer.ElapsedMilliseconds}");
            throw;
        }
        Write(() => $"request={request}; method={method}; controllerId='{controllerId}'; returned; elapsedMs={timer.ElapsedMilliseconds}; {describe(result)}");
        return result;
    }

    private static string DescribeState(DriverEntityState state)
    {
        var definition = state.Definition;
        string managedIds = "<absent>";
        if (state.PropertyValues.TryGetValue("platform:managedDevices", out var managed))
        {
            managedIds = managed.TryGetValue<DriverEntityValueDictionary>(out var entries)
                ? Keys(entries.Keys) : "<unexpected-value-type>";
        }
        return $"properties=[{Keys(definition.Properties.Keys)}]; events=[{Keys(definition.Events.Keys)}]; commands=[{Keys(definition.Commands.Keys)}]; valueKeys=[{Keys(state.PropertyValues.Keys)}]; managedIds=[{managedIds}]";
    }

    private void OnControllerIdsChanged(object? sender, ControllerIdsChangedEventArgs args) =>
        Write(() => $"ControllerIdsChanged; added=[{Keys(args.AddedControllerIds)}]; removed=[{Keys(args.RemovedControllerIds)}]");

    private static string Keys(IEnumerable<string>? keys) =>
        keys == null ? string.Empty : string.Join(",", keys.OrderBy(key => key, StringComparer.Ordinal));

    private void Write(Func<string> message)
    {
        // Diagnostics must not change a host call's result, even if logging fails.
        try { _trace?.Invoke($"REGISTRATION-TRACE: {message()}"); }
        catch { }
    }

    public override void Dispose()
    {
        ControllerIdsChanged -= OnControllerIdsChanged;
        Write(() => "disposing");
        base.Dispose();
    }
}
