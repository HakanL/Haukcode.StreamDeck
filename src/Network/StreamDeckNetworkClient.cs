using Haukcode.StreamDeck.Imaging;

namespace Haukcode.StreamDeck.Network;

internal enum StreamDeckNetworkConnectionState
{
    Disconnected,
    Connecting,
    Activating,
    Connected
}

/// <summary>
/// Managed client for an Elgato Stream Deck Network Dock with a USB Stream
/// Deck attached. Holds two TCP connections in lock-step:
/// <list type="bullet">
///   <item><b>Primary</b> (default 5343) — talk to the dock itself; used
///   to discover the dynamic secondary port via the capabilities query.</item>
///   <item><b>Secondary</b> (dynamic, learned from primary) — talk to the
///   connected USB Stream Deck via HID-over-TCP. All real I/O — button
///   events, image writes, brightness — happens here.</item>
/// </list>
///
/// Activation sequence (replicates the official Stream Deck app, reverse-
/// engineered against PCAP captures + Julusian's node-elgato-stream-deck):
/// <list type="number">
///   <item>Connect primary → query capabilities → record secondary port.</item>
///   <item>Connect secondary → GET_REPORT 0x05/0x06/0x0B (firmware/serial/devinfo).</item>
///   <item>SEND_REPORT 0x03 0x08 with the configured brightness.</item>
///   <item>WRITE 0x02 0x07 image pages — pushing initial images for every key
///   forces the dock out of its built-in setup-mode screen.</item>
/// </list>
///
/// Auto-reconnects on disconnect with a short fixed back-off.
/// </summary>
internal sealed class StreamDeckNetworkClient : IAsyncDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CapabilitiesTimeout = TimeSpan.FromSeconds(3);

    /// <summary>MK.2 / native Stream Deck image-page size (1024-byte payload, 8 header, 1016 JPEG bytes).</summary>
    private const int ImagePagePayloadSize = 1024;
    private const int ImagePageHeaderSize = 8;
    private const int ImagePageJpegMax = ImagePagePayloadSize - ImagePageHeaderSize;

    private readonly ILogger log;
    private readonly string host;
    private readonly int primaryPort;
    private readonly byte initialBrightness;

    private CancellationTokenSource? lifetimeCts;
    private Task? supervisorTask;

    private TcpClient? primaryTcp;
    private TcpClient? secondaryTcp;
    private NetworkStream? secondaryStream;
    private readonly SemaphoreSlim secondaryWriteLock = new(1, 1);
    private long nextMessageId;

    private readonly BehaviorSubject<StreamDeckNetworkConnectionState> stateSubject
        = new(StreamDeckNetworkConnectionState.Disconnected);
    private readonly Subject<bool[]> buttonStatesSubject = new();
    private readonly Subject<bool[]> encoderPressesSubject = new();
    private readonly Subject<sbyte[]> encoderRotationsSubject = new();

    private CapabilitiesEvent? lastCapabilities;

    public StreamDeckNetworkClient(ILogger logger, string host, int primaryPort = 5343, byte initialBrightness = 80)
    {
        this.log = logger ?? throw new ArgumentNullException(nameof(logger));
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.primaryPort = primaryPort;
        this.initialBrightness = initialBrightness;
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public IObservable<StreamDeckNetworkConnectionState> ConnectionState => this.stateSubject.AsObservable();
    public IObservable<bool[]> ButtonStates => this.buttonStatesSubject.AsObservable();
    public IObservable<bool[]> EncoderPresses => this.encoderPressesSubject.AsObservable();
    public IObservable<sbyte[]> EncoderRotations => this.encoderRotationsSubject.AsObservable();

    public bool IsConnected => this.stateSubject.Value == StreamDeckNetworkConnectionState.Connected;
    public string Host => this.host;
    public string? Serial => this.lastCapabilities?.ChildSerialNumber;
    public string? Model => this.lastCapabilities?.ChildModelName;
    public ushort? VendorId => this.lastCapabilities?.ChildVendorId;
    public ushort? ProductId => this.lastCapabilities?.ChildProductId;

    public int KeyCount { get; private set; }
    public int KeyImageWidth { get; private set; }
    public int KeyImageHeight { get; private set; }

    public void Start()
    {
        if (this.supervisorTask != null) return;
        this.lifetimeCts = new CancellationTokenSource();
        this.supervisorTask = Task.Run(() => SupervisorLoopAsync(this.lifetimeCts.Token));
    }

    public Task SetKeyImageAsync(int keyIndex, byte[] jpegBytes, CancellationToken ct = default)
    {
        if (jpegBytes == null) throw new ArgumentNullException(nameof(jpegBytes));
        if (jpegBytes.Length == 0) throw new ArgumentException("JPEG must be non-empty.", nameof(jpegBytes));
        if (keyIndex < 0 || (this.KeyCount > 0 && keyIndex >= this.KeyCount))
            throw new ArgumentOutOfRangeException(nameof(keyIndex));
        return SendKeyImageAsync(keyIndex, jpegBytes, ct);
    }

    public Task SetBrightnessAsync(byte percent, CancellationToken ct = default)
        => SendBrightnessAsync(percent, ct);

    public async ValueTask DisposeAsync()
    {
        if (this.lifetimeCts != null)
        {
            try { await this.lifetimeCts.CancelAsync().ConfigureAwait(false); } catch { }
        }

        if (this.supervisorTask != null)
        {
            try { await this.supervisorTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { this.log.LogDebug(ex, "Supervisor task threw during dispose: {Message}", ex.Message); }
        }

        TearDownConnections();

        try { this.lifetimeCts?.Dispose(); } catch { }
        this.stateSubject.OnCompleted();
        this.buttonStatesSubject.OnCompleted();
        this.encoderPressesSubject.OnCompleted();
        this.encoderRotationsSubject.OnCompleted();
        this.secondaryWriteLock.Dispose();
    }

    // -------------------------------------------------------------------------
    // Supervisor — connect / activate / receive / reconnect
    // -------------------------------------------------------------------------

    private async Task SupervisorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                this.stateSubject.OnNext(StreamDeckNetworkConnectionState.Connecting);

                await ConnectPrimaryAsync(ct).ConfigureAwait(false);
                var capabilities = await QueryCapabilitiesAsync(ct).ConfigureAwait(false);
                if (!capabilities.IsConnected || capabilities.SecondaryTcpPort == 0)
                    throw new InvalidOperationException(
                        $"Stream Deck Network Dock at {this.host}:{this.primaryPort} reports no connected USB device " +
                        $"(connected={capabilities.IsConnected}, secondaryPort={capabilities.SecondaryTcpPort}).");

                this.lastCapabilities = capabilities;
                this.KeyImageWidth = ParseKeyDim(capabilities.RawBody, 6);
                this.KeyImageHeight = ParseKeyDim(capabilities.RawBody, 8);

                this.log.LogInformation(
                    "Stream Deck Network Dock @ {Host}: '{Model}' (sn={Serial}) connected via secondary port {SecPort}.",
                    this.host, capabilities.ChildModelName, capabilities.ChildSerialNumber, capabilities.SecondaryTcpPort);

                await ConnectSecondaryAsync(capabilities.SecondaryTcpPort, ct).ConfigureAwait(false);

                this.stateSubject.OnNext(StreamDeckNetworkConnectionState.Activating);
                await PerformActivationAsync(ct).ConfigureAwait(false);

                this.stateSubject.OnNext(StreamDeckNetworkConnectionState.Connected);

                // Primary connection has done its job — close it so we only
                // run the secondary receive loop.
                CloseSafe(ref this.primaryTcp);
                await SecondaryReceiveLoopAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                this.log.LogWarning(ex,
                    "Stream Deck Network Dock @ {Host}: connection cycle failed: {Message}", this.host, ex.Message);
            }

            TearDownConnections();
            this.stateSubject.OnNext(StreamDeckNetworkConnectionState.Disconnected);

            if (ct.IsCancellationRequested) break;

            try { await Task.Delay(ReconnectDelay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // -------------------------------------------------------------------------
    // Primary — capabilities query
    // -------------------------------------------------------------------------

    private async Task ConnectPrimaryAsync(CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await tcp.ConnectAsync(this.host, this.primaryPort, connectCts.Token).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        this.primaryTcp = tcp;
    }

    private async Task<CapabilitiesEvent> QueryCapabilitiesAsync(CancellationToken ct)
    {
        var stream = this.primaryTcp!.GetStream();

        var payload = StreamDeckPrimaryProtocol.BuildGetCapabilities();
        var frame = new CoraFrame(0, CoraHidOp.Write, 1, payload).Encode();
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(CapabilitiesTimeout);

        var reader = new CoraFrameReader();
        var rxBuffer = new byte[4096];
        while (!timeoutCts.IsCancellationRequested)
        {
            int n;
            try { n = await stream.ReadAsync(rxBuffer, timeoutCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Capabilities query timed out for {this.host}:{this.primaryPort}.");
            }
            if (n == 0) throw new IOException("Primary connection closed during capabilities query.");

            reader.Append(rxBuffer.AsSpan(0, n));
            foreach (var f in reader.DrainFrames())
            {
                var ev = StreamDeckPrimaryProtocol.Parse(f.Payload);
                if (ev is CapabilitiesEvent caps) return caps;
            }
        }

        throw new TimeoutException($"Capabilities query timed out for {this.host}:{this.primaryPort}.");
    }

    private static int ParseKeyDim(byte[] capsBody, int offset)
    {
        if (offset + 1 >= capsBody.Length) return 0;
        return BinaryPrimitives.ReadUInt16LittleEndian(capsBody.AsSpan(offset, 2));
    }

    // -------------------------------------------------------------------------
    // Secondary — activation + receive loop
    // -------------------------------------------------------------------------

    private async Task ConnectSecondaryAsync(int port, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await tcp.ConnectAsync(this.host, port, connectCts.Token).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        this.secondaryTcp = tcp;
        this.secondaryStream = tcp.GetStream();
    }

    private async Task PerformActivationAsync(CancellationToken ct)
    {
        // GET_REPORT probes — replicates the official software's opening sequence.
        await SendSecondaryAsync(
            CoraFlags.ReqAck | CoraFlags.Verbatim, CoraHidOp.GetReport,
            StreamDeckSecondaryProtocol.BuildGetFirmwareVersion(), ct).ConfigureAwait(false);

        await SendSecondaryAsync(
            CoraFlags.ReqAck | CoraFlags.Verbatim, CoraHidOp.GetReport,
            StreamDeckSecondaryProtocol.BuildGetSerialNumber(), ct).ConfigureAwait(false);

        await SendSecondaryAsync(
            CoraFlags.ReqAck | CoraFlags.Verbatim, CoraHidOp.GetReport,
            StreamDeckSecondaryProtocol.BuildGetDeviceInfo(), ct).ConfigureAwait(false);

        await SendBrightnessAsync(this.initialBrightness, ct).ConfigureAwait(false);

        // The deck won't leave its built-in setup-mode screen until we push
        // at least one image. Send a blank to every key.
        var caps = this.lastCapabilities;
        int keys = caps != null ? KeyCountFromCapabilities(caps) : 15;
        this.KeyCount = keys;

        var blank = KeyImageEncoder.CreateBlankJpeg(this.KeyImageWidth, this.KeyImageHeight);
        for (int k = 0; k < keys; k++)
            await SendKeyImageAsync(k, blank, ct).ConfigureAwait(false);
    }

    private static int KeyCountFromCapabilities(CapabilitiesEvent caps)
    {
        // body bytes 3 = cols, 4 = rows. Falls back to 15 (MK.2) when body is short.
        if (caps.RawBody.Length < 5) return 15;
        int cols = caps.RawBody[3];
        int rows = caps.RawBody[4];
        int keys = cols * rows;
        return keys > 0 ? keys : 15;
    }

    private async Task SecondaryReceiveLoopAsync(CancellationToken ct)
    {
        var stream = this.secondaryStream!;
        var reader = new CoraFrameReader();
        var rxBuffer = new byte[8192];

        while (!ct.IsCancellationRequested)
        {
            int n = await stream.ReadAsync(rxBuffer, ct).ConfigureAwait(false);
            if (n == 0)
                throw new IOException("Secondary connection closed by peer.");

            reader.Append(rxBuffer.AsSpan(0, n));
            foreach (var frame in reader.DrainFrames())
            {
                var ev = StreamDeckSecondaryProtocol.Parse(frame.Payload);
                switch (ev)
                {
                    case KeepAlivePingEvent ka:
                        await SendKeepAliveAckAsync(ka.ConnectionNumber, ct).ConfigureAwait(false);
                        break;
                    case ButtonStateEvent btn:
                        this.buttonStatesSubject.OnNext(btn.States);
                        break;
                    case EncoderPressEvent ep:
                        this.encoderPressesSubject.OnNext(ep.Pressed);
                        break;
                    case EncoderRotateEvent er:
                        this.encoderRotationsSubject.OnNext(er.Deltas);
                        break;
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Secondary — outbound helpers
    // -------------------------------------------------------------------------

    private async Task SendKeepAliveAckAsync(byte connectionNumber, CancellationToken ct)
    {
        await SendSecondaryAsync(
            flags: 0,
            hidOp: CoraHidOp.Write,
            payload: StreamDeckPrimaryProtocol.BuildKeepAliveAck(connectionNumber),
            ct: ct,
            messageIdOverride: 0).ConfigureAwait(false);
    }

    private async Task SendBrightnessAsync(byte percent, CancellationToken ct)
    {
        var pl = new byte[StreamDeckSecondaryProtocol.RequestPayloadSize];
        pl[0] = StreamDeckPrimaryProtocol.ReportFeature;
        pl[1] = StreamDeckPrimaryProtocol.FeatureSetBrightness;
        pl[2] = Math.Clamp(percent, (byte)0, (byte)100);
        await SendSecondaryAsync(
            CoraFlags.ReqAck | CoraFlags.Verbatim, CoraHidOp.SendReport, pl, ct).ConfigureAwait(false);
    }

    private async Task SendKeyImageAsync(int keyIndex, byte[] jpegBytes, CancellationToken ct)
    {
        int totalPages = (jpegBytes.Length + ImagePageJpegMax - 1) / ImagePageJpegMax;
        for (int page = 0; page < totalPages; page++)
        {
            int offset = page * ImagePageJpegMax;
            int chunk = Math.Min(ImagePageJpegMax, jpegBytes.Length - offset);
            bool isLast = (offset + chunk) >= jpegBytes.Length;

            var body = new byte[ImagePagePayloadSize];
            body[0] = 0x02;
            body[1] = 0x07;
            body[2] = (byte)keyIndex;
            body[3] = isLast ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4, 2), (ushort)chunk);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6, 2), (ushort)page);
            Buffer.BlockCopy(jpegBytes, offset, body, ImagePageHeaderSize, chunk);

            // Last page is REQ_ACK so the dock confirms render completion.
            ushort flags = isLast
                ? (ushort)(CoraFlags.ReqAck | CoraFlags.Verbatim)
                : CoraFlags.Verbatim;

            await SendSecondaryAsync(flags, CoraHidOp.Write, body, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Send a CORA frame on the secondary connection. Serialised through
    /// <see cref="secondaryWriteLock"/> so concurrent SetKeyImageAsync /
    /// SetBrightnessAsync / keep-alive ACKs don't interleave bytes.
    /// </summary>
    private async Task SendSecondaryAsync(
        ushort flags,
        byte hidOp,
        byte[] payload,
        CancellationToken ct,
        uint? messageIdOverride = null)
    {
        var stream = this.secondaryStream;
        if (stream == null) throw new InvalidOperationException("Secondary stream not connected.");

        uint mid = messageIdOverride ?? unchecked((uint)Interlocked.Increment(ref this.nextMessageId));
        var encoded = new CoraFrame(flags, hidOp, mid, payload).Encode();

        await this.secondaryWriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(encoded, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Half-closed socket: force-close so the receive loop's pending
            // ReadAsync wakes immediately rather than hanging for hours.
            try { this.secondaryTcp?.Close(); } catch { }
            throw;
        }
        finally
        {
            this.secondaryWriteLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void TearDownConnections()
    {
        CloseSafe(ref this.primaryTcp);
        CloseSafe(ref this.secondaryTcp);
        this.secondaryStream = null;
    }

    private static void CloseSafe(ref TcpClient? tcp)
    {
        if (tcp == null) return;
        try { tcp.Close(); } catch { }
        try { tcp.Dispose(); } catch { }
        tcp = null;
    }
}
