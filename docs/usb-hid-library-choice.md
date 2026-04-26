# USB HID library: HidApi.Net vs HidSharp

This document records why we picked **HidApi.Net** for the USB HID transport,
the alternative we considered (**HidSharp**), and the trade-offs that would
matter if we ever wanted to switch.

Decision date: 2026-04-26. Revisit if the trade-offs below change materially
(e.g. if HidApi.Net stops being maintained, or if Native AOT becomes a hard
requirement and HidSharp grows AOT support).

---

## Decision

Use **[HidApi.Net 1.x](https://github.com/badcel/HidApi.Net)** — a P/Invoke
binding to the C [hidapi](https://github.com/libusb/hidapi) library. We bundle
the native `hidapi` shared library for every supported RID
(`win-x64`, `win-x86`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`)
inside the published NuGet package under `runtimes/<rid>/native/`, so consumers
do not need to install anything on the host.

Native binaries live in this repo at `native/runtimes/<rid>/native/` and are
produced (or downloaded prebuilt) by `scripts/fetch-natives.sh`.

---

## Alternative considered: HidSharp

[HidSharp](https://www.zer7.com/software/hidsharp) (James F. Bellinger,
Apache 2.0) is a pure-managed HID library that P/Invokes directly to platform
APIs:

- Windows → `setupapi`/`hid.dll` (already present in the OS)
- Linux → `libudev` + `/dev/hidraw*` (libudev is preinstalled on every modern distro)
- macOS → `IOKit` (already present)

So HidSharp ships **zero native binaries** — one NuGet reference and it works
on all three platforms.

### Why we did not pick HidSharp

| Axis                        | HidApi.Net                              | HidSharp                                       |
|-----------------------------|-----------------------------------------|------------------------------------------------|
| API style                   | Modern: `Span<T>`/`Memory<T>`, allocation-aware | Older: `HidStream` + sync `byte[]` Read/Write  |
| Native AOT                  | Annotated, AOT-clean                    | Uses reflection in spots; not AOT-annotated    |
| Trimming                    | Trim-friendly                           | Will produce trim warnings                     |
| Maintenance                 | Active (badcel, regular releases)       | Stable, slow cadence (last release Oct 2025)   |
| Native binary distribution  | We have to ship `hidapi.dll`/`.so`/`.dylib` | Nothing to ship                                |
| Used by Stream Deck libs    | Less common in the ecosystem            | StreamDeckSharp (proven on this hardware)      |
| License                     | MIT                                     | Apache 2.0                                     |

### When to revisit

Switch back to HidSharp if:
- HidApi.Net stops being maintained, **or**
- The native-binary distribution overhead becomes a real problem
  (e.g. signing/notarization issues on macOS, broken arm64 builds, or a
  Linux distro without our chosen libc compatibility), **and**
- We do not need Native AOT or aggressive trimming

Switch costs: rewrite `src/Usb/StreamDeckUsbDevice.cs` and
`src/Usb/StreamDeckUsbEnumerator.cs` (~150 LOC). The `IStreamDeckDevice`
public API does not change; the test suite is protocol-only and is unaffected.

---

## How HidApi.Net's native lookup works

`HidApi.Net` uses `[DllImport("hidapi")]` and installs a
`NativeLibrary.SetDllImportResolver` that probes platform-specific filenames:

- Linux: `libhidapi-hidraw.so.0`, then `libhidapi-libusb.so.0`, then `libhidapi.so.0`
- macOS: `libhidapi.0.dylib`, then `libhidapi.dylib`
- Windows: `hidapi.dll`

We bundle whichever name the platform's package manager produces (preserving
the Debian / Homebrew / libusb-hidapi-release naming verbatim) — the resolver
finds it because .NET adds `runtimes/<rid>/native/` to the dlopen search path.

---

## Where the natives come from

| RID         | Source                                                                                    |
|-------------|-------------------------------------------------------------------------------------------|
| win-x64     | [libusb/hidapi](https://github.com/libusb/hidapi/releases) `hidapi-win.zip` (MSVC build)  |
| win-x86     | Same release zip                                                                          |
| linux-x64   | Debian `libhidapi-hidraw0` `.deb` (extracted; signed package from `deb.debian.org`)       |
| linux-arm64 | Same Debian package, arm64 architecture                                                   |
| osx-x64     | Homebrew bottle (`hidapi` formula, x86_64 architecture)                                   |
| osx-arm64   | Homebrew bottle (`hidapi` formula, arm64 architecture)                                    |

All sources are official, signed, and freely redistributable under hidapi's
GPL-3.0-or-later / BSD-3-Clause / original license dual-license — see the
[LICENSE files](https://github.com/libusb/hidapi/tree/master) in the upstream
repo. We elect the BSD-3-Clause terms for redistribution.
