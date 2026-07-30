using Crestron.DeviceDrivers.EntityModel.Data;

namespace KasaTapoCrestronDriver;

public sealed partial class PlatformDriver
	{
	private void NotifyCredentialValuesChanged ()
		{
		LogInfo ($"NotifyCredentialValuesChanged: publishing userNameState={DescribeConfiguredValueState (_userName)}, passwordState={DescribeConfiguredValueState (_password)} back to the configuration controller.");
		// TEMPORARY DIAGNOSTIC: UserName is not masked/secret, so log a redacted preview of the
		// value being published back to the configuration controller (i.e. what will be persisted
		// to the .dat configuration storage).
		LogInfo ($"NotifyCredentialValuesChanged: UserName being put back to configuration storage: {DescribeCredentialPreview (_userName)}.");
		_dataDrivenConfigurationController.NotifyValuesChanged (
			new Dictionary<string, DriverEntityValue?>
				{
				["UserName"] = new DriverEntityValue (_userName),
				["Password"] = new DriverEntityValue (_password)
				});
		}

	private void ApplyValues (IDictionary<string, DriverEntityValue?> values)
		{
		bool hasUserNameValue = values.TryGetValue ("UserName", out var userNameValue) && userNameValue.HasValue;
		bool hasPasswordValue = values.TryGetValue ("Password", out var passwordValue) && passwordValue.HasValue;

		string? configuredUserName = hasUserNameValue
			? userNameValue!.Value.GetValue<string> ()?.Trim ()
			: null;
		string? configuredPassword = hasPasswordValue
			? passwordValue!.Value.GetValue<string> ()
			: null;

		LogInfo ($"ApplyValues credential shape: userNamePresent={hasUserNameValue}, userNameState={DescribeConfiguredValueState (configuredUserName)}, passwordPresent={hasPasswordValue}, passwordState={DescribeConfiguredValueState (configuredPassword)}.");

		if (hasUserNameValue)
			{
			// TEMPORARY DIAGNOSTIC: UserName is not masked/secret, so log a redacted preview to
			// confirm the value actually retrieved from the incoming Apply values and whether it
			// changed from what was previously held in memory.
			LogInfo ($"ApplyValues: UserName retrieved from configuration values: previous={DescribeCredentialPreview (_userName)}, incoming={DescribeCredentialPreview (configuredUserName)}, changed={!string.Equals (_userName, configuredUserName ?? string.Empty, StringComparison.Ordinal)}.");
			_userName = configuredUserName ?? string.Empty;
			}

		// Password is a masked configuration item. Crestron Home's configuration UI fires a
		// RealtimeChange re-apply as soon as focus leaves an edited field, and the live log
		// evidence shows this can capture the Password box mid-edit - a transient empty resend for
		// the very same apply in which the user is actively typing a new password (confirmed live:
		// userNameState=non-empty, passwordState=empty under mode=RealtimeChange, which immediately
		// tripped the "must both be provided" validation below even though the user was in the
		// middle of entering a real password, not clearing it). Blindly overwriting _password with
		// that transient empty resend would silently erase/reject an in-progress password entry.
		// Only actually clear _password when the user has also explicitly cleared UserName in the
		// same apply - that is the only way this driver's UI lets a user signal "I want to remove
		// my Tapo credentials". A later apply in the same edit session carries the real, fully
		// committed password value.
		if (hasPasswordValue && (!string.IsNullOrEmpty (configuredPassword) || string.IsNullOrEmpty (configuredUserName)))
			{
			_password = configuredPassword ?? string.Empty;
			}

		if (values.TryGetValue ("DiscoveryTimeoutSeconds", out var timeoutValue) && timeoutValue.HasValue)
			{
			_discoveryTimeoutSeconds = timeoutValue.Value.GetValue<string> ()?.Trim () ?? _discoveryTimeoutSeconds;
			}

		if (values.TryGetValue ("EnableLightPolling", out var enableLightPollingValue) && enableLightPollingValue.HasValue)
			{
			_enableLightPolling = enableLightPollingValue.Value.GetValue<bool> ();
			}

		if (values.TryGetValue ("LightPollIntervalSeconds", out var lightPollIntervalValue) && lightPollIntervalValue.HasValue)
			{
			_lightPollIntervalSeconds = lightPollIntervalValue.Value.GetValue<string> ()?.Trim () ?? _lightPollIntervalSeconds;
			}

		if (values.TryGetValue ("SensorPollIntervalSeconds", out var sensorPollIntervalValue) && sensorPollIntervalValue.HasValue)
			{
			_sensorPollIntervalSeconds = sensorPollIntervalValue.Value.GetValue<string> ()?.Trim () ?? _sensorPollIntervalSeconds;
			}

		if (values.TryGetValue ("TreatPlugsAsLights", out var treatPlugsValue) && treatPlugsValue.HasValue)
			{
			_treatPlugsAsLights = treatPlugsValue.Value.GetValue<bool> ();
			}

		if (values.TryGetValue ("EnableProcessorBaselineWorkaround", out var enableProcessorBaselineWorkaroundValue) && enableProcessorBaselineWorkaroundValue.HasValue)
			{
			_enableProcessorBaselineWorkaround = enableProcessorBaselineWorkaroundValue.Value.GetValue<bool> ();
			}

		if (values.TryGetValue ("ProcessorSshHost", out var processorSshHostValue) && processorSshHostValue.HasValue)
			{
			_processorSshHost = processorSshHostValue.Value.GetValue<string> ()?.Trim () ?? _processorSshHost;
			}

		bool hasProcessorSshUserNameValue = values.TryGetValue ("ProcessorSshUserName", out var processorSshUserNameValue) && processorSshUserNameValue.HasValue;
		bool hasProcessorSshPasswordValue = values.TryGetValue ("ProcessorSshPassword", out var processorSshPasswordValue) && processorSshPasswordValue.HasValue;

		string? configuredProcessorSshUserName = hasProcessorSshUserNameValue
			? processorSshUserNameValue!.Value.GetValue<string> ()?.Trim ()
			: null;
		string? configuredProcessorSshPassword = hasProcessorSshPasswordValue
			? processorSshPasswordValue!.Value.GetValue<string> ()
			: null;

		if (hasProcessorSshUserNameValue)
			{
			_processorSshUserName = configuredProcessorSshUserName ?? _processorSshUserName;
			}

		// Same transient-empty-resend protection as Password above: Generic.Prompt items commit on
		// blur, so editing ProcessorSshPassword can resend a transient empty value while the user is
		// still typing. Only actually clear _processorSshPassword when ProcessorSshUserName was also
		// explicitly cleared in the same apply.
		if (hasProcessorSshPasswordValue && (!string.IsNullOrEmpty (configuredProcessorSshPassword) || string.IsNullOrEmpty (configuredProcessorSshUserName)))
			{
			_processorSshPassword = configuredProcessorSshPassword ?? string.Empty;
			}
		}

	private static string DescribeConfiguredValueState (string? value)
		{
		if (value is null)
			{
			return "null";
			}

		return value.Length == 0
			? "empty"
			: "non-empty";
		}

	// TEMPORARY DIAGNOSTIC: UserName is not a masked/secret configuration item, so it is safe to
	// log a redacted preview (first 3 characters + ellipsis + last 3 characters) to help verify
	// that credentials are actually retrieved from and stored to configuration correctly. This
	// should be removed once the Apply-time credential capture issue is confirmed resolved.
	private static string DescribeCredentialPreview (string? value)
		{
		if (value is null)
			{
			return "(null)";
			}

		if (value.Length == 0)
			{
			return "(empty)";
			}

		return value.Length < 6
			? $"{value.Substring (0, Math.Min (3, value.Length))}..."
			: $"{value.Substring (0, 3)}...{value.Substring (value.Length - 3)}";
		}

	}
