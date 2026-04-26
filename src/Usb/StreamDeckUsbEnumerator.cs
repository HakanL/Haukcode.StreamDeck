using HidApi;

namespace Haukcode.StreamDeck.Usb;

/// <summary>
/// Enumerates Stream Deck devices attached via USB HID.
/// </summary>
public static class StreamDeckUsbEnumerator
{
    /// <summary>
    /// Return all Stream Deck devices currently attached via USB.
    /// Devices with unsupported image formats (e.g. BMP-only Mini Mk.2)
    /// are included in the enumeration but their USB transport is limited
    /// — check <see cref="Models.DeviceInfo.ImageFormat"/>.
    /// </summary>
    public static IEnumerable<StreamDeckUsbDevice> Enumerate(ILogger? logger = null)
    {
        foreach (var info in DeviceCatalog.All)
        {
            foreach (var pid in info.ProductIds)
            {
                IEnumerable<HidApi.DeviceInfo> hidDevices;
                try
                {
                    hidDevices = Hid.Enumerate(info.VendorId, pid);
                }
                catch
                {
                    continue;
                }

                foreach (var hidDevice in hidDevices)
                {
                    yield return new StreamDeckUsbDevice(hidDevice, info, logger ?? NullLogger.Instance);
                }
            }
        }
    }
}
