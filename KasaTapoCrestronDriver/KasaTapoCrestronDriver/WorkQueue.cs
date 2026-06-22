using System;
using System.Threading;
using System.Threading.Tasks;

namespace KasaTapoCrestronDriver;

internal sealed class WorkQueue<TClient> : IDisposable
	where TClient : class
	{
	private readonly SemaphoreSlim _gate = new (1, 1);
	private volatile bool _stopped;
	private bool _disposed;
	private TClient? _client;

	/// <summary>
	/// Sets the active client instance used by queued work items.
	/// </summary>
	/// <param name="client">The client instance that subsequent work items should use.</param>
	public void SetClient (TClient client)
		{
		_client = client ?? throw new ArgumentNullException (nameof (client));
		}

	/// <summary>
	/// Clears the active client instance.
	/// </summary>
	public void ClearClient ()
		{
		_client = null;
		}

	/// <summary>
	/// Stops the queue from accepting additional work and clears the current client.
	/// </summary>
	public void Stop ()
		{
		_stopped = true;
		_client = null;
		}

	public void Dispose ()
		{
		if (_disposed)
			{
			return;
			}

		Stop ();
		_gate.Dispose ();
		_disposed = true;
		}

	/// <summary>
	/// Queues a unit of asynchronous work to run serially against the current client instance.
	/// </summary>
	/// <param name="work">The asynchronous delegate to invoke for the current client.</param>
	/// <returns>A task that completes when the queued work has finished.</returns>
	public async Task EnqueueAsync (Func<TClient, Task> work)
		{
		if (_disposed)
			{
			return;
			}

		if (_stopped)
			{
			return;
			}

		if (work == null)
			{
			throw new ArgumentNullException (nameof (work));
			}

		await _gate.WaitAsync ().ConfigureAwait (false);
		try
			{
			if (_disposed)
				{
				return;
				}

			if (_stopped)
				{
				return;
				}

			var client = _client ?? throw new InvalidOperationException ("Client instance is required before work can be queued.");
			await work (client).ConfigureAwait (false);
			}
		finally
			{
			_ = _gate.Release ();
			}
		}
	}