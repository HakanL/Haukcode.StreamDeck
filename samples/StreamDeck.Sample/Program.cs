using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Haukcode.StreamDeck;
using Haukcode.StreamDeck.Usb;

// Set up a logger so connection state and protocol events are visible.
using var logFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => o.SingleLine = true)
    .SetMinimumLevel(LogLevel.Information));
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

// Log button presses.
device.ButtonStates.Subscribe(
    states =>
    {
        for (int i = 0; i < states.Length; i++)
            if (states[i])
                log.LogInformation("Key {Index} pressed", i);
    },
    cts.Token);

// Log encoder events (Stream Deck Plus and Studio).
device.EncoderRotations.Subscribe(
    deltas =>
    {
        for (int i = 0; i < deltas.Length; i++)
            if (deltas[i] != 0)
                log.LogInformation("Encoder {Index} rotated {Delta:+0;-0}", i, deltas[i]);
    },
    cts.Token);

device.EncoderPresses.Subscribe(
    pressed =>
    {
        for (int i = 0; i < pressed.Length; i++)
            if (pressed[i])
                log.LogInformation("Encoder {Index} pressed", i);
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

log.LogInformation("Connected — model={Model}, keys={Keys}, encoders={Encoders}",
    device.Model, device.KeyCount, device.EncoderCount);

// Push a distinct dark tile to every key so the deck exits setup-mode
// and the keys are in a known visual state.
for (int i = 0; i < device.KeyCount; i++)
{
    using var image = RenderTile(device.KeyImageWidth, device.KeyImageHeight, i);
    await device.SetKeyImageAsync(i, image, cts.Token);
}

log.LogInformation("Images sent. Press any key label to log it. Ctrl+C to exit.");

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

static Image<Rgba32> RenderTile(int width, int height, int keyIndex)
{
    // Dark grey tile. Replace this with your own rendering — icons, text,
    // gradients — using SixLabors.ImageSharp (add the Drawing package for
    // text support: SixLabors.ImageSharp.Drawing).
    byte shade = (byte)(0x18 + (keyIndex % 8) * 0x0A);
    return new Image<Rgba32>(width, height, new Rgba32(shade, shade, (byte)(shade + 0x10)));
}
