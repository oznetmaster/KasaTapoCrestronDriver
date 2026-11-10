// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
using System.Diagnostics;
namespace KasaTapoCrestronDriver;

internal sealed partial class KasaOutletEntity
	{
#if OUTLET_DIAGNOSTICS
    internal Action<OutletDiagnosticRecord>? DiagnosticObserverForTests { get; set; }
    private void CaptureDiagnostic(OutletDiagnosticRecord record)
    { DiagnosticObserverForTests?.Invoke(record); OutletDiagnosticJournal.Capture(record); }
    private readonly string _diagnosticInstance = Guid.NewGuid().ToString("N");
    private OutletDiagnosticRecord DiagnosticRecord(string stage)
    {
        var c = _configuration;
        var p = c?.ConnectionOptions.ConnectionParameters;
        return new OutletDiagnosticRecord {
            Event = stage, Instance = _diagnosticInstance, Controller = ControllerId,
            Name = DeviceName, Model = ModelName, Host = c?.Host ?? "", Port = c?.Port,
            Transport = c?.ConnectionOptions.TransportKind.ToString(), Family = p?.DeviceFamily.ToString(),
            Encryption = p?.EncryptionKind.ToString(), LoginVersion = p?.LoginVersion,
            Disposed = _disposed, Stopped = Volatile.Read(ref _stopState) != 0,
            Configured = _isConfigured, Generation = Volatile.Read(ref _pollingGeneration)
        };
    }
#endif
	[Conditional ("OUTLET_DIAGNOSTICS")]
	private void TraceOutlet (string stage, Exception? error = null, double? durationMs = null)
		{
#if OUTLET_DIAGNOSTICS
        try {
            var record = DiagnosticRecord(stage);
            record.DurationMs = durationMs;
            if (error is not null) {
                record.ErrorType = error.GetType().FullName;
                record.ErrorReason = (error.Message ?? "").Contains("did not contain a tpap object") ? "MissingTpapDiscoveryObject" : "OtherFailure";
                // Stack frames only: exception messages and response bodies may contain credentials.
                record.ErrorStack = error.StackTrace;
            }
            CaptureDiagnostic(record);
        } catch { }
#endif
		}
	[Conditional ("OUTLET_DIAGNOSTICS")]
	private void TraceNumericDecision (string property, double previous, double candidate, bool changed)
		{
#if OUTLET_DIAGNOSTICS
        try {
            var record = DiagnosticRecord("numeric-decision"); record.Property = property;
            record.Previous = previous; record.Value = candidate; record.Changed = changed;
            CaptureDiagnostic(record);
        } catch { }
#endif
		}
	[Conditional ("OUTLET_DIAGNOSTICS")]
	private void TraceSample (KasaDevice device)
		{
#if OUTLET_DIAGNOSTICS
        try {
            var e = device.Energy;
            var record = DiagnosticRecord("confirmed-sample"); record.IsOn = device.IsOn;
            record.Raw = new OutletDiagnosticEnergy { Voltage = e?.VoltageVolts, Current = e?.CurrentAmps,
                Power = e?.CurrentPowerWatts, Today = e?.TodayKilowattHours, Month = e?.MonthKilowattHours, Total = e?.TotalKilowattHours };
            record.Published = new OutletDiagnosticEnergy { Voltage = OutletEnergyVoltageVolts, Current = OutletEnergyCurrentAmps,
                Power = OutletEnergyCurrentPowerWatts, Today = OutletEnergyTodayKilowattHours,
                Month = OutletEnergyMonthKilowattHours, Total = OutletEnergyTotalKilowattHours };
            CaptureDiagnostic(record);
        } catch { }
#endif
		}
	}