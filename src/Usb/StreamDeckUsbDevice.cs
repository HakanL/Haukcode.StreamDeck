using HidApi;
using Haukcode.StreamDeck.Imaging;
using CatalogDeviceInfo = Haukcode.StreamDeck.Models.DeviceInfo;

namespace Haukcode.StreamDeck.Usb;

/// <summary>
/// <see cref="IStreamDeckDevice"/> implementation backed by a USB HID connection.
///
/// USB HID protocol (V2 / JPEG family, sourced from Julusian/node-elgato-stream-deck
/// and Elgato's published HID API documentation):
///
/// Image write — hid_write, report ID 0x02:
/// <code>
/// [02] [07] [keyIndex] [isLast] [chunkSize LE] [pageNo LE] [JPEG data ≤1016 bytes]
/// </code>
/// Total 1024 bytes per chunk; fill with zeros if JPEG data is shorter.
///
/// Set brightness — hid_send_feature_report, report ID 0x03:
/// <code>
/// [03] [08] [brightness 0–100] [00 ...]
/// </code>
/// 32-byte payload.
///
/// Button input — hid_read, report ID 0x01:
/// Parsed by <see cref="StreamDeckSecondaryProtocol.Parse"/> (same format as
/// the Network Dock's secondary-port HID reports, since the dock tunnels raw
/// HID bytes verbatim).
///
/// Encoder input (Plus) — same report ID 0x01, subtype byte 0x03.
/// </summary>
public sealed class StreamDeckUsbDevice : IStreamDeckDevice
{
    private const int ImagePagePayloadSize = 1024;
    private const int ImagePageHeaderSize = 8;
    private const int ImagePageJpegMax = ImagePagePayloadSize - ImagePageHeaderSize;

    private const int HidReadSize = 512;
    private const int HidReadTimeoutMs = 200;

    private readonly HidApi.DeviceInfo hidDeviceInfo;
    private readonly CatalogDeviceInfo catalog;
    private readonly ILogger log;

    private Device? device;
    private CancellationTokenSource? lifetimeCts;
    private Task? readLoopTask;
    private readonly SemaphoreSlim writeLock = new(1, 1);

    private readonly BehaviorSubject<ConnectionState> connectionSubject
        = new(ConnectionState.Disconnected);
    private readonly Subject<bool[]> buttonStatesSubject = new();
    private readonly Subject<bool[]> encoderPressesSubject = new();
    private readonly Subject<sbyte[]> encoderRotationsSubject = new();

    internal StreamDeckUsbDevice(HidApi.DeviceInfo hidDeviceInfo, CatalogDeviceInfo catalog, ILogger logger)
    {
        this.hidDeviceInfo = hidDeviceInfo;
        this.catalog = catalog;
        this.log = logger;
    }

    // -------------------------------------------------------------------------
    // IStreamDeckDevice
    // -------------------------------------------------------------------------

    public StreamDeckModel Model => this.catalog.Model;
    public int KeyCount => this.catalog.KeyCount;
    public int KeyImageWidth => this.catalog.KeyImageWidth;
    public int KeyImageHeight => this.catalog.KeyImageHeight;
    public bool HasEncoders => this.catalog.EncoderCount > 0;
    public int EncoderCount => this.catalog.EncoderCount;

    public string? SerialNumber => string.IsNullOrEmpty(this.hidDeviceInfo.SerialNumber)
        ? null
        : this.hidDeviceInfo.SerialNumber;

    public IObservable<bool[]> ButtonStates => this.buttonStatesSubject.AsObservable();
    public IObservable<bool[]> EncoderPresses => this.encoderPressesSubject.AsObservable();
    public IObservable<sbyte[]> EncoderRotations => this.encoderRotationsSubject.AsObservable();
    public IObservable<ConnectionState> Connection => this.connectionSubject.AsObservable();

    /// <summary>
    /// Open the USB HID device, send initial brightness, and start the
    /// background read loop. Calling twice is a no-op.
    /// </summary>
    public void Start()
    {
        if (this.readLoopTask != null) return;
        this.log.LogInformation(
            "Starting Stream Deck USB HID transport: model={Model} serial={Serial}",
            this.catalog.Model,
            this.SerialNumber ?? "<none>");
        this.lifetimeCts = new CancellationTokenSource();
        this.readLoopTask = Task.Run(() => RunAsync(this.lifetimeCts.Token));
    }

    public async Task SetKeyImageAsync(int slot, Image<Rgba32> image, CancellationToken ct = default)
    {
        var jpegBytes = KeyImageEncoder.EncodeJpeg(
            image,
            this.catalog.KeyImageWidth,
            this.catalog.KeyImageHeight,
            rotate180: this.catalog.UsbImageRotate180);

        await WriteKeyImageChunksAsync(slot, jpegBytes, ct).ConfigureAwait(false);
    }

    public Task SetKeyImageAsync(int slot, byte[] encodedBytes, CancellationToken ct = default)
        => WriteKeyImageChunksAsync(slot, encodedBytes, ct);

    public async Task SetBrightnessAsync(byte percent, CancellationToken ct = default)
    {
        var pl = new byte[32];
        pl[0] = StreamDeckPrimaryProtocol.ReportFeature; // 0x03
        pl[1] = StreamDeckPrimaryProtocol.FeatureSetBrightness; // 0x08
        pl[2] = Math.Clamp(percent, (byte)0, (byte)100);

        await this.writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() => this.device!.SendFeatureReport(pl), ct).ConfigureAwait(false);
        }
        finally
        {
            this.writeLock.Release();
        }
    }

    public async Task ResetAsync(CancellationToken ct = default)
    {
        var pl = new byte[32];
        pl[0] = StreamDeckPrimaryProtocol.ReportFeature;
        pl[1] = StreamDeckPrimaryProtocol.FeatureReset;

        await this.writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() => this.device!.SendFeatureReport(pl), ct).ConfigureAwait(false);
        }
        finally
        {
            this.writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (this.lifetimeCts != null)
        {
            try { await this.lifetimeCts.CancelAsync().ConfigureAwait(false); } catch { }
        }

        if (this.readLoopTask != null)
        {
            try { await this.readLoopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { this.log.LogDebug(ex, "USB read loop threw during dispose: {Message}", ex.Message); }
        }

        this.device?.Dispose();
        this.device = null;

        this.lifetimeCts?.Dispose();
        this.connectionSubject.OnCompleted();
        this.buttonStatesSubject.OnCompleted();
        this.encoderPressesSubject.OnCompleted();
        this.encoderRotationsSubject.OnCompleted();
        this.writeLock.Dispose();
    }

    // -------------------------------------------------------------------------
    // Background task — open, activate, read loop
    // -------------------------------------------------------------------------

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            this.connectionSubject.OnNext(ConnectionState.Connecting);

            this.device = this.hidDeviceInfo.ConnectToDevice();

            this.connectionSubject.OnNext(ConnectionState.Activating);
            await ActivateAsync(ct).ConfigureAwait(false);

            this.connectionSubject.OnNext(ConnectionState.Connected);
            await ReadLoopAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            this.log.LogWarning(ex, "USB Stream Deck {Model} disconnected: {Message}", this.catalog.Model, ex.Message);
        }
        finally
        {
            this.connectionSubject.OnNext(ConnectionState.Disconnected);
        }
    }

    private async Task ActivateAsync(CancellationToken ct)
    {
        // Set a default brightness so the deck is in a known state.
        await SetBrightnessAsync(80, ct).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        // hidapi reads are blocking; run on the thread-pool thread already
        // provided by Task.Run in Start().
        await Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                ReadOnlySpan<byte> data;
                try
                {
                    data = this.device!.ReadTimeout(HidReadSize, HidReadTimeoutMs);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    this.log.LogDebug(ex, "USB HID read error: {Message}", ex.Message);
                    break;
                }

                if (data.IsEmpty) continue; // timeout — check ct and retry

                var ev = StreamDeckSecondaryProtocol.Parse(data);
                switch (ev)
                {
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
        }, CancellationToken.None).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Image write
    // -------------------------------------------------------------------------

    private async Task WriteKeyImageChunksAsync(int keyIndex, byte[] jpegBytes, CancellationToken ct)
    {
        if (this.catalog.ImageFormat == StreamDeckImageFormat.Bmp)
            throw new NotSupportedException($"{this.catalog.Model} requires BMP image format which is not yet implemented for USB transport.");

        int totalPages = (jpegBytes.Length + ImagePageJpegMax - 1) / ImagePageJpegMax;

        await this.writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                for (int page = 0; page < totalPages; page++)
                {
                    int offset = page * ImagePageJpegMax;
                    int chunk = Math.Min(ImagePageJpegMax, jpegBytes.Length - offset);
                    bool isLast = offset + chunk >= jpegBytes.Length;

                    var body = new byte[ImagePagePayloadSize];
                    body[0] = 0x02; // report ID
                    body[1] = 0x07; // command: set key image
                    body[2] = (byte)keyIndex;
                    body[3] = isLast ? (byte)1 : (byte)0;
                    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4, 2), (ushort)chunk);
                    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6, 2), (ushort)page);
                    Buffer.BlockCopy(jpegBytes, offset, body, ImagePageHeaderSize, chunk);

                    this.device!.Write(body);
                }
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            this.writeLock.Release();
        }
    }
}
