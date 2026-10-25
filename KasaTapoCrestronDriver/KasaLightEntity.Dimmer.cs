// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the project root.

namespace KasaTapoCrestronDriver;

internal partial class KasaLightEntity
    {
    private static Task SetLightPowerAsync (KasaDevice device, bool on, CancellationToken cancellationToken)
        {
        // SMART wall dimmers retain brightness independently of power. The client's light-specific
        // power API accepts bulbs/strips only; its general API already selects the dimmer's
        // protocol command. Keep bulbs, strips and all other device types on their current
        // light API, with no brightness or color payload changes.
        if (device.DeviceType == KasaDeviceType.Dimmer)
            {
            return on ? device.TurnOnAsync (cancellationToken) : device.TurnOffAsync (cancellationToken);
            }

        return on ? device.TurnLightOnAsync (cancellationToken) : device.TurnLightOffAsync (cancellationToken);
        }
    }
