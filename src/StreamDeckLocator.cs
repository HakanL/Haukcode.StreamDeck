using System.Runtime.InteropServices;
using Haukcode.StreamDeck.Network;
using Haukcode.StreamDeck.Usb;

namespace Haukcode.StreamDeck;

/// <summary>
/// Discovers and connects to Stream Deck devices regardless of transport.
///
/// USB devices are returned immediately (synchronous HID enumeration).
/// On Linux, when HID enumeration finds no devices, a raw USB fallback is
/// attempted automatically via <c>/dev/bus/usb</c> — compatible with the
/// Snap <c>raw-usb</c> interface.
/// Network devices require an mDNS scan (asynchronous, default 3 s window).
///
/// Simple one-liner:
/// <code>
/// var device = await StreamDeckLocator.FindFirstAsync();
/// </code>
/// </summary>
public static class StreamDeckLocator
{
    /// <summary>
    /// Return the first available Stream Deck device, checking USB before network.
    /// The returned device is <b>not yet started</b> — call
    /// <see cref="IStreamDeckDevice.Start"/> and then subscribe to observables.
    /// </summary>
    /// <param name="includeUsb">Enumerate USB HID devices.</param>
    /// <param name="includeNetwork">Scan for Network Docks via mDNS.</param>
    /// <param name="networkScanTime">How long to wait for mDNS responses. Default 3 s.</param>
    /// <param name="logger">Optional logger injected into the created device.</param>
    public static async Task<IStreamDeckDevice?> FindFirstAsync(
        bool includeUsb = true,
        bool includeNetwork = true,
        TimeSpan? networkScanTime = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (includeUsb)
        {
            var usb = EnumerateUsb(logger).FirstOrDefault();
            if (usb != null) return usb;
        }

        if (includeNetwork)
        {
            var docks = await StreamDeckNetworkDiscovery.ResolveAsync(networkScanTime, ct)
                .ConfigureAwait(false);

            if (docks.Count > 0)
                return docks[0].CreateDevice(logger);
        }

        return null;
    }

    /// <summary>
    /// Return all USB Stream Deck devices currently attached.
    ///
    /// On Linux, if the HID enumerator finds no devices (e.g. because the
    /// hidraw interface is not accessible in a strict Snap), a raw USB
    /// fallback via <c>/dev/bus/usb</c> is attempted automatically.
    /// The fallback requires the <c>raw-usb</c> snap interface to be connected.
    /// </summary>
    public static IEnumerable<IStreamDeckDevice> EnumerateUsb(ILogger? logger = null)
    {
        var hidDevices = StreamDeckUsbEnumerator.Enumerate(logger).ToList<IStreamDeckDevice>();
        if (hidDevices.Count > 0)
        {
            logger?.LogInformation(
                "Using USB HID transport for {Count} Stream Deck device(s).",
                hidDevices.Count);
            return hidDevices;
        }

        // On Linux, fall back to raw USB when HID finds nothing (e.g. Snap without hidraw).
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            logger?.LogInformation("USB HID enumeration found no Stream Deck devices; trying raw USB transport.");
            var rawDevices = StreamDeckRawUsbEnumerator.Enumerate(logger).ToList<IStreamDeckDevice>();
            if (rawDevices.Count > 0)
            {
                logger?.LogInformation(
                    "Using raw USB transport (raw-usb) for {Count} Stream Deck device(s).",
                    rawDevices.Count);
                return rawDevices;
            }

            logger?.LogInformation("Raw USB enumeration also found no Stream Deck devices.");
        }

        return hidDevices; // empty
    }

    /// <summary>
    /// Explicitly enumerate Stream Deck devices via raw USB (<c>/dev/bus/usb</c>)
    /// without attempting HID. Useful on Linux when the hidraw interface is
    /// unavailable. Returns an empty sequence on non-Linux platforms.
    /// </summary>
    public static IEnumerable<IStreamDeckDevice> EnumerateLinuxRawUsb(ILogger? logger = null)
        => StreamDeckRawUsbEnumerator.Enumerate(logger);

    /// <summary>
    /// Perform a one-shot mDNS scan and return all Network Dock devices found.
    /// Each returned device is not yet started.
    /// </summary>
    public static async Task<IReadOnlyList<IStreamDeckDevice>> FindNetworkDevicesAsync(
        TimeSpan? scanTime = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var docks = await StreamDeckNetworkDiscovery.ResolveAsync(scanTime, ct).ConfigureAwait(false);
        return docks.Select(d => (IStreamDeckDevice)d.CreateDevice(logger)).ToList();
    }

    /// <summary>
    /// Returns an observable that emits Stream Deck devices as they are
    /// discovered — USB devices immediately, network devices as mDNS
    /// announcements arrive.
    ///
    /// Dispose the returned subscription to stop discovery.
    /// </summary>
    public static IObservable<IStreamDeckDevice> Discover(
        bool includeUsb = true,
        bool includeNetwork = true,
        ILogger? logger = null)
    {
        var sources = new List<IObservable<IStreamDeckDevice>>();

        if (includeUsb)
        {
            var usbObservable = Observable.Create<IStreamDeckDevice>(observer =>
            {
                foreach (var dev in EnumerateUsb(logger))
                    observer.OnNext(dev);
                observer.OnCompleted();
                return System.Reactive.Disposables.Disposable.Empty;
            });
            sources.Add(usbObservable);
        }

        if (includeNetwork)
        {
            var discovery = new StreamDeckNetworkDiscovery();
            var networkObservable = discovery.DocksFound
                .Select(dock => (IStreamDeckDevice)dock.CreateDevice(logger))
                .Finally(discovery.Dispose);
            sources.Add(networkObservable);
        }

        return sources.Count switch
        {
            0 => Observable.Empty<IStreamDeckDevice>(),
            1 => sources[0],
            _ => sources.Merge()
        };
    }
}
