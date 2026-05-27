using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Haukcode.StreamDeck;
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

// Log button press and release events by tracking the previous state array.
bool[]? prevButtons = null;
device.ButtonStates.Subscribe(
    states =>
    {
        for (int i = 0; i < states.Length; i++)
        {
            bool wasPressed = prevButtons != null && i < prevButtons.Length && prevButtons[i];
            if (states[i] && !wasPressed)
                log.LogInformation("Key {Index} pressed", i);
            else if (!states[i] && wasPressed)
                log.LogInformation("Key {Index} released", i);
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

// Log touch events from the LCD strip (Stream Deck Plus / Studio).
// Tap   = active contact (quick tap or move) — logged at Debug to reduce noise.
// Hold  = stationary contact (finger resting) — logged at Debug to reduce noise.
// Swipe = gesture complete (finger lifted after moving), includes start and end position.
device.TouchEvents.Subscribe(
    ev =>
    {
        if (ev.EventType == LcdTouchEventType.Swipe)
            log.LogInformation("LCD Swipe ({X}, {Y}) → ({EndX}, {EndY})", ev.X, ev.Y, ev.EndX, ev.EndY);
        else
            log.LogDebug("LCD {EventType} at ({X}, {Y})", ev.EventType, ev.X, ev.Y);
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
    await device.SetKeyImageAsync(i, image, cts.Token);
}

// Push an image to the LCD touch strip if the device has one.
if (device.HasTouchDisplay)
{
    using var lcdImage = RenderLcdStrip(device.LcdStripWidth, device.LcdStripHeight);
    await device.SetLcdImageAsync(lcdImage, cts.Token);
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
    return transport switch
    {
        "auto" => await StreamDeckLocator.FindFirstAsync(
            includeUsb: true,
            includeNetwork: true,
            logger: logger,
            ct: ct),

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
        return value.ToLowerInvariant() switch
        {
            "auto" => "auto",
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
        "Unknown transport '{Transport}'. Use --transport=auto, --transport=hid, or --transport=raw-usb.",
        value);
    return null;
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

static Image<Rgba32> RenderTile(int width, int height, int keyIndex)
{
    byte shade = (byte)(0x18 + (keyIndex % 8) * 0x0A);
    var bg = new Rgba32(shade, shade, (byte)(shade + 0x10));
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

static Image<Rgba32> RenderLcdStrip(int width, int height)
{
    var image = new Image<Rgba32>(width, height, new Rgba32(0x10, 0x18, 0x40));

    var font = TryGetFont(height * 0.50f);
    if (font != null)
    {
        var opts = new RichTextOptions(font)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Origin              = new PointF(width / 2f, height / 2f),
        };
        image.Mutate(ctx => ctx.DrawText(opts, "Touch me!", Color.LightCyan));
    }

    return image;
}
