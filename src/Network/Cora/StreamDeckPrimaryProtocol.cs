namespace Haukcode.StreamDeck.Network.Cora;

/// <summary>
/// Inner-payload commands and event parsing for the <b>primary</b> TCP port
/// of an Elgato Stream Deck Network Dock or Studio (default port 5343).
/// Builders return the inner payload bytes — wrap them in a
/// <see cref="CoraFrame"/> before sending. Parser expects the inner payload
/// already extracted from a frame.
///
/// Convention on the primary port: every command starts with <c>0x03</c>
/// (feature-report kind) followed by a 1-byte sub-command. Replies echo
/// the same two bytes. Some unsolicited events use <c>0x01</c> as the
/// top-level kind (keep-alive ping, capabilities push).
/// </summary>
public static class StreamDeckPrimaryProtocol
{
    /// <summary>Standard inner-payload size for primary-port commands — 1024 bytes, zero-padded.</summary>
    public const int CommandPayloadSize = 1024;

    // --- bytes[0] (top-level kind) ---
    public const byte ReportFeature = 0x03;
    public const byte ReportEvent = 0x01;

    // --- 03 XX feature sub-commands ---
    public const byte FeatureGetVersion1 = 0x81;
    public const byte FeatureGetVersion2 = 0x82;
    public const byte FeatureGetFirmware = 0x83;
    public const byte FeatureGetSerial = 0x84;
    public const byte FeatureReset = 0x02;
    public const byte FeatureSetBrightness = 0x08;
    public const byte FeatureGetCapabilities = 0x1c;
    public const byte FeatureKeepAliveAck = 0x1a;

    // --- 01 XX event sub-commands ---
    public const byte EventKeepAlive = 0x0a;
    public const byte EventCapabilities = 0x0b;

    // -------------------------------------------------------------------------
    // Outgoing — building 1024-byte payloads (wrap in a CoraFrame to send)
    // -------------------------------------------------------------------------

    /// <summary>Build a feature-query payload (03 XX) with no extra arguments.</summary>
    public static byte[] BuildFeatureQuery(byte featureSubCommand)
    {
        var buf = new byte[CommandPayloadSize];
        buf[0] = ReportFeature;
        buf[1] = featureSubCommand;
        return buf;
    }

    public static byte[] BuildGetVersion1() => BuildFeatureQuery(FeatureGetVersion1);
    public static byte[] BuildGetVersion2() => BuildFeatureQuery(FeatureGetVersion2);
    public static byte[] BuildGetFirmware() => BuildFeatureQuery(FeatureGetFirmware);
    public static byte[] BuildGetSerial() => BuildFeatureQuery(FeatureGetSerial);
    public static byte[] BuildGetCapabilities() => BuildFeatureQuery(FeatureGetCapabilities);

    /// <summary>Set display brightness as a percentage (clamped to 0–100).</summary>
    public static byte[] BuildSetBrightness(int percent)
    {
        var buf = new byte[CommandPayloadSize];
        buf[0] = ReportFeature;
        buf[1] = FeatureSetBrightness;
        buf[2] = (byte)Math.Clamp(percent, 0, 100);
        return buf;
    }

    /// <summary>
    /// Build the inner payload for a keep-alive ACK in response to a
    /// <see cref="EventKeepAlive"/> ping. The connection-number byte is
    /// echoed back from byte 5 of the incoming ping payload — the dock
    /// uses it to match ACKs to its internal virtual-link tracking. Wrap in a
    /// CoraFrame with flags=0, hidOp=0, messageId=0 (per the official
    /// Stream Deck app's wire pattern observed in capture).
    /// </summary>
    public static byte[] BuildKeepAliveAck(byte connectionNumber)
    {
        // 1024-byte payload, matching the official Stream Deck software's
        // wire pattern. (Julusian's node library uses 32 bytes here, which
        // the dock seems to also accept; we mirror the official software for
        // maximum compatibility.)
        var buf = new byte[CommandPayloadSize];
        buf[0] = ReportFeature;
        buf[1] = FeatureKeepAliveAck;
        buf[2] = connectionNumber;
        return buf;
    }

    // -------------------------------------------------------------------------
    // Incoming — parsing inner payloads
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parse the inner payload of a CORA frame received on the primary port.
    /// </summary>
    public static StreamDeckIncomingEvent Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
            return new UnknownEvent(payload.ToArray());

        return payload[0] switch
        {
            ReportEvent => ParseEventReport(payload),
            ReportFeature => ParseFeatureResponse(payload),
            _ => new UnknownEvent(payload.ToArray())
        };
    }

    private static StreamDeckIncomingEvent ParseEventReport(ReadOnlySpan<byte> payload)
    {
        byte sub = payload[1];
        return sub switch
        {
            EventKeepAlive => new KeepAlivePingEvent(payload.Length > 5 ? payload[5] : (byte)0),
            EventCapabilities => ParseCapabilities(payload),
            _ => new UnknownEvent(payload.ToArray())
        };
    }

    private static StreamDeckIncomingEvent ParseCapabilities(ReadOnlySpan<byte> payload)
    {
        // Layout (verified live against MK.2 + Network Dock):
        //   payload[0..1]   : 01 0b
        //   payload[2..3]   : meaningful-section length (LE uint16) — informational
        //   payload[4]      : connected flag  (0x02 when a USB deck is plugged)
        //   payload[5..7]   : grid type / cols / rows
        //   payload[8..9]   : key image width  (LE uint16)
        //   payload[10..11] : key image height (LE uint16)
        //   payload[12..13] : touchscreen / display-area width  (LE uint16)
        //   payload[14..15] : touchscreen / display-area height (LE uint16)
        //   payload[26..27] : USB vendor ID (LE uint16)
        //   payload[28..29] : USB product ID (LE uint16)
        //   payload[30..]   : "Elgato\0" manufacturer string (null-terminated)
        //   payload[62..93] : ASCII model name, null-padded (offset 62 verified live — 64 truncates "Stream Deck MK.2")
        //   payload[94..125]: ASCII serial, null-padded
        //   payload[126..127]: dynamic secondary TCP port (LE uint16)
        bool connected = payload.Length > 4 && payload[4] == 0x02;
        ushort vid = payload.Length >= 28 ? BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(26, 2)) : (ushort)0;
        ushort pid = payload.Length >= 30 ? BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(28, 2)) : (ushort)0;
        string model = ReadAsciiZ(payload, 62, 94);
        string serial = ReadAsciiZ(payload, 94, 126);
        ushort secPort = payload.Length >= 128 ? BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(126, 2)) : (ushort)0;

        return new CapabilitiesEvent(
            IsConnected: connected,
            ChildSerialNumber: serial,
            ChildModelName: model,
            ChildVendorId: vid,
            ChildProductId: pid,
            SecondaryTcpPort: secPort,
            RawBody: payload.Slice(2).ToArray());
    }

    private static StreamDeckIncomingEvent ParseFeatureResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
            return new UnknownEvent(payload.ToArray());

        byte sub = payload[1];
        ushort length = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2, 2));
        var body = payload.Slice(4).ToArray();
        return new FeatureResponseEvent(sub, length, body);
    }

    private static string ReadAsciiZ(ReadOnlySpan<byte> payload, int start, int endExclusive)
    {
        if (start >= payload.Length) return string.Empty;
        int hardEnd = Math.Min(endExclusive, payload.Length);
        int nulAt = -1;
        for (int i = start; i < hardEnd; i++)
        {
            if (payload[i] == 0) { nulAt = i; break; }
        }
        int len = (nulAt >= 0 ? nulAt : hardEnd) - start;
        if (len <= 0) return string.Empty;
        return System.Text.Encoding.ASCII.GetString(payload.Slice(start, len));
    }
}
