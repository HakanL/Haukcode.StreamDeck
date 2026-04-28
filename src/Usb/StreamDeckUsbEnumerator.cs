using HidApi;
using System.Runtime.InteropServices;

namespace Haukcode.StreamDeck.Usb;

/// <summary>
/// Enumerates Stream Deck devices attached via USB HID.
///
/// Backed by HidApi.Net (P/Invoke wrapper around the C hidapi library).
/// Native hidapi binaries for all supported RIDs are bundled into the NuGet
/// package under <c>runtimes/&lt;rid&gt;/native/</c> so consumers do not need
/// to install hidapi separately. See <c>docs/usb-hid-library-choice.md</c>
/// for the rationale.
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
        var log = logger ?? NullLogger.Instance;

        foreach (var info in DeviceCatalog.All)
        {
            foreach (var pid in info.ProductIds)
            {
                IEnumerable<HidApi.DeviceInfo> hidDevices;
                try
                {
                    hidDevices = Hid.Enumerate(info.VendorId, pid);
                }
                catch (DllNotFoundException ex)
                {
                    // Native hidapi shared library not loadable. With the bundled
                    // binaries this should not happen on supported RIDs; if it
                    // does, fall through after logging once and abort enumeration.
                    log.LogWarning(ex,
                        "Native hidapi library not found. USB HID enumeration is unavailable. " +
                        "Supported RIDs: win-x64, win-x86, linux-x64, linux-arm64, osx-x64, osx-arm64.");
                    yield break;
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "Hid.Enumerate failed for VID=0x{Vid:X4} PID=0x{Pid:X4}: {Message}",
                        info.VendorId, pid, ex.Message);
                    continue;
                }

                foreach (var hidDevice in hidDevices)
                {
                    // On Linux (especially strict Snap), HID enumeration can succeed
                    // even when opening /dev/hidraw* is denied. Skip such devices so
                    // StreamDeckLocator can fall back to raw-usb.
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        try
                        {
                            using var probe = hidDevice.ConnectToDevice();
                        }
                        catch (Exception ex)
                        {
                            log.LogInformation(
                                ex,
                                "Skipping USB HID candidate model={Model} pid=0x{Pid:X4} serial={Serial} because opening HID failed: {Message}",
                                info.Model,
                                pid,
                                string.IsNullOrEmpty(hidDevice.SerialNumber) ? "<none>" : hidDevice.SerialNumber,
                                ex.Message);
                            continue;
                        }
                    }

                    log.LogInformation(
                        "USB HID candidate found: model={Model} pid=0x{Pid:X4} serial={Serial}",
                        info.Model,
                        pid,
                        string.IsNullOrEmpty(hidDevice.SerialNumber) ? "<none>" : hidDevice.SerialNumber);
                    yield return new StreamDeckUsbDevice(hidDevice, info, log);
                }
            }
        }
    }
}
