namespace KasaTapoCrestronDriver;

internal static class StripOutletControl
{
    internal static bool ReadIsOn(KasaDevice device, string? childId) =>
        string.IsNullOrWhiteSpace(childId) ? device.IsOn ?? false : device.GetChild(childId!)?.IsOn ?? false;

    internal static Task SetIsOnAsync(KasaDevice device, string? childId, bool on, CancellationToken token) =>
        string.IsNullOrWhiteSpace(childId)
            ? (on ? device.TurnOnAsync(token) : device.TurnOffAsync(token))
            : (on ? device.TurnChildOnAsync(childId!, token) : device.TurnChildOffAsync(childId!, token));
}
