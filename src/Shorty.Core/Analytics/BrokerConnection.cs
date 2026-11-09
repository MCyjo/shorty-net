namespace Shorty.Core.Analytics;

/// <summary>
/// The setting for Event Hubs and blob storage is either a connection string (local development, the emulator) or just an address,
/// in which case the application signs in with its managed identity and no secret exists at all.
/// </summary>
public static class BrokerConnection
{
    /// <summary>An Event Hubs connection string contains "Endpoint="; a plain namespace address does not.</summary>
    public static bool IsEventHubsConnectionString(string setting) =>
        setting.Contains("Endpoint=", StringComparison.OrdinalIgnoreCase);

    /// <summary>A blob storage setting is an address when it starts with https://; otherwise it is a connection string (Azurite, an account key).</summary>
    public static bool IsBlobAddress(string setting) =>
        setting.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
