# Haukcode.StreamDeck

Managed .NET implementation for Elgato Stream Deck devices, supporting both **USB HID** and the **Network Dock** TCP protocol behind a single transport-agnostic API.

[![NuGet](https://img.shields.io/nuget/v/Haukcode.StreamDeck.svg)](https://www.nuget.org/packages/Haukcode.StreamDeck)
[![Build](https://github.com/HakanL/Haukcode.StreamDeck/actions/workflows/main.yml/badge.svg)](https://github.com/HakanL/Haukcode.StreamDeck/actions)

---

## Features

- One `IStreamDeckDevice` abstraction over USB HID and Network Dock TCP
- Button, encoder rotation, and encoder press input as `IObservable<T>` streams (System.Reactive)
- Per-key image rendering via **SixLabors.ImageSharp** (`Image<Rgba32>`) — automatic resize, rotate, JPEG encode
- Brightness control and connection-state tracking
- mDNS-based discovery for Network Dock / Studio (`_elg._tcp`) via **Haukcode.Mdns**
- USB HID transport via **HidApi.Net** — cross-platform (Windows, Linux, macOS)
- CORA framing protocol (Elgato Network Dock / Studio) implemented from a clean specification, verified against live PCAP captures

## Supported devices

| Model               | Keys   | Encoders | USB | Network Dock |
|---------------------|--------|----------|-----|--------------|
| Stream Deck MK.2    | 5×3    | —        | ✅  | ✅           |
| Stream Deck XL      | 8×4    | —        | ✅  | ✅           |
| Stream Deck +       | 4×2    | 4        | ✅  | ✅           |
| Stream Deck Studio  | 8×4    | 6        | ✅  | ✅           |
| Stream Deck Mini Mk.2 | 3×2  | —        | ⚠️  | —            |

⚠️ Mini Mk.2 enumerates over USB but uses a BMP image format that is not yet implemented.

---

## Installation

```
dotnet add package Haukcode.StreamDeck
```

---

## Quick start

```csharp
using Haukcode.StreamDeck;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

// Find the first available device — USB or Network Dock
var device = await StreamDeckLocator.FindFirstAsync(
    includeUsb: true,
    includeNetwork: true);

if (device is null) return;

device.Connection.Subscribe(state => Console.WriteLine($"State: {state}"));

device.ButtonStates.Subscribe(states =>
{
    for (int i = 0; i < states.Length; i++)
        if (states[i]) Console.WriteLine($"Key {i} pressed");
});

device.EncoderRotations.Subscribe(deltas =>
{
    for (int i = 0; i < deltas.Length; i++)
        if (deltas[i] != 0) Console.WriteLine($"Encoder {i}: {deltas[i]:+0;-0}");
});

device.EncoderPresses.Subscribe(pressed =>
{
    for (int i = 0; i < pressed.Length; i++)
        if (pressed[i]) Console.WriteLine($"Encoder {i} pressed");
});

device.Start();

// Wait for the connect handshake to complete
await device.Connection
    .Where(s => s == ConnectionState.Connected)
    .FirstAsync();

// Push an image to a key
using var image = new Image<Rgba32>(device.KeyImageWidth, device.KeyImageHeight,
    new Rgba32(0x18, 0x18, 0x28));
await device.SetKeyImageAsync(slot: 0, image);

await device.SetBrightnessAsync(80);

await Task.Delay(Timeout.Infinite);
await device.DisposeAsync();
```

A full console sample is in [`samples/StreamDeck.Sample`](samples/StreamDeck.Sample).

---

## Discovery

### USB only

```csharp
foreach (var device in StreamDeckLocator.EnumerateUsb())
{
    Console.WriteLine($"USB: {device.Model} ({device.KeyCount} keys)");
}
```

### Network Docks via mDNS

```csharp
var docks = await StreamDeckLocator.FindNetworkDevicesAsync(
    scanDuration: TimeSpan.FromSeconds(3));

foreach (var dock in docks)
{
    Console.WriteLine($"Dock: {dock.Name} at {dock.Host}:{dock.PrimaryPort}");
    var device = dock.CreateDevice();
    device.Start();
}
```

### Continuous discovery (USB + network)

```csharp
using var subscription = StreamDeckLocator.Discover().Subscribe(device =>
{
    Console.WriteLine($"Found {device.Model}");
    device.Start();
});
```

---

## API surface

```csharp
public interface IStreamDeckDevice : IAsyncDisposable
{
    StreamDeckModel Model           { get; }
    int             KeyCount        { get; }
    int             KeyImageWidth   { get; }
    int             KeyImageHeight  { get; }
    bool            HasEncoders     { get; }
    int             EncoderCount    { get; }

    IObservable<ConnectionState> Connection       { get; }
    IObservable<bool[]>          ButtonStates     { get; }
    IObservable<bool[]>          EncoderPresses   { get; }
    IObservable<sbyte[]>         EncoderRotations { get; }

    void Start();
    Task SetKeyImageAsync(int slot, Image<Rgba32> image, CancellationToken ct = default);
    Task SetKeyImageAsync(int slot, byte[] encodedBytes, CancellationToken ct = default);
    Task SetBrightnessAsync(byte percent, CancellationToken ct = default);
}
```

`ConnectionState` transitions: `Disconnected → Connecting → Activating → Connected`.

---

## Protocol notes

- **USB HID**: report ID `0x02` for image writes (1024-byte chunks, 8-byte header + ≤1016 bytes JPEG), feature report `0x03 0x08` for brightness, report ID `0x01` for input.
- **Network Dock / Studio**: 16-byte CORA frames (`43 93 8A 41` magic) over two TCP ports — primary (5343, capabilities + keep-alive) and secondary (HID I/O tunneled verbatim). Last image chunk is sent with the `ReqAck | Verbatim` flags.
- Button and encoder reports use the same byte layout in both transports — the dock tunnels raw HID frames — so a single parser (`StreamDeckSecondaryProtocol.Parse`) handles both.

---

## Building

```
dotnet build src/StreamDeck.slnx
dotnet test  tests/StreamDeck.Tests/StreamDeck.Tests.csproj
```

The protocol is covered by 27 unit tests, including byte-for-byte verification against captured PCAP traces from real Network Dock / Studio sessions.

---

## License

MIT
