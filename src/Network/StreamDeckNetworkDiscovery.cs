using Haukcode.Mdns;

namespace Haukcode.StreamDeck.Network;

/// <summary>
/// Discovers Elgato Network Docks on the local network via mDNS (<c>_elg._tcp</c>).
///
/// Usage — one-shot scan:
/// <code>
/// var docks = await StreamDeckNetworkDiscovery.ResolveAsync();
/// foreach (var dock in docks)
///     Console.WriteLine(dock);
/// </code>
///
/// Usage — continuous monitoring:
/// <code>
/// using var disc = new StreamDeckNetworkDiscovery();
/// disc.DocksFound.Subscribe(dock => Console.WriteLine($"Found: {dock}"));
/// disc.StartMonitoring();
/// </code>
/// </summary>
public sealed class StreamDeckNetworkDiscovery : IDisposable
{
    private const string ServiceType = "_elg._tcp";

    private readonly Subject<StreamDeckNetworkDock> foundSubject = new();
    private readonly Subject<StreamDeckNetworkDock> lostSubject = new();
    private readonly MdnsBrowser browser;
    private bool disposed;

    /// <summary>Emits each dock the moment it is first discovered.</summary>
    public IObservable<StreamDeckNetworkDock> DocksFound => this.foundSubject.AsObservable();

    /// <summary>Emits a dock when its mDNS record expires.</summary>
    public IObservable<StreamDeckNetworkDock> DocksLost => this.lostSubject.AsObservable();

    public StreamDeckNetworkDiscovery()
    {
        this.browser = new MdnsBrowser(ServiceType);
        this.browser.ServiceFound += OnServiceFound;
        this.browser.ServiceLost += OnServiceLost;
    }

    // -------------------------------------------------------------------------
    // One-shot resolve
    // -------------------------------------------------------------------------

    /// <summary>
    /// Perform a one-shot mDNS scan and return all currently-advertising
    /// Network Docks within <paramref name="scanTime"/>.
    /// </summary>
    public static async Task<IReadOnlyList<StreamDeckNetworkDock>> ResolveAsync(
        TimeSpan? scanTime = null,
        CancellationToken ct = default)
    {
        using var browser = new MdnsBrowser(ServiceType);
        var found = new List<StreamDeckNetworkDock>();

        browser.ServiceFound += svc =>
        {
            if (IsNetworkDock(svc))
                lock (found)
                    found.Add(ToDock(svc));
        };

        browser.Start();
        await Task.Delay(scanTime ?? TimeSpan.FromSeconds(3), ct);

        return found.ToList();
    }

    // -------------------------------------------------------------------------
    // Continuous monitoring
    // -------------------------------------------------------------------------

    /// <summary>
    /// Start continuous mDNS monitoring. <see cref="DocksFound"/> emits as
    /// new docks appear; <see cref="DocksLost"/> emits when their TTL expires.
    /// Call <see cref="Dispose"/> to stop.
    /// </summary>
    public void StartMonitoring()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        this.browser.Start();
    }

    // -------------------------------------------------------------------------
    // Filtering
    // -------------------------------------------------------------------------

    // Elgato device type codes advertised in the _elg._tcp TXT record's "dt=" key.
    // Verified live: a Network Dock advertises two _elg._tcp instances —
    //   dt=215 → the dock itself (primary port 5343)
    //   dt=213 → the connected Stream Deck (dynamic secondary port)
    // We accept the dock (215) only; the secondary port is discovered in-band
    // via the capabilities query once we connect to the primary port.
    private const string DeviceTypeDock = "215";

    private static bool IsNetworkDock(ServiceProfile profile)
    {
        if (!profile.Properties.TryGetValue("dt", out var dt))
            return false;

        return dt == DeviceTypeDock;
    }

    private static StreamDeckNetworkDock ToDock(ServiceProfile profile)
        => new(profile.InstanceName, profile.Address!.ToString(), profile.Port);

    // -------------------------------------------------------------------------
    // MdnsBrowser callbacks
    // -------------------------------------------------------------------------

    private void OnServiceFound(ServiceProfile profile)
    {
        if (IsNetworkDock(profile))
            this.foundSubject.OnNext(ToDock(profile));
    }

    private void OnServiceLost(ServiceProfile profile)
    {
        if (IsNetworkDock(profile))
            this.lostSubject.OnNext(ToDock(profile));
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;

        this.browser.ServiceFound -= OnServiceFound;
        this.browser.ServiceLost -= OnServiceLost;
        this.browser.Dispose();

        this.foundSubject.OnCompleted();
        this.foundSubject.Dispose();
        this.lostSubject.OnCompleted();
        this.lostSubject.Dispose();
    }
}
