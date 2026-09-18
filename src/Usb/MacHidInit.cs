using HidApi;
using System.Runtime.InteropServices;

namespace Haukcode.StreamDeck.Usb;

/// <summary>
/// Initializes hidapi on macOS from a thread that lives as long as the process.
///
/// hidapi's macOS backend creates its process-wide IOHIDManager on the first
/// hid_init / hid_enumerate call and schedules it on that thread's run loop
/// (<c>IOHIDManagerScheduleWithRunLoop(..., CFRunLoopGetCurrent(), ...)</c>).
/// Left to itself that first call happens on a .NET thread-pool thread; when
/// the pool retires the thread its run loop is freed, and the next
/// enumeration schedules newly matched devices on the dangling run loop —
/// CoreFoundation's pointer-authentication check then faults inside
/// <c>CFRunLoopAddSource</c> and the enumerating thread never returns.
/// Calling <see cref="Hid.Init"/> first on a parked thread keeps that run loop
/// valid. hidapi registers no manager callbacks, so the loop never needs to
/// run; the thread only has to stay alive.
/// </summary>
internal static class MacHidInit
{
    private static readonly object Gate = new();
    private static bool started;

    public static void EnsureInitialized(ILogger log)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        lock (Gate)
        {
            if (started)
                return;

            started = true;

            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                try
                {
                    Hid.Init();
                }
                catch (Exception ex)
                {
                    // Enumeration reports a missing native library itself.
                    log.LogDebug(ex, "hidapi init failed: {Message}", ex.Message);
                }
                finally
                {
                    ready.Set();
                }

                Thread.Sleep(Timeout.Infinite);
            })
            {
                IsBackground = true,
                Name = "hidapi run loop owner",
            };
            thread.Start();
            ready.Wait();
        }
    }
}
