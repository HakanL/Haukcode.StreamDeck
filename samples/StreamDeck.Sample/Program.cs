using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Haukcode.StreamDeck;
using Haukcode.StreamDeck.Network;
using Haukcode.StreamDeck.Usb;

// Set up a logger so connection state and protocol events are visible.
using var logFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => o.SingleLine = true)
    .SetMinimumLevel(LogLevel.Debug));
var log = logFactory.CreateLogger("Sample");
var streamDeckLog = logFactory.CreateLogger("StreamDeck");

var transport = ParseTransport(args, log);
if (transport == null)
    return 2;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

log.LogInformation("Searching for Stream Deck devices using transport mode '{TransportMode}'...", transport);

var device = await FindDeviceAsync(transport, streamDeckLog, cts.Token);

if (device is null)
{
    log.LogError("No Stream Deck found for transport mode '{TransportMode}'.", transport);
    return 1;
}

// Log connection state transitions.
device.Connection.Subscribe(
    state => log.LogInformation("Connection: {State}", state),
    cts.Token);

// Update key image on press (bright/inverted) and restore on release.
bool[]? prevButtons = null;
device.ButtonStates.Subscribe(
    states =>
    {
        for (int i = 0; i < states.Length; i++)
        {
            bool wasPressed = prevButtons != null && i < prevButtons.Length && prevButtons[i];
            if (states[i] && !wasPressed)
            {
                log.LogInformation("Key {Index} pressed", i);
                int idx = i;
                _ = Task.Run(async () =>
                {
                    using var img = RenderTilePressed(device.KeyImageWidth, device.KeyImageHeight, idx);
                    await device.SetKeyImageAsync(idx, ToJpeg(img), cts.Token);
                });
            }
            else if (!states[i] && wasPressed)
            {
                log.LogInformation("Key {Index} released", i);
                int idx = i;
                _ = Task.Run(async () =>
                {
                    using var img = RenderTile(device.KeyImageWidth, device.KeyImageHeight, idx);
                    await device.SetKeyImageAsync(idx, ToJpeg(img), cts.Token);
                });
            }
        }
        prevButtons = states;
    },
    cts.Token);

// Log encoder rotation events (Stream Deck Plus and Studio).
device.EncoderRotations.Subscribe(
    deltas =>
    {
        for (int i = 0; i < deltas.Length; i++)
            if (deltas[i] != 0)
                log.LogInformation("Encoder {Index} rotated {Delta:+0;-0}", i, deltas[i]);
    },
    cts.Token);

// Log encoder press and release events.
bool[]? prevEncoders = null;
device.EncoderPresses.Subscribe(
    pressed =>
    {
        for (int i = 0; i < pressed.Length; i++)
        {
            bool wasPressed = prevEncoders != null && i < prevEncoders.Length && prevEncoders[i];
            if (pressed[i] && !wasPressed)
                log.LogInformation("Encoder {Index} pressed", i);
            else if (!pressed[i] && wasPressed)
                log.LogInformation("Encoder {Index} released", i);
        }
        prevEncoders = pressed;
    },
    cts.Token);

// Update LCD image on tap (circle), hold (ring), and swipe (line start→end).
device.TouchEvents.Subscribe(
    ev =>
    {
        if (ev.EventType == LcdTouchEventType.Swipe)
        {
            log.LogInformation("LCD Swipe ({X}, {Y}) → ({EndX}, {EndY})", ev.X, ev.Y, ev.EndX, ev.EndY);
            _ = Task.Run(async () =>
            {
                using var img = RenderLcdSwipe(device.LcdStripWidth, device.LcdStripHeight, ev.X, ev.Y, ev.EndX, ev.EndY);
                await device.SetLcdImageAsync(ToJpeg(img), cts.Token);
            });
        }
        else if (ev.EventType == LcdTouchEventType.Tap)
        {
            log.LogDebug("LCD Tap at ({X}, {Y})", ev.X, ev.Y);
            _ = Task.Run(async () =>
            {
                using var img = RenderLcdTap(device.LcdStripWidth, device.LcdStripHeight, ev.X, ev.Y);
                await device.SetLcdImageAsync(ToJpeg(img), cts.Token);
            });
        }
        else
        {
            log.LogDebug("LCD Hold at ({X}, {Y})", ev.X, ev.Y);
            _ = Task.Run(async () =>
            {
                using var img = RenderLcdHold(device.LcdStripWidth, device.LcdStripHeight, ev.X, ev.Y);
                await device.SetLcdImageAsync(ToJpeg(img), cts.Token);
            });
        }
    },
    cts.Token);

// Start the connection cycle (USB opens immediately; network begins TCP handshake).
device.Start();

// Wait until the device reports Connected (or the user cancels).
using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
connectCts.CancelAfter(TimeSpan.FromSeconds(15));
try
{
    await device.Connection
        .Where(s => s == ConnectionState.Connected)
        .FirstAsync()
        .ToTask(connectCts.Token);
}
catch (OperationCanceledException) when (!cts.IsCancellationRequested)
{
    log.LogError("Timed out waiting for device to connect.");
    await device.DisposeAsync();
    return 1;
}

log.LogInformation("Connected — model={Model}, keys={Keys}, encoders={Encoders}, lcd={HasLcd}",
    device.Model, device.KeyCount, device.EncoderCount, device.HasTouchDisplay);

// Push a distinct tile with the key index to every key.
for (int i = 0; i < device.KeyCount; i++)
{
    using var image = RenderTile(device.KeyImageWidth, device.KeyImageHeight, i);
    await device.SetKeyImageAsync(i, ToJpeg(image), cts.Token);
}

// Push an image to the LCD touch strip if the device has one.
if (device.HasTouchDisplay)
{
    using var lcdImage = RenderLcdStrip(device.LcdStripWidth, device.LcdStripHeight);
    await device.SetLcdImageAsync(ToJpeg(lcdImage), cts.Token);
    log.LogInformation("LCD image sent ({W}×{H}). Try tapping the display.", device.LcdStripWidth, device.LcdStripHeight);
}

log.LogInformation("Images sent. Press keys or encoders to log events. Swipe the LCD strip to see a Swipe event. Ctrl+C to exit.");

// Keep running until the user cancels.
try { await Task.Delay(Timeout.Infinite, cts.Token); }
catch (OperationCanceledException) { }

await device.DisposeAsync();
log.LogInformation("Done.");
return 0;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

static async Task<IStreamDeckDevice?> FindDeviceAsync(string transport, ILogger logger, CancellationToken ct)
{
    if (transport.StartsWith("network:", StringComparison.OrdinalIgnoreCase))
    {
        string host = transport["network:".Length..];
        return new StreamDeckNetworkDevice(logger, host);
    }

    return transport switch
    {
        "auto" => await StreamDeckLocator.FindFirstAsync(
            includeUsb: true,
            includeNetwork: true,
            logger: logger,
            ct: ct),

        "network" => (await StreamDeckLocator.FindNetworkDevicesAsync(logger: logger, ct: ct)).FirstOrDefault(),

        "hid" => StreamDeckUsbEnumerator.Enumerate(logger).FirstOrDefault(),

        "raw-usb" => StreamDeckLocator.EnumerateLinuxRawUsb(logger).FirstOrDefault(),

        _ => null
    };
}

static string? ParseTransport(string[] args, ILogger log)
{
    const string transportPrefix = "--transport=";

    foreach (var arg in args)
    {
        if (!arg.StartsWith(transportPrefix, StringComparison.OrdinalIgnoreCase))
            continue;

        string value = arg[transportPrefix.Length..];
        if (value.StartsWith("network:", StringComparison.OrdinalIgnoreCase))
            return value; // preserve "network:<host>" verbatim

        return value.ToLowerInvariant() switch
        {
            "auto" => "auto",
            "network" => "network",
            "hid" => "hid",
            "raw-usb" => "raw-usb",
            "rawusb" => "raw-usb",
            _ => InvalidTransport(value, log)
        };
    }

    return "auto";
}

static string? InvalidTransport(string value, ILogger log)
{
    log.LogError(
        "Unknown transport '{Transport}'. Use --transport=auto, --transport=network, --transport=network:<host>, --transport=hid, or --transport=raw-usb.",
        value);
    return null;
}

// The library takes pre-encoded JPEG bytes; the renders below already match the
// device's native dimensions, so encoding is all that's left to do.
static byte[] ToJpeg(Image<Rgba32> image)
{
    using var ms = new MemoryStream();
    image.Save(ms, new JpegEncoder { Quality = 90 });
    return ms.ToArray();
}

// Try to find a usable system font for text rendering.
static Font? TryGetFont(float size)
{
    string[] candidates = ["Arial", "Liberation Sans", "DejaVu Sans", "Helvetica", "FreeSans"];
    foreach (var name in candidates)
    {
        if (SystemFonts.TryGet(name, out var family))
            return family.CreateFont(size, FontStyle.Bold);
    }
    return null;
}

static Rgba32[] PaletteColors() =>
[
    new(0xE0, 0x40, 0x40), // red
    new(0xE0, 0x90, 0x20), // orange
    new(0xD0, 0xD0, 0x10), // yellow
    new(0x30, 0xC0, 0x40), // green
    new(0x20, 0xA0, 0xD0), // cyan
    new(0x30, 0x50, 0xE0), // blue
    new(0x90, 0x30, 0xD0), // purple
    new(0xD0, 0x30, 0x90), // pink
];

static Image<Rgba32> RenderTile(int width, int height, int keyIndex)
{
    var colors = PaletteColors();
    var bg = colors[keyIndex % colors.Length];
    var image = new Image<Rgba32>(width, height, bg);

    var font = TryGetFont(height * 0.38f);
    if (font != null)
    {
        var opts = new RichTextOptions(font)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Origin              = new PointF(width / 2f, height / 2f),
        };
        image.Mutate(ctx => ctx.DrawText(opts, keyIndex.ToString(), Color.White));
    }

    return image;
}

static Image<Rgba32> RenderTilePressed(int width, int height, int keyIndex)
{
    var colors = PaletteColors();
    var c = colors[keyIndex % colors.Length];
    // Lighten towards white to signal pressed state.
    var bg = new Rgba32((byte)((c.R + 255) / 2), (byte)((c.G + 255) / 2), (byte)((c.B + 255) / 2));
    var image = new Image<Rgba32>(width, height, bg);

    var font = TryGetFont(height * 0.38f);
    if (font != null)
    {
        var opts = new RichTextOptions(font)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Origin              = new PointF(width / 2f, height / 2f),
        };
        image.Mutate(ctx => ctx.DrawText(opts, keyIndex.ToString(), new Color(c)));
    }

    return image;
}

static Image<Rgba32> RenderLcdBase(int width, int height)
{
    var colors = PaletteColors();
    var image = new Image<Rgba32>(width, height);
    image.Mutate(ctx =>
    {
        int bandW = width / colors.Length;
        for (int i = 0; i < colors.Length; i++)
            ctx.Fill(new SolidBrush(colors[i]),
                new RectangleF(i * bandW, 0, i == colors.Length - 1 ? width - i * bandW : bandW, height));
    });
    return image;
}

static Image<Rgba32> RenderLcdStrip(int width, int height)
{
    var image = RenderLcdBase(width, height);
    var font = TryGetFont(height * 0.50f);
    if (font != null)
    {
        var opts = new RichTextOptions(font)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Origin              = new PointF(width / 2f, height / 2f),
        };
        image.Mutate(ctx => ctx.DrawText(opts, "Touch me!", Color.White));
    }
    return image;
}

static Image<Rgba32> RenderLcdTap(int width, int height, ushort x, ushort y)
{
    var image = RenderLcdBase(width, height);
    image.Mutate(ctx =>
    {
        ctx.Fill(new SolidBrush(Color.White), new EllipsePolygon(x, y, 22f));
        ctx.Fill(new SolidBrush(Color.Black), new EllipsePolygon(x, y, 10f));
    });
    return image;
}

static Image<Rgba32> RenderLcdHold(int width, int height, ushort x, ushort y)
{
    var image = RenderLcdBase(width, height);
    image.Mutate(ctx =>
    {
        // Outer orange ring + smaller inner ring to suggest a "hold" indicator.
        ctx.Draw(Pens.Solid(Color.Orange, 4f), new EllipsePolygon(x, y, 28f));
        ctx.Draw(Pens.Solid(Color.Orange, 2f), new EllipsePolygon(x, y, 14f));
    });
    return image;
}

static Image<Rgba32> RenderLcdSwipe(int width, int height, ushort x, ushort y, ushort endX, ushort endY)
{
    var image = RenderLcdBase(width, height);
    image.Mutate(ctx =>
    {
        ctx.DrawLine(Pens.Solid(Color.White, 4f), new PointF(x, y), new PointF(endX, endY));
        ctx.Fill(new SolidBrush(Color.White), new EllipsePolygon(x, y, 8f));
        ctx.Fill(new SolidBrush(Color.Yellow), new EllipsePolygon(endX, endY, 10f));
    });
    return image;
}
