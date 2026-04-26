namespace Haukcode.StreamDeck.Network.Cora;

/// <summary>
/// Inner-payload commands and event parsing for the <b>secondary</b> TCP
/// port of an Elgato Stream Deck Network Dock — the dynamic port reported
/// in the primary port's <see cref="CapabilitiesEvent.SecondaryTcpPort"/>.
/// This is where the dock tunnels HID I/O for the connected USB Stream
/// Deck.
///
/// Convention on the secondary port: payloads are <i>raw HID-style</i> with
/// no <c>0x03</c> prefix. Outgoing commands typically use
/// <see cref="CoraHidOp.GetReport"/> with flags
/// <c>(<see cref="CoraFlags.ReqAck"/> | <see cref="CoraFlags.Verbatim"/>)</c>
/// = <c>0xC000</c> and a 32-byte payload starting with the HID report ID.
/// Incoming HID input reports (button states) arrive with flag
/// <see cref="CoraFlags.Verbatim"/> only and a 512-byte payload.
///
/// This same parser is reused for USB HID reads, since the CORA tunnel
/// passes raw HID bytes verbatim and the USB device produces the same format.
/// </summary>
public static class StreamDeckSecondaryProtocol
{
    /// <summary>Standard request payload size on the secondary port — matches Elgato's framing.</summary>
    public const int RequestPayloadSize = 32;

    /// <summary>Flags used for outgoing GET_REPORT requests on the secondary port.</summary>
    public const ushort GetReportFlags = (ushort)(CoraFlags.ReqAck | CoraFlags.Verbatim); // 0xC000

    // --- HID report IDs observed on the secondary port ---

    /// <summary>HID feature report — firmware version of the connected deck (e.g. "1.02.000").</summary>
    public const byte ReportFirmwareVersion = 0x05;

    /// <summary>HID feature report — serial number of the connected deck (length-prefixed ASCII).</summary>
    public const byte ReportSerialNumber = 0x06;

    /// <summary>HID input report — button state (sent unsolicited on key change).</summary>
    public const byte ReportButtonStateInput = 0x01;

    /// <summary>HID feature report — device info / capabilities of the connected deck.</summary>
    public const byte ReportDeviceInfo = 0x0B;

    // -------------------------------------------------------------------------
    // Outgoing — building HID GET_REPORT payloads (wrap in CoraFrame to send)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Build the 32-byte HID GET_REPORT payload for the given report ID.
    /// Wrap the result in a <see cref="CoraFrame"/> with
    /// <c>HidOp = <see cref="CoraHidOp.GetReport"/></c>,
    /// <c>Flags = <see cref="GetReportFlags"/></c>, and a fresh
    /// <c>messageId</c>.
    /// </summary>
    public static byte[] BuildGetReport(byte reportId)
    {
        var buf = new byte[RequestPayloadSize];
        buf[0] = reportId;
        return buf;
    }

    public static byte[] BuildGetFirmwareVersion() => BuildGetReport(ReportFirmwareVersion);
    public static byte[] BuildGetSerialNumber() => BuildGetReport(ReportSerialNumber);
    public static byte[] BuildGetDeviceInfo() => BuildGetReport(ReportDeviceInfo);

    // -------------------------------------------------------------------------
    // Incoming — parsing inner payloads
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parse the inner payload of a CORA frame received on the secondary port,
    /// or a raw USB HID input report.
    /// All Stream Deck input reports start with byte 0 = 0x01; byte 1 selects
    /// the event subtype:
    ///   0x00 = button state (verified live against MK.2)
    ///   0x02 = touch event (Plus / Studio LCD strip — not implemented)
    ///   0x03 = encoder event (Plus rotary encoders — press / rotate)
    ///   0x04 = NFC scan (rare; not implemented)
    ///   0x0a = keep-alive ping
    ///   0x0b = capabilities push (typically primary-port only)
    /// HID feature reports (replies to GET_REPORT for 0x05 / 0x06 / 0x0B) are
    /// distinguished by byte 0 != 0x01 and surfaced as raw bytes.
    /// </summary>
    public static StreamDeckIncomingEvent Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
            return new UnknownEvent(payload.ToArray());

        if (payload[0] != ReportButtonStateInput)
        {
            // Replies to GET_REPORT for 0x05 / 0x06 / 0x0B etc. — surface the
            // raw report so callers that know the per-report layout can
            // decode it. (e.g. 0x06 is length-prefixed ASCII serial:
            // payload[1]=length, then ASCII chars; 0x05 is firmware string
            // starting around payload[5].)
            return new HidFeatureReportEvent(payload[0], payload.Slice(1).ToArray());
        }

        byte sub = payload[1];

        // Keep-alive ping shared with the primary port.
        if (sub == StreamDeckPrimaryProtocol.EventKeepAlive)
        {
            byte conn = payload.Length > 5 ? payload[5] : (byte)0;
            return new KeepAlivePingEvent(conn);
        }

        // Encoder event (Stream Deck Plus / Studio): byte 4 selects the
        // sub-subtype (0 = press bitmap, 1 = rotation deltas) and the per-
        // encoder bytes start at byte 5. Format from cliffrowley's HID
        // notes + SKAARHOJ go-streamdeck — verified pattern, untested live
        // for the Network Dock case (no Plus-on-dock hardware available).
        if (sub == 0x03)
        {
            if (payload.Length < 6) return new UnknownEvent(payload.ToArray());
            bool isRotation = payload[4] == 1;
            int encoderCount = Math.Min(8, payload.Length - 5);
            if (encoderCount <= 0) return new UnknownEvent(payload.ToArray());

            if (isRotation)
            {
                var deltas = new sbyte[encoderCount];
                for (int i = 0; i < encoderCount; i++)
                    deltas[i] = (sbyte)payload[5 + i];
                return new EncoderRotateEvent(deltas);
            }
            else
            {
                var pressed = new bool[encoderCount];
                for (int i = 0; i < encoderCount; i++)
                    pressed[i] = payload[5 + i] == 1;
                return new EncoderPressEvent(pressed);
            }
        }

        // Button state (sub == 0x00). payload[2] = button count, then one
        // byte per key starting at payload[4].
        if (sub == 0x00)
        {
            int count = payload.Length > 2 ? payload[2] : 0;
            if (count <= 0 || 4 + count > payload.Length)
                return new UnknownEvent(payload.ToArray());

            var states = new bool[count];
            for (int i = 0; i < count; i++)
                states[i] = payload[4 + i] == 1;
            return new ButtonStateEvent(states);
        }

        // Touch (sub == 0x02), NFC (sub == 0x04), capabilities (sub == 0x0b)
        // and any unknown sub-event fall through here.
        return new UnknownEvent(payload.ToArray());
    }

    /// <summary>
    /// Decode a length-prefixed ASCII string from a HID feature report
    /// (e.g. report 0x06 serial number layout: <c>06 0E [14 ASCII bytes]</c>).
    /// </summary>
    public static string DecodeLengthPrefixedString(HidFeatureReportEvent report)
    {
        if (report.Body.Length < 1) return string.Empty;
        int len = report.Body[0];
        if (len == 0 || 1 + len > report.Body.Length) return string.Empty;
        return System.Text.Encoding.ASCII.GetString(report.Body, 1, len);
    }
}
