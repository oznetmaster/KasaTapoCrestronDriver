// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using Newtonsoft.Json.Linq;

namespace KasaTapoCrestronDriver.Tests;

#if NETFRAMEWORK
[Category ("Processor")]
#endif
[TestFixture]
public sealed class CommandActivityTests
	{
	[Test]
	public void CompletionIncludesEveryOutstandingOperation ()
		{
		var activity = new CommandActivity ();
		string epoch = (string)JObject.Parse (activity.Snapshot)["Epoch"]!;
		activity.Begin ();
		activity.Begin ();
		activity.Complete ();
		var busy = JObject.Parse (activity.Snapshot);
		Assert.That ((int)busy["Pending"]!, Is.EqualTo (1));
		Assert.That ((long)busy["Completed"]!, Is.EqualTo (1));
		activity.Complete ();
		var idle = JObject.Parse (activity.Snapshot);
		Assert.That ((int)idle["Pending"]!, Is.Zero);
		Assert.That ((long)idle["Completed"]!, Is.EqualTo (2));
		Assert.That ((string)idle["Epoch"]!, Is.EqualTo (epoch));
		Assert.That ((string)JObject.Parse (new CommandActivity ().Snapshot)["Epoch"]!, Is.Not.EqualTo (epoch));
		Assert.Throws<InvalidOperationException> (activity.Complete);
		}

	[Test]
	public void ParallelCommandsKeepAnAtomicSnapshot ()
		{
		var activity = new CommandActivity ();
		Parallel.For (0, 100, _ => { activity.Begin (); activity.Complete (); });
		var final = JObject.Parse (activity.Snapshot);
		Assert.That ((int)final["Pending"]!, Is.Zero);
		Assert.That ((long)final["Completed"]!, Is.EqualTo (100));
		}
	}