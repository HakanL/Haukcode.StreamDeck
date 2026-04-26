namespace Haukcode.StreamDeck.Network;

/// <summary>
/// A discovered Elgato Network Dock on the local network.
/// Obtained from <see cref="StreamDeckNetworkDiscovery.ResolveAsync"/> or
/// <see cref="StreamDeckNetworkDiscovery.DocksFound"/>.
/// Call <see cref="CreateDevice"/> to get an <see cref="IStreamDeckDevice"/>
/// backed by the CORA TCP protocol.
/// </summary>
public sealed class StreamDeckNetworkDock
{
    /// <summary>mDNS instance name of the dock (e.g. "Elgato Stream Deck Studio").</summary>
    public string Name { get; }

    /// <summary>IPv4 host address of the dock.</summary>
    public string Host { get; }

    /// <summary>Primary TCP port (default 5343).</summary>
    public int PrimaryPort { get; }

    public StreamDeckNetworkDock(string name, string host, int primaryPort = 5343)
    {
        Name = name;
        Host = host;
        PrimaryPort = primaryPort;
    }

    /// <summary>
    /// Create an <see cref="IStreamDeckDevice"/> that connects to the USB
    /// Stream Deck attached to this dock via the CORA TCP protocol.
    /// Call <see cref="IStreamDeckDevice.Start"/> to begin the connection cycle.
    /// </summary>
    public StreamDeckNetworkDevice CreateDevice(ILogger? logger = null, byte initialBrightness = 80)
        => new(logger ?? NullLogger.Instance, Host, PrimaryPort, initialBrightness);

    public override string ToString() => $"{Name} @ {Host}:{PrimaryPort}";
}
