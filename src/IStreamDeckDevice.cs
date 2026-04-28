namespace Haukcode.StreamDeck;

/// <summary>
/// Unified abstraction over a Stream Deck device regardless of transport (USB HID or Network Dock TCP).
/// </summary>
public interface IStreamDeckDevice : IAsyncDisposable
{
    /// <summary>Device model (MK2, XL, Plus, etc.). Unknown until connected for network devices.</summary>
    StreamDeckModel Model { get; }

    /// <summary>Number of physical LCD keys. 0 until activated for network devices.</summary>
    int KeyCount { get; }

    /// <summary>Per-key image width in pixels. 0 until activated for network devices.</summary>
    int KeyImageWidth { get; }

    /// <summary>Per-key image height in pixels. 0 until activated for network devices.</summary>
    int KeyImageHeight { get; }

    /// <summary>True for devices with rotary encoders (e.g. Stream Deck Plus).</summary>
    bool HasEncoders { get; }

    /// <summary>Number of rotary encoders. 0 for button-only models.</summary>
    int EncoderCount { get; }

    /// <summary>
    /// Hardware serial number reported by the device, e.g. "A00SA5202LJPCY".
    /// Available immediately for USB devices (read from the HID descriptor).
    /// For network devices it is null until <see cref="ConnectionState.Connected"/>
    /// is reached, since it is delivered in the dock's capabilities response.
    /// </summary>
    string? SerialNumber { get; }

    /// <summary>
    /// Per-key pressed/released state, emitted on every change.
    /// Array length = <see cref="KeyCount"/>; index = physical key number (top-left first).
    /// </summary>
    IObservable<bool[]> ButtonStates { get; }

    /// <summary>
    /// Per-encoder pressed/released state. Array length = <see cref="EncoderCount"/>.
    /// Empty for button-only models.
    /// </summary>
    IObservable<bool[]> EncoderPresses { get; }

    /// <summary>
    /// Per-encoder rotation deltas. Positive = clockwise, negative = counter-clockwise.
    /// Array length = <see cref="EncoderCount"/>.
    /// </summary>
    IObservable<sbyte[]> EncoderRotations { get; }

    /// <summary>Connection lifecycle state transitions.</summary>
    IObservable<ConnectionState> Connection { get; }

    /// <summary>
    /// Begin the connect / activate / receive cycle as a background task.
    /// Returns immediately; observe <see cref="Connection"/> for status.
    /// Calling twice is a no-op.
    /// </summary>
    void Start();

    /// <summary>
    /// Set the image on key <paramref name="slot"/>. The image is resized to
    /// <see cref="KeyImageWidth"/> × <see cref="KeyImageHeight"/> and JPEG-encoded.
    /// </summary>
    Task SetKeyImageAsync(int slot, Image<Rgba32> image, CancellationToken ct = default);

    /// <summary>
    /// Set the image on key <paramref name="slot"/> using pre-encoded bytes
    /// (JPEG at the device's native resolution). Use this overload when you
    /// have already encoded the image yourself.
    /// </summary>
    Task SetKeyImageAsync(int slot, byte[] encodedBytes, CancellationToken ct = default);

    /// <summary>Set the display brightness (0–100).</summary>
    Task SetBrightnessAsync(byte percent, CancellationToken ct = default);

    /// <summary>
    /// Blank all key images and set brightness to zero so the device returns
    /// to its built-in default screen once the connection is closed.
    /// Safe to call with no keys known (e.g. network device not yet activated).
    /// </summary>
    Task ResetAsync(CancellationToken ct = default);
}
