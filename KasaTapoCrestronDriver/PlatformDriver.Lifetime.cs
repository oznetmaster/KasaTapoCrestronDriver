// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System.Collections.Concurrent;

namespace KasaTapoCrestronDriver;

public sealed partial class PlatformDriver
	{
	private readonly ConcurrentDictionary<Task, byte> _runtimeTasks = new ();

	private Task RunRuntimeTask (Func<Task> work, CancellationToken cancellationToken = default)
		{
		lock (_disposeGate)
			{
			if (_disposed || _runtimeCancellationSource.IsCancellationRequested)
				return Task.FromCanceled (_runtimeCancellationSource.Token);
			Task task = Task.Run (work, cancellationToken);
			TrackRuntimeTask (task);
			return task;
			}
		}

	private Task<T> RunRuntimeResult<T> (Func<T> work, CancellationToken cancellationToken)
		{
		lock (_disposeGate)
			{
			if (_disposed || _runtimeCancellationSource.IsCancellationRequested)
				return Task.FromCanceled<T> (_runtimeCancellationSource.Token);
			Task<T> task = Task.Run (work, cancellationToken);
			TrackRuntimeTask (task);
			return task;
			}
		}

	private void TrackRuntimeTask (Task task)
		{
		_runtimeTasks[task] = 0;
		_ = task.ContinueWith (completed =>
			{
				_runtimeTasks.TryRemove (completed, out _);
				_ = completed.Exception;
			}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
		}
	}