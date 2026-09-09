// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the project root.

namespace KasaTapoCrestronDriver;

internal static class HubChildCategoryResolver
	{
	// Recover categories for known models in caches written before HubChildCategory
	// was persisted. Unknown models must continue to use declared device features.
	internal static HubChildCategory FromModel (string? model)
		{
		string normalized = (model ?? string.Empty).Trim ();
		int suffix = normalized.IndexOf ('(');
		if (suffix >= 0)
			{
			normalized = normalized.Substring (0, suffix).Trim ();
			}

		switch (normalized.ToUpperInvariant ())
			{
			case "T100": return HubChildCategory.Motion;
			case "T110": return HubChildCategory.Contact;
			case "T310":
			case "T315": return HubChildCategory.TemperatureHumidity;
			default: return HubChildCategory.None;
			}
		}
	}
