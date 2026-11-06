// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
namespace KasaTapoCrestronDriver;

public sealed partial class PlatformDriver
{
    private void SaveVerifiedOutletConnection(ManagedLightDescriptor descriptor, KasaDevice device)
    {
        if (_disposed || !string.IsNullOrEmpty(descriptor.ChildId)
            || !_managedDeviceCacheMetadata.TryGetValue(descriptor.ControllerId, out var entry)
            || string.IsNullOrWhiteSpace(entry.DiscoveryDeviceId)
            || !string.Equals(entry.DiscoveryDeviceId, descriptor.DiscoveryDeviceId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(entry.DiscoveryDeviceId, device.SystemInfo?.DeviceId, StringComparison.OrdinalIgnoreCase))
            return;

        // Use the configuration of the successful connection, not the advertised or
        // attempted protocol. Updating it is independent of name/UX publication.
        DeviceConfiguration config = device.Configuration;
        DeviceConnectionOptions options = config.ConnectionOptions;
        DeviceConnectionParameters? protocol = options.ConnectionParameters;
        if (protocol is null) return;
        long? keepAlive = options.TpapKeepAliveInterval is TimeSpan interval ? (long)interval.TotalMilliseconds : null;
        lock (entry)
        {
            if (string.Equals(entry.Host, config.Host, StringComparison.OrdinalIgnoreCase)
                && entry.Port == config.Port && entry.TransportKind == options.TransportKind
                && entry.DeviceFamily == protocol.DeviceFamily && entry.EncryptionKind == protocol.EncryptionKind
                && entry.LoginVersion == protocol.LoginVersion && entry.UseHttps == protocol.UseHttps
                && entry.HttpPort == protocol.HttpPort && entry.UseSsl == options.UseSsl
                && entry.UseDefaultCredentials == options.UseDefaultCredentials
                && entry.DefaultCredentialProfile == options.DefaultCredentialProfile
                && entry.ApplicationPath == options.ApplicationPath
                && entry.UseSecurePassthrough == options.UseSecurePassthrough
                && entry.TpapKeepAliveIntervalMs == keepAlive) return;

            // Replace the reconnect fields as one snapshot. The configured-child
            // identity, name and placement remain unchanged; no credentials are written.
            var previous = entry.Mutable;
            entry.Mutable = new ManagedDeviceMutableCacheFields
            {
                Name = previous.Name,
                AwaitingConnectedIdentity = previous.AwaitingConnectedIdentity,
                IsConfigured = previous.IsConfigured,
                TreatAsLight = previous.TreatAsLight,
                Host = config.Host, Port = config.Port, TransportKind = options.TransportKind,
                DeviceFamily = protocol.DeviceFamily, EncryptionKind = protocol.EncryptionKind,
                LoginVersion = protocol.LoginVersion, UseHttps = protocol.UseHttps, HttpPort = protocol.HttpPort,
                UseSsl = options.UseSsl, UseDefaultCredentials = options.UseDefaultCredentials,
                DefaultCredentialProfile = options.DefaultCredentialProfile, ApplicationPath = options.ApplicationPath,
                UseSecurePassthrough = options.UseSecurePassthrough, TpapKeepAliveIntervalMs = keepAlive
            };
        }
        PersistManagedDeviceCache();
    }
}
