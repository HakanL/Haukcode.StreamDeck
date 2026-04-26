namespace Haukcode.StreamDeck.Network.Cora;

/// <summary>
/// CORA framing — the link-level wrapper used by Elgato's Stream Deck
/// Network Dock and Stream Deck Studio. Reverse-engineered with help from
/// <see href="https://github.com/Julusian/node-elgato-stream-deck">node-elgato-stream-deck</see>'s
/// <c>socketWrapper.ts</c>; the constants here mirror that source 1:1.
///
/// Wire format (16-byte header + variable payload):
/// <code>
/// bytes  0..3   : magic 43 93 8A 41
/// bytes  4..5   : flags (LE uint16)
/// byte   6      : hidOp
/// byte   7      : reserved (zero)
/// bytes  8..11  : messageId (LE uint32) — request/response correlation
/// bytes 12..15  : payloadLength (LE uint32)
/// bytes 16..    : payload
/// </code>
///
/// Both directions on both TCP ports (primary 5343 + dynamic secondary)
/// frame application data this way once the connection has handshaked
/// past its initial bare-byte greeting.
/// </summary>
public readonly record struct CoraFrame(ushort Flags, byte HidOp, uint MessageId, byte[] Payload)
{
    /// <summary>CORA magic bytes — first 4 bytes of every framed packet.</summary>
    public static readonly byte[] Magic = [0x43, 0x93, 0x8A, 0x41];

    public const int HeaderSize = 16;

    /// <summary>
    /// Encode this frame for transmission. Allocates a single buffer of
    /// <c>HeaderSize + Payload.Length</c> bytes.
    /// </summary>
    public byte[] Encode()
    {
        var buf = new byte[HeaderSize + Payload.Length];
        Magic.CopyTo(buf, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4, 2), Flags);
        buf[6] = HidOp;
        buf[7] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), MessageId);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), (uint)Payload.Length);
        Payload.CopyTo(buf, HeaderSize);
        return buf;
    }

    /// <summary>
    /// Try to decode one CORA frame from the head of <paramref name="buffer"/>.
    /// Returns <c>false</c> when the buffer is too short for the header or
    /// for the declared payload length — caller should append more bytes and
    /// retry. Sets <paramref name="consumed"/> to <c>HeaderSize + payloadLength</c>
    /// on success so the caller can advance its read pointer.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> buffer, out CoraFrame frame, out int consumed)
    {
        frame = default;
        consumed = 0;
        if (buffer.Length < HeaderSize) return false;
        if (!buffer[..4].SequenceEqual(Magic)) return false;

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(4, 2));
        byte hidOp = buffer[6];
        uint msgId = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(8, 4));
        uint payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(12, 4));

        if (buffer.Length < HeaderSize + payloadLen) return false;

        frame = new CoraFrame(flags, hidOp, msgId, buffer.Slice(HeaderSize, (int)payloadLen).ToArray());
        consumed = HeaderSize + (int)payloadLen;
        return true;
    }
}

/// <summary>
/// CORA flag bit constants (<see cref="CoraFrame.Flags"/>).
/// </summary>
public static class CoraFlags
{
    /// <summary>In/Out — payload is verbatim data for the child HID device.</summary>
    public const ushort Verbatim = 0x8000;

    /// <summary>Out — host requests an ACK from the unit.</summary>
    public const ushort ReqAck = 0x4000;

    /// <summary>In — unit response to a <see cref="ReqAck"/> request.</summary>
    public const ushort AckNak = 0x0200;

    /// <summary>In — unit response to a <see cref="CoraHidOp.GetReport"/> op.</summary>
    public const ushort Result = 0x0100;
}

/// <summary>
/// CORA hidOp values (<see cref="CoraFrame.HidOp"/>).
/// </summary>
public static class CoraHidOp
{
    /// <summary>hid_write — write an output report.</summary>
    public const byte Write = 0x00;

    /// <summary>hid_send_feature_report — send a feature report.</summary>
    public const byte SendReport = 0x01;

    /// <summary>hid_get_feature_report — request a feature report.</summary>
    public const byte GetReport = 0x02;
}
