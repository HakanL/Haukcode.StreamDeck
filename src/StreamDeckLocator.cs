using Haukcode.StreamDeck.Network;
using Haukcode.StreamDeck.Usb;

namespace Haukcode.StreamDeck;

/// <summary>
/// Discovers and connects to Stream Deck devices regardless of transport.
///
/// USB devices are returned immediately (synchronous HID enumeration).
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
            var usb = StreamDeckUsbEnumerator.Enumerate(logger).FirstOrDefault();
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
    /// </summary>
    public static IEnumerable<IStreamDeckDevice> EnumerateUsb(ILogger? logger = null)
        => StreamDeckUsbEnumerator.Enumerate(logger);

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
                foreach (var dev in StreamDeckUsbEnumerator.Enumerate(logger))
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
