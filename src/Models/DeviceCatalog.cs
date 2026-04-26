namespace Haukcode.StreamDeck.Models;

/// <summary>
/// Per-model static specification for a Stream Deck device.
/// </summary>
/// <param name="Model">Enum identifier.</param>
/// <param name="Columns">Key grid column count.</param>
/// <param name="Rows">Key grid row count.</param>
/// <param name="KeyImageWidth">Per-key image width in pixels.</param>
/// <param name="KeyImageHeight">Per-key image height in pixels.</param>
/// <param name="ImageFormat">Wire encoding required for this model's key images.</param>
/// <param name="UsbImageRotate180">True when the USB HID transport needs images rotated 180° before sending.
/// Not applicable for the Network Dock (the dock handles orientation itself).</param>
/// <param name="EncoderCount">Number of rotary encoders (0 for button-only models).</param>
/// <param name="VendorId">USB vendor ID (0x0FD9 for all Elgato devices).</param>
/// <param name="ProductIds">All known USB product IDs for this model across hardware revisions.</param>
public sealed record DeviceInfo(
    StreamDeckModel Model,
    int Columns,
    int Rows,
    int KeyImageWidth,
    int KeyImageHeight,
    StreamDeckImageFormat ImageFormat,
    bool UsbImageRotate180,
    int EncoderCount,
    ushort VendorId,
    ushort[] ProductIds)
{
    public int KeyCount => Columns * Rows;
}

public enum StreamDeckImageFormat
{
    Jpeg,
    Bmp
}

/// <summary>
/// Compile-time catalog of all known Stream Deck models.
///
/// USB VID/PID values and key image dimensions sourced from
/// Julusian/node-elgato-stream-deck and Elgato's published HID API docs.
/// Network Dock image dimensions are reported at runtime via the capabilities
/// response (see <see cref="CapabilitiesEvent"/>); this catalog is used as
/// fallback / USB reference.
/// </summary>
public static class DeviceCatalog
{
    public const ushort ElgatoVendorId = 0x0FD9;

    private static readonly DeviceInfo[] AllDevices =
    [
        // Stream Deck MK.2 — 5×3 JPEG 72×72.
        // PIDs: 0x006D (older), 0x0080 (live-captured), 0x00B8/0x00B9 (current).
        // USB images do not need rotation for MK.2 per Julusian's node-elgato-stream-deck.
        new(StreamDeckModel.MK2,
            Columns: 5, Rows: 3,
            KeyImageWidth: 72, KeyImageHeight: 72,
            ImageFormat: StreamDeckImageFormat.Jpeg,
            UsbImageRotate180: false,
            EncoderCount: 0,
            VendorId: ElgatoVendorId,
            ProductIds: [0x006D, 0x0080, 0x00B8, 0x00B9]),

        // Stream Deck XL — 8×4 JPEG 96×96.
        new(StreamDeckModel.XL,
            Columns: 8, Rows: 4,
            KeyImageWidth: 96, KeyImageHeight: 96,
            ImageFormat: StreamDeckImageFormat.Jpeg,
            UsbImageRotate180: false,
            EncoderCount: 0,
            VendorId: ElgatoVendorId,
            ProductIds: [0x006C, 0x008F]),

        // Stream Deck Mini Mk.2 — 3×2 BMP 80×80 with 90° rotation.
        // USB transport is not yet implemented for BMP models.
        new(StreamDeckModel.MiniMK2,
            Columns: 3, Rows: 2,
            KeyImageWidth: 80, KeyImageHeight: 80,
            ImageFormat: StreamDeckImageFormat.Bmp,
            UsbImageRotate180: false,
            EncoderCount: 0,
            VendorId: ElgatoVendorId,
            ProductIds: [0x0063, 0x0090]),

        // Stream Deck Plus — 4×2 JPEG 120×120 + 4 rotary encoders.
        new(StreamDeckModel.Plus,
            Columns: 4, Rows: 2,
            KeyImageWidth: 120, KeyImageHeight: 120,
            ImageFormat: StreamDeckImageFormat.Jpeg,
            UsbImageRotate180: false,
            EncoderCount: 4,
            VendorId: ElgatoVendorId,
            ProductIds: [0x0084]),

        // Stream Deck Studio — 8×4 JPEG 96×96 + 6 rotary encoders.
        new(StreamDeckModel.Studio,
            Columns: 8, Rows: 4,
            KeyImageWidth: 96, KeyImageHeight: 96,
            ImageFormat: StreamDeckImageFormat.Jpeg,
            UsbImageRotate180: false,
            EncoderCount: 6,
            VendorId: ElgatoVendorId,
            ProductIds: [0x00A9]),
    ];

    /// <summary>Look up by USB product ID. Returns null for unrecognised PIDs.</summary>
    public static DeviceInfo? GetByPid(ushort pid)
    {
        foreach (var d in AllDevices)
            foreach (var p in d.ProductIds)
                if (p == pid) return d;
        return null;
    }

    /// <summary>Look up by model name string as reported in the Network Dock capabilities response.</summary>
    public static DeviceInfo? GetByModelName(string? name) => name switch
    {
        "Stream Deck MK.2"  => AllDevices[0],
        "Stream Deck XL"    => AllDevices[1],
        "Stream Deck Mini"  => AllDevices[2],
        "Stream Deck +"     => AllDevices[3],
        "Stream Deck Studio" => AllDevices[4],
        _ => null
    };

    /// <summary>All known device records.</summary>
    public static IReadOnlyList<DeviceInfo> All => AllDevices;
}
