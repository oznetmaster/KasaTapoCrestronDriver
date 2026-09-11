// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace KasaTapoCrestronDriver.Tests;

internal static class TestTaskExtensions
	{
	internal static async Task WaitForTestAsync (this Task task, TimeSpan timeout)
		{
		using var deadline = new CancellationTokenSource ();
		Task delay = Task.Delay (timeout, deadline.Token);
		if (await Task.WhenAny (task, delay).ConfigureAwait (false) != task)
			throw new TimeoutException ("Test operation did not complete before its deadline.");
		deadline.Cancel ();
		await task.ConfigureAwait (false);
		}

	internal static async Task WaitForTestAsync (this Task task, CancellationToken cancellationToken)
		{
		cancellationToken.ThrowIfCancellationRequested ();
		var canceled = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
		using (cancellationToken.Register (() => canceled.TrySetCanceled ()))
			{
			Task completed = await Task.WhenAny (task, canceled.Task).ConfigureAwait (false);
			await completed.ConfigureAwait (false);
			}
		}
	}