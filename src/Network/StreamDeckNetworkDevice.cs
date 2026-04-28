using Haukcode.StreamDeck.Imaging;

namespace Haukcode.StreamDeck.Network;

/// <summary>
/// <see cref="IStreamDeckDevice"/> implementation backed by a Network Dock TCP connection.
/// Wraps <see cref="StreamDeckNetworkClient"/> and exposes the unified interface.
/// </summary>
public sealed class StreamDeckNetworkDevice : IStreamDeckDevice
{
    private readonly StreamDeckNetworkClient client;

    public StreamDeckNetworkDevice(string host, int primaryPort = 5343, byte initialBrightness = 80)
        : this(NullLogger.Instance, host, primaryPort, initialBrightness) { }

    public StreamDeckNetworkDevice(ILogger logger, string host, int primaryPort = 5343, byte initialBrightness = 80)
    {
        this.client = new StreamDeckNetworkClient(logger, host, primaryPort, initialBrightness);
    }

    // -------------------------------------------------------------------------
    // IStreamDeckDevice
    // -------------------------------------------------------------------------

    public StreamDeckModel Model =>
        DeviceCatalog.GetByModelName(this.client.Model)?.Model ?? StreamDeckModel.Unknown;

    public int KeyCount => this.client.KeyCount;
    public int KeyImageWidth => this.client.KeyImageWidth > 0 ? this.client.KeyImageWidth : 72;
    public int KeyImageHeight => this.client.KeyImageHeight > 0 ? this.client.KeyImageHeight : 72;

    public bool HasEncoders => DeviceCatalog.GetByModelName(this.client.Model)?.EncoderCount > 0;
    public int EncoderCount => DeviceCatalog.GetByModelName(this.client.Model)?.EncoderCount ?? 0;

    public string? SerialNumber => this.client.Serial;

    public IObservable<bool[]> ButtonStates => this.client.ButtonStates;
    public IObservable<bool[]> EncoderPresses => this.client.EncoderPresses;
    public IObservable<sbyte[]> EncoderRotations => this.client.EncoderRotations;

    public IObservable<ConnectionState> Connection =>
        this.client.ConnectionState.Select(MapConnectionState);

    public void Start() => this.client.Start();

    public Task SetKeyImageAsync(int slot, Image<Rgba32> image, CancellationToken ct = default)
    {
        var jpegBytes = KeyImageEncoder.EncodeJpeg(image, KeyImageWidth, KeyImageHeight);
        return this.client.SetKeyImageAsync(slot, jpegBytes, ct);
    }

    public Task SetKeyImageAsync(int slot, byte[] encodedBytes, CancellationToken ct = default)
        => this.client.SetKeyImageAsync(slot, encodedBytes, ct);

    public Task SetBrightnessAsync(byte percent, CancellationToken ct = default)
        => this.client.SetBrightnessAsync(percent, ct);

    public Task ResetAsync(CancellationToken ct = default)
        => this.client.ResetAsync(ct);

    public ValueTask DisposeAsync() => this.client.DisposeAsync();

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ConnectionState MapConnectionState(StreamDeckNetworkConnectionState s) => s switch
    {
        StreamDeckNetworkConnectionState.Connecting  => ConnectionState.Connecting,
        StreamDeckNetworkConnectionState.Activating  => ConnectionState.Activating,
        StreamDeckNetworkConnectionState.Connected   => ConnectionState.Connected,
        _                                            => ConnectionState.Disconnected
    };
}
