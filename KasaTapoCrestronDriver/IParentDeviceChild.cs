// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

namespace KasaTapoCrestronDriver;

// One registered projection of a physical parent. Registration is by instance, so replacing
// an outlet with a light cannot let the old entity unregister its replacement.
internal interface IParentDeviceChild
	{
	string ChildId
		{
		get;
		}
	void ApplyPushedState (ChildDevice? child, KasaDevice parentDevice);
	void ApplyConnectionState (bool online);
	}