using System.Runtime.InteropServices;
using Haukcode.StreamDeck.Imaging;
using CatalogDeviceInfo = Haukcode.StreamDeck.Models.DeviceInfo;

namespace Haukcode.StreamDeck.Usb;

/// <summary>
/// <see cref="IStreamDeckDevice"/> implementation backed by direct Linux usbfs I/O.
///
/// Uses ioctl calls on <c>/dev/bus/usb/BBB/DDD</c> to communicate with the
/// Stream Deck without going through the kernel's hidraw layer. This is
/// compatible with the Snap <c>raw-usb</c> interface, which grants access to
/// <c>/dev/bus/usb/**</c> but does not grant access to <c>/dev/hidraw*</c>.
///
/// On open: the kernel's <c>hid-generic</c> driver is detached from the HID
/// interface, the interface is claimed, and the read/write loops start. On
/// dispose: the interface is released and the kernel driver is re-attached so
/// the device works normally after the application exits.
///
/// USB HID wire protocol used (identical to <see cref="StreamDeckUsbDevice"/>):
/// <list type="bullet">
///   <item>Image write (output report 0x02): 1024-byte interrupt OUT transfer.</item>
///   <item>Set brightness (feature report 0x03): SET_REPORT USB control transfer.</item>
///   <item>Button/encoder input (input report 0x01): interrupt IN transfer.</item>
/// </list>
///
/// Only supported on Linux. Returns no devices on other platforms.
/// </summary>
internal sealed class StreamDeckRawUsbDevice : IStreamDeckDevice
{
    // -------------------------------------------------------------------------
    // Linux usbfs ioctl constants (64-bit: arm64 and x86_64 use the same values)
    // Computed from <linux/usbdevice_fs.h> macros using LP64 struct layouts.
    // -------------------------------------------------------------------------

    // _IOWR('U', 0, struct usbdevfs_ctrltransfer)  — sizeof = 24 on 64-bit
    private const uint USBDEVFS_CONTROL = 0xC0185500u;

    // _IOWR('U', 2, struct usbdevfs_bulktransfer)  — sizeof = 24 on 64-bit
    private const uint USBDEVFS_BULK = 0xC0185502u;

    // _IOR('U', 15, unsigned int)
    private const uint USBDEVFS_CLAIMINTERFACE = 0x8004550Fu;

    // _IOR('U', 16, unsigned int)
    private const uint USBDEVFS_RELEASEINTERFACE = 0x80045510u;

    // _IOWR('U', 18, struct usbdevfs_ioctl)  — sizeof = 16 on 64-bit
    private const uint USBDEVFS_IOCTL = 0xC0105512u;

    // Sub-codes for USBDEVFS_IOCTL: _IO('U', 22) and _IO('U', 23)
    private const int USBDEVFS_DISCONNECT_CODE = 0x00005516;
    private const int USBDEVFS_CONNECT_CODE    = 0x00005517;

    // USB HID class request constants
    private const byte RT_HOST_TO_DEV_CLASS_IFACE = 0x21;
    private const byte USB_REQ_SET_REPORT = 0x09;
    private const int  HID_REPORT_TYPE_FEATURE = 3;

    // I/O parameters
    private const int ReadTimeoutMs  = 200;
    private const int WriteTimeoutMs = 5_000;
    private const int CtrlTimeoutMs  = 5_000;
    private const int HidReadSize    = 512;

    // Image write constants (same as StreamDeckUsbDevice)
    private const int ImagePagePayloadSize = 1024;
    private const int ImagePageHeaderSize  = 8;
    private const int ImagePageJpegMax     = ImagePagePayloadSize - ImagePageHeaderSize;

    // -------------------------------------------------------------------------
    // Fields
    // -------------------------------------------------------------------------

    private readonly string devPath;
    private readonly CatalogDeviceInfo catalog;
    private readonly string? serialNumber;
    private readonly int ifaceNum;
    private readonly byte epIn;
    private readonly byte epOut;
    private readonly ILogger log;

    private int fd = -1;
    private CancellationTokenSource? lifetimeCts;
    private Task? readLoopTask;
    private readonly SemaphoreSlim writeLock = new(1, 1);

    private readonly BehaviorSubject<ConnectionState> connectionSubject
        = new(ConnectionState.Disconnected);
    private readonly Subject<bool[]>   buttonStatesSubject    = new();
    private readonly Subject<bool[]>   encoderPressesSubject  = new();
    private readonly Subject<sbyte[]>  encoderRotationsSubject = new();

    // -------------------------------------------------------------------------
    // P/Invoke — libc open/close/ioctl
    // -------------------------------------------------------------------------

    // open(2) — O_RDWR = 2
    [DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    // ioctl(2) — argp passed as void* via nint
    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static extern unsafe int ioctl(int fd, uint request, void* argp);

    private const int O_RDWR = 2;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    internal StreamDeckRawUsbDevice(
        string devPath,
        CatalogDeviceInfo catalog,
        string? serialNumber,
        int ifaceNum,
        byte epIn,
        byte epOut,
        ILogger logger)
    {
        this.devPath      = devPath;
        this.catalog      = catalog;
        this.serialNumber = serialNumber;
        this.ifaceNum     = ifaceNum;
        this.epIn         = epIn;
        this.epOut        = epOut;
        this.log          = logger;
    }

    // -------------------------------------------------------------------------
    // IStreamDeckDevice
    // -------------------------------------------------------------------------

    public StreamDeckModel Model       => this.catalog.Model;
    public int KeyCount                => this.catalog.KeyCount;
    public int KeyImageWidth           => this.catalog.KeyImageWidth;
    public int KeyImageHeight          => this.catalog.KeyImageHeight;
    public bool HasEncoders            => this.catalog.EncoderCount > 0;
    public int EncoderCount            => this.catalog.EncoderCount;
    public string? SerialNumber        => this.serialNumber;

    public IObservable<bool[]>   ButtonStates      => this.buttonStatesSubject.AsObservable();
    public IObservable<bool[]>   EncoderPresses    => this.encoderPressesSubject.AsObservable();
    public IObservable<sbyte[]>  EncoderRotations  => this.encoderRotationsSubject.AsObservable();
    public IObservable<ConnectionState> Connection => this.connectionSubject.AsObservable();

    public void Start()
    {
        if (this.readLoopTask != null) return;
        this.log.LogInformation(
            "Starting Stream Deck raw USB transport: model={Model} serial={Serial} path={DevPath}",
            this.catalog.Model,
            this.serialNumber ?? "<none>",
            this.devPath);
        this.lifetimeCts  = new CancellationTokenSource();
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
        pl[0] = StreamDeckPrimaryProtocol.ReportFeature;         // 0x03
        pl[1] = StreamDeckPrimaryProtocol.FeatureSetBrightness;  // 0x08
        pl[2] = Math.Clamp(percent, (byte)0, (byte)100);

        await this.writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() => SendFeatureReport(pl), ct).ConfigureAwait(false);
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
            catch (Exception ex)
            {
                this.log.LogDebug(ex, "Raw USB read loop threw during dispose: {Message}", ex.Message);
            }
        }

        CloseDevice();
        this.lifetimeCts?.Dispose();

        this.connectionSubject.OnCompleted();
        this.buttonStatesSubject.OnCompleted();
        this.encoderPressesSubject.OnCompleted();
        this.encoderRotationsSubject.OnCompleted();
        this.writeLock.Dispose();
    }

    // -------------------------------------------------------------------------
    // Background lifecycle
    // -------------------------------------------------------------------------

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            this.connectionSubject.OnNext(ConnectionState.Connecting);
            OpenDevice();

            this.connectionSubject.OnNext(ConnectionState.Activating);
            await SetBrightnessAsync(80, ct).ConfigureAwait(false);

            this.connectionSubject.OnNext(ConnectionState.Connected);
            await ReadLoopAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            this.log.LogWarning(ex,
                "Raw USB Stream Deck {Model} at {DevPath} error: {Message}",
                this.catalog.Model, this.devPath, ex.Message);
        }
        finally
        {
            this.connectionSubject.OnNext(ConnectionState.Disconnected);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        await Task.Run(() =>
        {
            var buf = new byte[HidReadSize];
            while (!ct.IsCancellationRequested)
            {
                int transferred;
                try
                {
                    transferred = BulkRead(buf, ReadTimeoutMs);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    this.log.LogDebug(ex, "Raw USB HID read error: {Message}", ex.Message);
                    break;
                }

                if (transferred <= 0)
                    continue; // timeout — loop back and check ct

                var ev = StreamDeckSecondaryProtocol.Parse(buf.AsSpan(0, transferred));
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
        if (this.catalog.ImageFormat == Models.StreamDeckImageFormat.Bmp)
            throw new NotSupportedException(
                $"{this.catalog.Model} requires BMP image format which is not implemented for USB transport.");

        int totalPages = (jpegBytes.Length + ImagePageJpegMax - 1) / ImagePageJpegMax;

        await this.writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                for (int page = 0; page < totalPages; page++)
                {
                    int offset  = page * ImagePageJpegMax;
                    int chunk   = Math.Min(ImagePageJpegMax, jpegBytes.Length - offset);
                    bool isLast = offset + chunk >= jpegBytes.Length;

                    var body = new byte[ImagePagePayloadSize];
                    body[0] = 0x02; // output report ID
                    body[1] = 0x07; // command: set key image
                    body[2] = (byte)keyIndex;
                    body[3] = isLast ? (byte)1 : (byte)0;
                    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4, 2), (ushort)chunk);
                    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6, 2), (ushort)page);
                    Buffer.BlockCopy(jpegBytes, offset, body, ImagePageHeaderSize, chunk);

                    BulkWrite(body, WriteTimeoutMs);
                }
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            this.writeLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Low-level usbfs operations (unsafe)
    // -------------------------------------------------------------------------

    private unsafe void OpenDevice()
    {
        this.log.LogInformation(
            "Opening Stream Deck raw USB device: path={DevPath} iface={Iface} epIn=0x{EpIn:X2} epOut=0x{EpOut:X2}",
            this.devPath,
            this.ifaceNum,
            this.epIn,
            this.epOut);

        this.fd = open(this.devPath, O_RDWR);
        if (this.fd < 0)
        {
            int err = Marshal.GetLastSystemError();
            throw new IOException($"Cannot open {this.devPath}: errno {err}. " +
                "Ensure the raw-usb snap interface is connected.");
        }

        // Detach the kernel hid-generic driver from the HID interface so we
        // can claim it. ENODATA (61) or ENODEV (19) mean no driver is
        // attached, which is fine.
        //
        // struct usbdevfs_ioctl layout (64-bit):
        //   [0..3]  int ifno
        //   [4..7]  int ioctl_code
        //   [8..15] void* data  (null for DISCONNECT)
        byte* ioctlBuf = stackalloc byte[16];
        *(int*)(ioctlBuf + 0) = this.ifaceNum;
        *(int*)(ioctlBuf + 4) = USBDEVFS_DISCONNECT_CODE;
        *(nint*)(ioctlBuf + 8) = 0;

        int ret = ioctl(this.fd, USBDEVFS_IOCTL, ioctlBuf);
        if (ret < 0)
        {
            int err = Marshal.GetLastSystemError();
            // 61 = ENODATA, 19 = ENODEV — no driver attached, that's OK.
            if (err != 61 && err != 19)
            {
                this.log.LogDebug(
                    "USBDEVFS_DISCONNECT on {DevPath} iface {Iface} returned errno {Errno} (may be OK)",
                    this.devPath, this.ifaceNum, err);
            }
        }
        else
        {
            this.log.LogInformation(
                "Detached kernel HID driver from {DevPath} interface {Iface}.",
                this.devPath,
                this.ifaceNum);
        }

        // Claim the interface so we can issue transfers.
        int iface = this.ifaceNum;
        ret = ioctl(this.fd, USBDEVFS_CLAIMINTERFACE, &iface);
        if (ret < 0)
        {
            int err = Marshal.GetLastSystemError();
            close(this.fd);
            this.fd = -1;
            throw new IOException(
                $"Cannot claim interface {this.ifaceNum} on {this.devPath}: errno {err}");
        }

        this.log.LogInformation(
            "Raw USB transport opened: model={Model} path={DevPath} iface={Iface} epIn=0x{EpIn:X2} epOut=0x{EpOut:X2}",
            this.catalog.Model, this.devPath, this.ifaceNum, this.epIn, this.epOut);
    }

    private unsafe void CloseDevice()
    {
        if (this.fd < 0)
            return;

        // Release the interface.
        int iface = this.ifaceNum;
        ioctl(this.fd, USBDEVFS_RELEASEINTERFACE, &iface);

        // Re-attach the kernel driver so the device works after we exit.
        byte* ioctlBuf = stackalloc byte[16];
        *(int*)(ioctlBuf + 0) = this.ifaceNum;
        *(int*)(ioctlBuf + 4) = USBDEVFS_CONNECT_CODE;
        *(nint*)(ioctlBuf + 8) = 0;
        ioctl(this.fd, USBDEVFS_IOCTL, ioctlBuf);

        close(this.fd);
        this.fd = -1;
    }

    /// <summary>
    /// Submit an interrupt IN transfer on <see cref="epIn"/> with a timeout.
    /// Returns the number of bytes read, or 0 on timeout.
    /// Throws <see cref="IOException"/> on a real error.
    /// </summary>
    private unsafe int BulkRead(byte[] buf, int timeoutMs)
    {
        // struct usbdevfs_bulktransfer layout (64-bit):
        //   [0..3]   unsigned ep
        //   [4..7]   unsigned len
        //   [8..11]  unsigned timeout (ms)
        //   [12..15] padding (align void* to 8)
        //   [16..23] void* data
        fixed (byte* dataPtr = buf)
        {
            byte* xferBuf = stackalloc byte[24];
            *(uint*)(xferBuf + 0)  = this.epIn;
            *(uint*)(xferBuf + 4)  = (uint)buf.Length;
            *(uint*)(xferBuf + 8)  = (uint)timeoutMs;
            *(uint*)(xferBuf + 12) = 0;            // padding
            *(void**)(xferBuf + 16) = dataPtr;

            int ret = ioctl(this.fd, USBDEVFS_BULK, xferBuf);
            if (ret < 0)
            {
                int err = Marshal.GetLastSystemError();
                // 110 = ETIMEDOUT, 11 = EAGAIN — expected on empty poll interval.
                if (err == 110 || err == 11)
                    return 0;
                throw new IOException($"USB interrupt IN read failed: errno {err}");
            }

            return ret;
        }
    }

    /// <summary>
    /// Submit an interrupt OUT transfer on <see cref="epOut"/>.
    /// Throws <see cref="IOException"/> on error.
    /// </summary>
    private unsafe void BulkWrite(byte[] buf, int timeoutMs)
    {
        if (this.epOut == 0)
            throw new NotSupportedException(
                "No interrupt OUT endpoint found for this Stream Deck device.");

        fixed (byte* dataPtr = buf)
        {
            byte* xferBuf = stackalloc byte[24];
            *(uint*)(xferBuf + 0)  = this.epOut;
            *(uint*)(xferBuf + 4)  = (uint)buf.Length;
            *(uint*)(xferBuf + 8)  = (uint)timeoutMs;
            *(uint*)(xferBuf + 12) = 0;
            *(void**)(xferBuf + 16) = dataPtr;

            int ret = ioctl(this.fd, USBDEVFS_BULK, xferBuf);
            if (ret < 0)
            {
                int err = Marshal.GetLastSystemError();
                throw new IOException($"USB interrupt OUT write failed: errno {err}");
            }
        }
    }

    /// <summary>
    /// Send a HID feature report via USB SET_REPORT control transfer.
    /// The first byte of <paramref name="report"/> is the report ID, which is
    /// also placed in wValue. Per USB HID 1.11 §8.1, for multi-report devices
    /// the data stage includes the report ID prefix.
    /// </summary>
    private unsafe void SendFeatureReport(byte[] report)
    {
        if (report.Length < 1)
            throw new ArgumentException("Feature report must be at least 1 byte.", nameof(report));

        byte reportId = report[0];

        // struct usbdevfs_ctrltransfer layout (64-bit):
        //   [0]      bRequestType
        //   [1]      bRequest
        //   [2..3]   wValue
        //   [4..5]   wIndex
        //   [6..7]   wLength
        //   [8..11]  timeout (ms)
        //   [12..15] padding (align void* to 8)
        //   [16..23] void* data
        fixed (byte* dataPtr = report)
        {
            byte* ctrlBuf = stackalloc byte[24];
            ctrlBuf[0] = RT_HOST_TO_DEV_CLASS_IFACE;
            ctrlBuf[1] = USB_REQ_SET_REPORT;
            *(ushort*)(ctrlBuf + 2) = (ushort)((HID_REPORT_TYPE_FEATURE << 8) | reportId);
            *(ushort*)(ctrlBuf + 4) = (ushort)this.ifaceNum;
            *(ushort*)(ctrlBuf + 6) = (ushort)report.Length; // full length incl. report ID
            *(uint*)(ctrlBuf + 8)   = CtrlTimeoutMs;
            *(uint*)(ctrlBuf + 12)  = 0;                     // padding
            *(void**)(ctrlBuf + 16) = dataPtr;               // full report incl. report ID

            int ret = ioctl(this.fd, USBDEVFS_CONTROL, ctrlBuf);
            if (ret < 0)
            {
                int err = Marshal.GetLastSystemError();
                throw new IOException($"USB SET_REPORT (feature) failed: errno {err}");
            }
        }
    }
}
