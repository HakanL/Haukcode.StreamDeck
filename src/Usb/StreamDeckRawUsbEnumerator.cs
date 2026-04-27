using System.Globalization;
using System.Runtime.InteropServices;

namespace Haukcode.StreamDeck.Usb;

/// <summary>
/// Enumerates Stream Deck devices on Linux using sysfs and <c>/dev/bus/usb</c>.
///
/// Intended as a fallback when the hidraw interface is not accessible —
/// for example in a strict Snap package where only the <c>raw-usb</c>
/// interface is connected. The <c>raw-usb</c> snap interface grants
/// read/write access to <c>/dev/bus/usb/**</c> and read access to
/// <c>/sys/bus/usb/devices/**</c>, which is everything this enumerator needs.
/// </summary>
internal static class StreamDeckRawUsbEnumerator
{
    private const string SysBusUsb = "/sys/bus/usb/devices";

    /// <summary>
    /// Enumerate all connected Stream Deck devices accessible via raw USB.
    /// Returns an empty sequence on non-Linux platforms or when sysfs is
    /// unavailable.
    /// </summary>
    public static IEnumerable<StreamDeckRawUsbDevice> Enumerate(ILogger? logger = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            yield break;

        if (!Directory.Exists(SysBusUsb))
            yield break;

        string[] deviceDirs;
        try
        {
            deviceDirs = Directory.GetDirectories(SysBusUsb);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Cannot read {SysBusUsb}: {Message}", SysBusUsb, ex.Message);
            yield break;
        }

        foreach (var deviceDir in deviceDirs)
        {
            // Interface directories contain ':' in the name — skip them here.
            string name = Path.GetFileName(deviceDir);
            if (name.Contains(':'))
                continue;

            string vendorPath = Path.Combine(deviceDir, "idVendor");
            string productPath = Path.Combine(deviceDir, "idProduct");
            if (!File.Exists(vendorPath) || !File.Exists(productPath))
                continue;

            if (!TryReadHex(vendorPath, out ushort vendorId) || vendorId != DeviceCatalog.ElgatoVendorId)
                continue;

            if (!TryReadHex(productPath, out ushort productId))
                continue;

            var deviceInfo = DeviceCatalog.GetByPid(productId);
            if (deviceInfo == null)
                continue;

            string busnumPath = Path.Combine(deviceDir, "busnum");
            string devnumPath = Path.Combine(deviceDir, "devnum");
            if (!TryReadInt(busnumPath, out int busNum) || !TryReadInt(devnumPath, out int devNum))
                continue;

            string usbDevPath = $"/dev/bus/usb/{busNum:D3}/{devNum:D3}";
            if (!File.Exists(usbDevPath))
                continue;

            // Serial number is readily available from sysfs (no USB descriptor read needed).
            string? serial = TryReadText(Path.Combine(deviceDir, "serial"));

            var (ifaceNum, epIn, epOut) = FindHidInterfaceEndpoints(deviceDir);
            if (epIn == 0)
            {
                logger?.LogDebug(
                    "StreamDeckRawUsb: no HID interrupt IN endpoint found for {DevPath} — skipping",
                    usbDevPath);
                continue;
            }

            logger?.LogDebug(
                "StreamDeckRawUsb: found {Model} at {DevPath} (iface={Iface} epIn=0x{EpIn:X2} epOut=0x{EpOut:X2})",
                deviceInfo.Model, usbDevPath, ifaceNum, epIn, epOut);

            yield return new StreamDeckRawUsbDevice(
                usbDevPath, deviceInfo, serial, ifaceNum, epIn, epOut,
                logger ?? NullLogger.Instance);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Walk the interface sub-directories of a USB device's sysfs path to
    /// find the first HID class interface and its interrupt IN/OUT endpoints.
    /// </summary>
    private static (int ifaceNum, byte epIn, byte epOut) FindHidInterfaceEndpoints(string deviceDir)
    {
        string[] ifaceDirs;
        try
        {
            ifaceDirs = Directory.GetDirectories(deviceDir);
        }
        catch
        {
            return (0, 0, 0);
        }

        foreach (var ifaceDir in ifaceDirs)
        {
            // Interface directories are named "{dev}:{config}.{iface}", e.g. "1-2:1.0".
            if (!Path.GetFileName(ifaceDir).Contains(':'))
                continue;

            // Filter to HID class (class code 3).
            if (!TryReadHex(Path.Combine(ifaceDir, "bInterfaceClass"), out ushort ifaceClass) || ifaceClass != 3)
                continue;

            TryReadHex(Path.Combine(ifaceDir, "bInterfaceNumber"), out ushort ifaceNum);

            byte epIn = 0, epOut = 0;

            string[] epDirs;
            try { epDirs = Directory.GetDirectories(ifaceDir); }
            catch { continue; }

            foreach (var epDir in epDirs)
            {
                string epName = Path.GetFileName(epDir);
                if (!epName.StartsWith("ep_", StringComparison.OrdinalIgnoreCase) || epName == "ep_00")
                    continue;

                string? type = TryReadText(Path.Combine(epDir, "type"));
                if (!string.Equals(type, "Interrupt", StringComparison.OrdinalIgnoreCase))
                    continue;

                // ep_81 → 0x81, ep_02 → 0x02
                string hexPart = epName[3..];
                if (!byte.TryParse(hexPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte epAddr))
                    continue;

                string? dir = TryReadText(Path.Combine(epDir, "direction"));
                if (string.Equals(dir, "in", StringComparison.OrdinalIgnoreCase))
                    epIn = epAddr;
                else if (string.Equals(dir, "out", StringComparison.OrdinalIgnoreCase))
                    epOut = epAddr;
            }

            if (epIn != 0)
                return ((int)ifaceNum, epIn, epOut);
        }

        return (0, 0, 0);
    }

    private static bool TryReadHex(string path, out ushort value)
    {
        value = 0;
        string? text = TryReadText(path);
        return text != null && ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadInt(string path, out int value)
    {
        value = 0;
        string? text = TryReadText(path);
        return text != null && int.TryParse(text, out value);
    }

    private static string? TryReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }
}
