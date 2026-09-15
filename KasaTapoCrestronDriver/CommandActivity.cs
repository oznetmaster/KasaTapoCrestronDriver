// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

namespace KasaTapoCrestronDriver;

// Published as one property so observers never see mismatched pending/completed counters.
internal sealed class CommandActivity
	{
	private readonly object _sync = new ();
	private readonly string _epoch = Guid.NewGuid ().ToString ("N");
	private long _completed;
	private int _pending;
	public string Snapshot
		{
		get
			{
			lock (_sync)
				return "{\"Epoch\":\"" + _epoch + "\",\"Completed\":" + _completed.ToString (System.Globalization.CultureInfo.InvariantCulture)
					+ ",\"Pending\":" + _pending.ToString (System.Globalization.CultureInfo.InvariantCulture) + "}";
			}
		}
	public void Begin ()
		{
		lock (_sync)
			checked
				{
				_pending++;
				}
		}
	public void Complete ()
		{
		lock (_sync)
			{
			if (_pending <= 0)
				throw new InvalidOperationException ("No command is pending.");
			checked
				{
				_completed++;
				}
			_pending--;
			}
		}
	}