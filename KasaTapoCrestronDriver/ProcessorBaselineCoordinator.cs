using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace KasaTapoCrestronDriver;

internal enum ProcessorLightTuningMode
	{
	Color,
	White
	}

internal sealed class ProcessorBaselineCoordinator : IDisposable
	{
	private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds (8);
	private static readonly TimeSpan ConsoleResponseQuietPeriod = TimeSpan.FromMilliseconds (250);
	private static readonly TimeSpan IdleSessionLifetime = TimeSpan.FromMinutes (90);
	// SSH.NET's ConnectionInfo.Timeout does not reliably bound a stalled TCP connect on this
	// platform (packets can be silently dropped rather than rejected), so Connect()/WaitForPrompt()
	// can block well past CommandTimeout. Without an outer hard deadline, a single stall permanently
	// holds _gate and silently disables all future baseline synchronization for the process lifetime.
	private static readonly TimeSpan OverallOperationTimeout = TimeSpan.FromSeconds (20);
	// The console prompt is model-specific (e.g. "CP4-R>", "MC4-R>", "PRO3>"), so match the generic
	// Crestron console prompt shape instead of a single hardcoded model string.
	private static readonly Regex ProcessorConsolePromptExpression = new (@"(?:^|[\r\n])[A-Z0-9]{2,8}(?:-R)?>", RegexOptions.CultureInvariant);
	private static readonly Regex LightLoadHeaderExpression = new (@"^\s*LightLoad\s*#\d+\s*:\s*$", RegexOptions.CultureInvariant);
	private static readonly Regex LoadIdExpression = new (@"^\s*Id\s*:\s*(?<id>\d+)\s*$", RegexOptions.CultureInvariant);
	private static readonly Regex LoadNameExpression = new (@"^\s*LoadName\s*:\s*(?<name>.+?)\s*$", RegexOptions.CultureInvariant);
	// ListAllLoadStates reports two distinct TuningMode fields per load: one nested inside
	// BaselineSetting (the stored/target tuning mode - the one the processor re-asserts on every
	// off->on power transition, which is what actually drives the Load layer to dispatch
	// lightEmulatedColorTemperature:setLevel as a side effect of a plain power-on) and a separate
	// one following TunableChannelStates, which only reflects whichever channel happens to be
	// currently active/rendered. These two can disagree (confirmed live via direct console
	// inspection: BaselineSetting.TuningMode=WhiteTuning while TunableChannelStates.TuningMode
	// still read ColorTuning from the live Hue/Saturation values) - it is the STORED
	// BaselineSetting value that must be captured and corrected here, not the transient active
	// value, otherwise a synchronize call wrongly concludes the processor "already matches" the
	// requested mode (because the active channel happens to agree) while the stored baseline stays
	// wrong and keeps re-triggering the White artifact on every subsequent power-on.
	private static readonly Regex BaselineTuningModeExpression = new (@"LoadId\s*:\s*(?<id>\d+)(?:(?!\s*LightLoadState\s*#).)*?BaselineSetting\s*:(?:(?!\s*LightLoadState\s*#).)*?TuningMode\s*:\s*(?<mode>\w+)", RegexOptions.Singleline | RegexOptions.CultureInvariant);
	// Detects a .NET exception echoed back on the processor console (e.g. RpcLightsManager
	// rejecting a SetLoadState call whose parameters don't match the load's current tuning mode).
	private static readonly Regex ConsoleExceptionExpression = new (@"^\s*\S*Exception\s*:", RegexOptions.Multiline | RegexOptions.CultureInvariant);
	private readonly IPlatformSharedConfiguration _configuration;
	private readonly Action<string> _logInfo;
	private readonly Action<string> _logError;
	private readonly SemaphoreSlim _gate = new (1, 1);
	private readonly Dictionary<string, long> _loadIdsByName = new (StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<long, string> _baselineTuningModesByLoadId = new ();
	private SshClient? _client;
	private ShellStream? _shell;
	private DateTime _connectedUtc;
	private bool _disposed;

	public ProcessorBaselineCoordinator (IPlatformSharedConfiguration configuration, Action<string> logInfo, Action<string> logError)
		{
		_configuration = configuration;
		_logInfo = logInfo;
		_logError = logError;
		SshNetLoggingConfiguration.InitializeLogging (new ProcessorSshLoggerFactory (_logInfo));
		}

	public Task SynchronizeAsync (string loadName, ProcessorLightTuningMode mode, double level, double hue, double saturation, long colorTemperature, CancellationToken cancellationToken)
		{
		return ExecuteSafelyAsync (loadName, mode, level, hue, saturation, colorTemperature, cancellationToken);
		}

	private async Task ExecuteSafelyAsync (string loadName, ProcessorLightTuningMode mode, double level, double hue, double saturation, long colorTemperature, CancellationToken cancellationToken)
		{
		if (_disposed || !_configuration.EnableProcessorBaselineWorkaround || string.IsNullOrWhiteSpace (loadName))
			{
			return;
			}

		await _gate.WaitAsync (cancellationToken).ConfigureAwait (false);
		try
			{
			using var overallTimeoutSource = new CancellationTokenSource (OverallOperationTimeout);
			using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken, overallTimeoutSource.Token);
			Task synchronizeTask = SynchronizeCoreAsync (loadName, mode, level, hue, saturation, colorTemperature, linkedSource.Token);
			Task completedTask = await Task.WhenAny (synchronizeTask, Task.Delay (Timeout.Infinite, linkedSource.Token)).ConfigureAwait (false);
			if (!ReferenceEquals (completedTask, synchronizeTask))
				{
				// The blocking SSH.NET calls inside SynchronizeCoreAsync do not observe cancellation
				// tokens, so this hard deadline cannot abort the stuck call; it only prevents the
				// hang from being reported as success and forces the connection to be torn down and
				// re-established on the next attempt instead of being reused in an unknown state.
				if (overallTimeoutSource.IsCancellationRequested)
					{
					_logError ($"Processor baseline synchronization timed out after {OverallOperationTimeout} for loadName='{loadName}'; abandoning connection.");
					Disconnect ();
					}

				await synchronizeTask.ConfigureAwait (false);
				return;
				}

			await synchronizeTask.ConfigureAwait (false);
			}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
			}
		catch (Exception ex)
			{
			_logError ($"Processor baseline synchronization failed for loadName='{loadName}': {ex}");
			Disconnect ();
			}
		finally
			{
			_gate.Release ();
			}
		}

	private async Task SynchronizeCoreAsync (string loadName, ProcessorLightTuningMode mode, double level, double hue, double saturation, long colorTemperature, CancellationToken cancellationToken)
		{
		try
			{
			long loadId = await ResolveLoadIdAsync (loadName, cancellationToken).ConfigureAwait (false);
			if (loadId <= 0)
				{
				return;
				}

			string tuningMode = mode == ProcessorLightTuningMode.White ? "WhiteTuning" : "ColorTuning";
			if (!_baselineTuningModesByLoadId.TryGetValue (loadId, out string? cachedProcessorTuningMode))
				{
				string processorTuningMode = await ResolveBaselineTuningModeAsync (loadId, cancellationToken).ConfigureAwait (false)
				?? throw new InvalidOperationException ($"Processor baseline synchronization skipped: unable to determine baseline tuning mode for loadId={loadId}.");
				_baselineTuningModesByLoadId[loadId] = processorTuningMode;
				cachedProcessorTuningMode = processorTuningMode;
				_logInfo ($"Processor baseline initialized: loadName='{loadName}', loadId={loadId}, tuningMode={processorTuningMode}.");
			}

			if (string.Equals (cachedProcessorTuningMode, tuningMode, StringComparison.OrdinalIgnoreCase))
				{
				_logInfo ($"Processor baseline already matches cached mode: loadName='{loadName}', loadId={loadId}, tuningMode={tuningMode}.");
				return;
				}

			int hueDegrees = (int)Math.Round (Math.Max (0d, Math.Min (1d, hue)) * 360d, MidpointRounding.AwayFromZero);
			int saturationPercent = (int)Math.Round (Math.Max (0d, Math.Min (1d, saturation)) * 100d, MidpointRounding.AwayFromZero);
			long temperature = Math.Max (1L, colorTemperature);
			int levelPercent = (int)Math.Round (Math.Max (0d, Math.Min (100d, level)), MidpointRounding.AwayFromZero);
			string baselineCommand = string.Format (CultureInfo.InvariantCulture, "ch rpc lights SetBaselineSetting {0} {1} {2} {3} false {4}", loadId, hueDegrees, saturationPercent, temperature, tuningMode);
			// Wait for the baseline command to fully complete (rather than fire-and-forget) so the
			// SetLoadState toggle below is issued against a load whose baseline has already been
			// persisted by the processor.
			string baselineResponse = await ExecuteCommandAsync (baselineCommand, cancellationToken).ConfigureAwait (false);
			if (ConsoleExceptionExpression.IsMatch (baselineResponse))
				{
				_logError ($"Processor baseline update failed: loadName='{loadName}', loadId={loadId}, tuningMode={tuningMode}, command='SetBaselineSetting'. Response: '{baselineResponse}'.");
				// Invalidate the cached tuning mode: it may no longer reflect the processor's real
				// current baseline, forcing the next synchronize call to re-resolve it fresh rather
				// than incorrectly treating a stale cached value as an "already matches" no-op.
				_baselineTuningModesByLoadId.Remove (loadId);
				return;
				}

			// The processor only asserts a load's baseline TuningMode into the UI on an actual
			// off->on power transition; issuing SetLoadState with the on-flag while the load is
			// already on does not re-assert it. Force a real off->on toggle here so the new
			// baseline takes effect immediately instead of silently waiting for the next time the
			// user happens to power-cycle the load from the UI.
			//
			// SetLoadState's parameter format is LoadId:ChannelType(int):Level(0-65535):Duration(ms)
			// (confirmed via "ch rpc lights SetLoadState -help" on the processor console) - the
			// second field is a LightChannelType enum value, NOT an on/off flag, and Level is a
			// 0-65535 scale, not a 0-100 percentage. This load only supports LightChannelType.White,
			// whose enum value is 1; sending ChannelType 0 (as if it were an "off" boolean) is an
			// invalid parameter and is unconditionally rejected by
			// RpcLightsManager.ThrowIfAnyLoadStatesContainInvalidParameters with
			// NotSupportedException ("channels other than LightChannelType.White are not
			// supported") - confirmed by direct console testing. Off is expressed as Channel
			// White at level 0, not a different/invalid channel type.
			const int whiteChannelType = 1;
			const long maxChannelLevel = 65535L;
			string offCommand = string.Format (CultureInfo.InvariantCulture, "ch rpc lights SetLoadState {0}:{1}:0:0", loadId, whiteChannelType);
			string offResponse = await ExecuteCommandAsync (offCommand, cancellationToken).ConfigureAwait (false);
			if (ConsoleExceptionExpression.IsMatch (offResponse))
				{
				_logError ($"Processor baseline update failed: loadName='{loadName}', loadId={loadId}, tuningMode={tuningMode}, command='SetLoadState:off'. Response: '{offResponse}'.");
				// SetBaselineSetting above already succeeded and mutated the processor's real
				// baseline before this toggle failed, so the cached value is now stale. Invalidate
				// it so the next synchronize call re-resolves the processor's real current baseline
				// instead of skipping a needed update.
				_baselineTuningModesByLoadId.Remove (loadId);
				return;
				}

			long onChannelLevel = (long)Math.Round (levelPercent / 100d * maxChannelLevel, MidpointRounding.AwayFromZero);
			string onCommand = string.Format (CultureInfo.InvariantCulture, "ch rpc lights SetLoadState {0}:{1}:{2}:0", loadId, whiteChannelType, onChannelLevel);
			string onResponse = await ExecuteCommandAsync (onCommand, cancellationToken).ConfigureAwait (false);
			if (ConsoleExceptionExpression.IsMatch (onResponse))
				{
				_logError ($"Processor baseline update failed: loadName='{loadName}', loadId={loadId}, tuningMode={tuningMode}, command='SetLoadState:on'. Response: '{onResponse}'.");
				// Same rationale as the SetLoadState:off failure above: the baseline setting itself
				// already changed on the processor, so the cache must be invalidated rather than left
				// pointing at a stale value.
				_baselineTuningModesByLoadId.Remove (loadId);
				return;
				}

			// Only cache the tuning mode as successfully applied once every command in the
			// sequence completed without the processor console echoing back an exception; caching
			// unconditionally would cause future synchronize calls to believe the processor
			// baseline already matches the requested mode when the update actually failed,
			// silently leaving the processor's TuningMode stale.
			_baselineTuningModesByLoadId[loadId] = tuningMode;
			_logInfo ($"Processor baseline update sent: loadName='{loadName}', loadId={loadId}, tuningMode={tuningMode}, levelPercent={levelPercent}.");
			}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
			}
		}

	private async Task<long> ResolveLoadIdAsync (string loadName, CancellationToken cancellationToken)
		{
		if (_loadIdsByName.TryGetValue (loadName, out long cachedLoadId))
			{
			return cachedLoadId;
			}

		string loads = await ExecuteCommandAsync ("ch rpc lights ListAllLoads", cancellationToken).ConfigureAwait (false);
		var matchingIds = new List<long> ();
		long? currentLoadId = null;
		foreach (string line in Regex.Split (loads, @"\r\n|\r|\n"))
			{
			if (LightLoadHeaderExpression.IsMatch (line))
				{
				currentLoadId = null;
				continue;
				}

			Match idMatch = LoadIdExpression.Match (line);
			if (idMatch.Success && long.TryParse (idMatch.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsedLoadId))
				{
				currentLoadId = parsedLoadId;
				continue;
				}

			Match nameMatch = LoadNameExpression.Match (line);
			if (currentLoadId.HasValue && nameMatch.Success)
				{
				string processorLoadName = nameMatch.Groups["name"].Value.Trim ();
				bool isMatch = IsMatchingLoadName (loadName, processorLoadName);
				_logInfo ($"Processor baseline load candidate: requestedName='{loadName}', processorName='{processorLoadName}', loadId={currentLoadId.Value}, isMatch={isMatch}.");
				if (isMatch)
					{
					matchingIds.Add (currentLoadId.Value);
					}
				}
			}

		if (matchingIds.Count != 1)
			{
			_logError ($"Processor baseline synchronization skipped: loadName='{loadName}' matched {matchingIds.Count} processor loads. Load names must be unique.");
			return 0;
			}

		_loadIdsByName[loadName] = matchingIds[0];
		_logInfo ($"Processor baseline load resolved: loadName='{loadName}', loadId={matchingIds[0]}.");
		return matchingIds[0];
		}

	private async Task<string?> ResolveBaselineTuningModeAsync (long loadId, CancellationToken cancellationToken)
		{
		string loadStates = await ExecuteCommandAsync ("ch rpc lights ListAllLoadStates", cancellationToken).ConfigureAwait (false);
		foreach (Match match in BaselineTuningModeExpression.Matches (loadStates))
			{
			if (long.TryParse (match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long stateLoadId)
				&& stateLoadId == loadId)
				{
				return match.Groups["mode"].Value;
				}
			}

		throw new InvalidOperationException ($"Processor baseline synchronization skipped: unable to determine baseline tuning mode for loadId={loadId}.");
		}

	private static bool IsMatchingLoadName (string kasaTapoLoadName, string processorLoadName)
		{
		return string.Equals (kasaTapoLoadName, processorLoadName, StringComparison.OrdinalIgnoreCase)
			|| (kasaTapoLoadName.Length > processorLoadName.Length
				&& kasaTapoLoadName.EndsWith (processorLoadName, StringComparison.OrdinalIgnoreCase)
				&& kasaTapoLoadName[^(processorLoadName.Length + 1)] == ' ');
		}

	private Task<string> ExecuteCommandAsync (string command, CancellationToken cancellationToken)
		{
		cancellationToken.ThrowIfCancellationRequested ();
		EnsureConnected ();

		while (_shell!.DataAvailable)
			{
			_shell.Read ();
			}

		_shell!.WriteLine (command);
		DateTime deadlineUtc = DateTime.UtcNow + CommandTimeout;
		DateTime? lastResponseUtc = null;
		bool promptReceived = false;
		var response = new StringBuilder ();
		while (DateTime.UtcNow < deadlineUtc)
			{
			cancellationToken.ThrowIfCancellationRequested ();
			Thread.Sleep (100);
			if (_shell.DataAvailable)
				{
				response.Append (_shell.Read ());
				lastResponseUtc = DateTime.UtcNow;
				promptReceived = ProcessorConsolePromptExpression.IsMatch (response.ToString ());
				}

			if (promptReceived && lastResponseUtc.HasValue && DateTime.UtcNow - lastResponseUtc.Value >= ConsoleResponseQuietPeriod)
				{
				return Task.FromResult (response.ToString ());
				}
			}

		throw new TimeoutException ($"Timed out waiting for processor console command '{command}' to complete. Partial response: '{response}'.");
		}

	private void EnsureConnected ()
		{
		if (_shell is not null && _client is not null && _client.IsConnected && DateTime.UtcNow - _connectedUtc < IdleSessionLifetime)
			{
			return;
			}

		Disconnect ();
		string host = ResolveProcessorSshHost ();
		string userName = _configuration.ProcessorSshUserName;
		string password = _configuration.ProcessorSshPassword;
		if (string.IsNullOrWhiteSpace (userName) || string.IsNullOrWhiteSpace (password))
			{
			throw new InvalidOperationException ("Processor SSH user name and password must be configured.");
			}

		_client = new SshClient (host, userName, password)
			{
			ConnectionInfo = { Timeout = CommandTimeout }
			};
		string[] firstHostKeyAlgorithms = _client.ConnectionInfo.HostKeyAlgorithms.Keys.ToArray ();
		string[] secondHostKeyAlgorithms = _client.ConnectionInfo.HostKeyAlgorithms.Keys.ToArray ();
		bool hostKeyAlgorithmSnapshotsMatch = firstHostKeyAlgorithms.SequenceEqual (secondHostKeyAlgorithms, StringComparer.Ordinal);
		_logInfo ($"Processor SSH host-key snapshots: first='{string.Join (",", firstHostKeyAlgorithms)}', second='{string.Join (",", secondHostKeyAlgorithms)}', ordinalSequenceEqual={hostKeyAlgorithmSnapshotsMatch}.");
		_logInfo ($"Processor SSH connecting to host='{host}', sshNetVersion='{typeof (SshClient).Assembly.GetName ().Version}', hostKeyAlgorithms='{string.Join (",", secondHostKeyAlgorithms)}'.");
		ConnectWithHardTimeout (_client, host);
		_shell = _client.CreateShellStream ("xterm", 80, 24, 800, 600, 1024);
		WaitForPrompt ("initial console connection");
		_shell.WriteLine ("enableprogramcmd");
		WaitForPrompt ("enableprogramcmd");
		_connectedUtc = DateTime.UtcNow;
		_loadIdsByName.Clear ();
		_baselineTuningModesByLoadId.Clear ();
		_logInfo ($"Processor SSH console connected to host='{host}'.");
		}

	// SshClient.Connect() is a single blocking call whose internal ConnectionInfo.Timeout does not
	// reliably fire if the TCP handshake completes but the SSH protocol handshake afterward stalls
	// (packets silently dropped, no RST/FIN received). No exception surfaces in that case, so the
	// call can hang indefinitely on its own. Race it on a background thread against a hard deadline;
	// if it doesn't complete in time, dispose the client to force the underlying socket closed,
	// which unblocks/faults the stuck call instead of leaking the thread and the connection forever.
	private void ConnectWithHardTimeout (SshClient client, string host)
		{
		var connectTask = Task.Run (() => client.Connect ());
		if (!connectTask.Wait (CommandTimeout))
			{
			_logError ($"Processor SSH connect to host='{host}' did not complete within {CommandTimeout}; forcing the connection closed.");
			try { client.Dispose (); } catch { }
			throw new TimeoutException ($"Timed out connecting to processor SSH host='{host}' after {CommandTimeout}.");
			}

		if (connectTask.IsFaulted)
			{
			throw connectTask.Exception!.GetBaseException ();
			}
		}

	private void WaitForPrompt (string operation)
		{
		DateTime deadlineUtc = DateTime.UtcNow + CommandTimeout;
		var response = new StringBuilder ();
		while (DateTime.UtcNow < deadlineUtc)
			{
			Thread.Sleep (100);
			if (_shell!.DataAvailable)
				{
				response.Append (_shell.Read ());
				if (ProcessorConsolePromptExpression.IsMatch (response.ToString ()))
						{
						return;
						}
				}
			}

		throw new TimeoutException ($"Timed out waiting for processor console prompt after {operation}. Partial response: '{response}'.");
		}

	private string ResolveProcessorSshHost ()
		{
		string configuredHost = _configuration.ProcessorSshHost.Trim ();
		if (configuredHost.Length > 0)
			{
			return configuredHost;
			}

		IPAddress? address = Dns.GetHostEntry (Dns.GetHostName ()).AddressList
			.FirstOrDefault (candidate => candidate.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback (candidate));
		if (address is null)
			{
			throw new InvalidOperationException ("Unable to determine a non-loopback IPv4 address for the processor. Configure Processor SSH Host explicitly.");
			}

		return address.ToString ();
		}

	private void Disconnect ()
		{
		_loadIdsByName.Clear ();
		_baselineTuningModesByLoadId.Clear ();
		try { _shell?.Dispose (); } catch { }
		try { if (_client?.IsConnected == true) { _client.Disconnect (); } } catch { }
		try { _client?.Dispose (); } catch { }
		_shell = null;
		_client = null;
		}

	public void Dispose ()
		{
		if (_disposed)
			{
			return;
			}

		_disposed = true;
		Disconnect ();
		_gate.Dispose ();
		}

	private sealed class ProcessorSshLoggerFactory : ILoggerFactory
		{
		private readonly Action<string> _logInfo;

		public ProcessorSshLoggerFactory (Action<string> logInfo)
			{
			_logInfo = logInfo;
			}

		public void AddProvider (ILoggerProvider provider)
			{
			provider.Dispose ();
			}

		public ILogger CreateLogger (string categoryName) => new ProcessorSshLogger (_logInfo, categoryName);

		public void Dispose ()
			{
			}
		}

	private sealed class ProcessorSshLogger : ILogger
		{
		private readonly Action<string> _logInfo;
		private readonly string _categoryName;

		public ProcessorSshLogger (Action<string> logInfo, string categoryName)
			{
			_logInfo = logInfo;
			_categoryName = categoryName;
			}

		public IDisposable? BeginScope<TState> (TState state) where TState : notnull => EmptyScope.Instance;

		public bool IsEnabled (LogLevel logLevel) => logLevel == LogLevel.Trace;

		public void Log<TState> (LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
			{
			string message = formatter (state, exception);
			if (message.IndexOf ("Host key algorithm:", StringComparison.OrdinalIgnoreCase) >= 0)
				{
				_logInfo ($"SSH.NET trace [{_categoryName}]: {message}");
				}
			}
		}

	private sealed class EmptyScope : IDisposable
		{
		public static readonly EmptyScope Instance = new ();

		public void Dispose ()
			{
			}
		}
	}
