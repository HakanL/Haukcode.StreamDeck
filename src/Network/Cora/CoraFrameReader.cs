namespace Haukcode.StreamDeck.Network.Cora;

/// <summary>
/// Streaming CORA frame reader. Bytes are appended via <see cref="Append"/> as
/// they arrive on the socket; complete frames are returned by
/// <see cref="DrainFrames"/>. Holds a single growable buffer and shifts it
/// whenever frames are consumed. Not thread-safe — callers serialise.
/// </summary>
public sealed class CoraFrameReader
{
    /// <summary>Soft cap to prevent unbounded growth when the peer sends garbage. Generous enough to fit several full image-write packets.</summary>
    private const int MaxBufferBytes = 64 * 1024;

    private byte[] buffer = new byte[4096];
    private int length;

    /// <summary>Append received bytes to the internal buffer.</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        EnsureCapacity(this.length + data.Length);
        data.CopyTo(this.buffer.AsSpan(this.length));
        this.length += data.Length;
    }

    /// <summary>
    /// Pull every complete frame out of the buffer. The implementation also
    /// silently skips bare-byte greetings the dock sends before the first
    /// CORA frame (e.g. the initial 2-byte <c>00 00</c> on a fresh connection)
    /// — the buffer is fast-forwarded to the next CORA magic.
    /// </summary>
    public List<CoraFrame> DrainFrames()
    {
        var frames = new List<CoraFrame>();
        var span = this.buffer.AsSpan(0, this.length);
        int consumed = 0;

        while (consumed < span.Length)
        {
            // Resync to magic if we're not aligned. This skips bare-byte
            // greetings (00 00 / 00 00 00 00 00 00) the dock sends on
            // connection setup.
            if (span.Length - consumed >= 4 && !span.Slice(consumed, 4).SequenceEqual(CoraFrame.Magic))
            {
                int idx = IndexOf(span, CoraFrame.Magic, consumed + 1);
                if (idx < 0)
                {
                    // No magic anywhere — keep the last 3 bytes in case the
                    // magic straddles the next read.
                    consumed = Math.Max(0, span.Length - 3);
                    break;
                }
                consumed = idx;
                continue;
            }

            if (!CoraFrame.TryDecode(span.Slice(consumed), out var frame, out var frameLen))
                break; // need more bytes

            frames.Add(frame);
            consumed += frameLen;
        }

        // Shift the buffer so unconsumed bytes start at index 0.
        if (consumed > 0)
        {
            int remaining = this.length - consumed;
            if (remaining > 0)
                Array.Copy(this.buffer, consumed, this.buffer, 0, remaining);
            this.length = remaining;
        }

        return frames;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle, int startAt)
    {
        if (startAt < 0) startAt = 0;
        if (needle.Length == 0 || startAt + needle.Length > haystack.Length) return -1;

        for (int i = startAt; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return i;
        }
        return -1;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= this.buffer.Length) return;
        if (required > MaxBufferBytes)
            throw new InvalidOperationException(
                $"CoraFrameReader buffer overflow ({required} > {MaxBufferBytes}). Peer is likely sending malformed framing.");

        int newSize = this.buffer.Length;
        while (newSize < required) newSize *= 2;
        if (newSize > MaxBufferBytes) newSize = MaxBufferBytes;

        var newBuffer = new byte[newSize];
        Array.Copy(this.buffer, newBuffer, this.length);
        this.buffer = newBuffer;
    }
}
