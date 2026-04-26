namespace Haukcode.StreamDeck.Network.Cora;

/// <summary>
/// Strongly-typed event decoded from the inner payload of a CORA frame.
/// Some event types only appear from one of the two ports (primary or
/// secondary); see XML doc on each.
/// </summary>
public abstract record StreamDeckIncomingEvent;

/// <summary>
/// Liveness ping (payload <c>01 0a 02 00 01 [conn_no] ...</c>) — sent by the
/// dock on both primary and secondary ports. The CORA-level ACK uses flag
/// <see cref="CoraFlags.AckNak"/>, echoes the request's <c>messageId</c>, and
/// carries a 32-byte payload starting with <c>03 1a [conn_no]</c>.
/// </summary>
/// <param name="ConnectionNumber">Connection-number byte from the ping payload — must be echoed in the ACK so the dock matches it back to the right virtual link.</param>
public sealed record KeepAlivePingEvent(byte ConnectionNumber) : StreamDeckIncomingEvent;

/// <summary>
/// Reply to a primary-port <c>03 XX</c> feature query (e.g. firmware,
/// serial). The dock uses two response shapes interchangeably:
///   <list type="bullet">
///     <item><b>Length-prefixed</b> (e.g. 0x84 serial): bytes 2..3 are a
///       big-endian uint16 length and exactly that many ASCII bytes follow
///       at byte 4.</item>
///     <item><b>ASCII run with leading 4-byte tag</b> (e.g. 0x81/0x82/0x83
///       versions): the length field is zero, bytes 4..7 are an unknown
///       4-byte tag (looks like a checksum / BCD version), and the human
///       version string begins at byte 8 and is null-terminated.</item>
///   </list>
/// Use <see cref="GetLengthPrefixedString"/> or <see cref="GetAsciiRun"/>
/// depending on which sub-command was queried.
/// </summary>
public sealed record FeatureResponseEvent(byte SubCommand, ushort LengthField, byte[] Body) : StreamDeckIncomingEvent
{
    /// <summary>Decode the first <see cref="LengthField"/> bytes of <see cref="Body"/> as ASCII.</summary>
    public string GetLengthPrefixedString()
    {
        if (LengthField == 0 || LengthField > Body.Length) return string.Empty;
        return System.Text.Encoding.ASCII.GetString(Body, 0, LengthField);
    }

    /// <summary>
    /// Find the longest run of printable ASCII characters in <see cref="Body"/>
    /// starting at <paramref name="offset"/>. Default 4 = post-tag offset for
    /// version replies (since Body starts at byte 4 of the inner packet, the
    /// 4-byte tag is at Body[0..3] and the string starts at Body[4]).
    /// </summary>
    public string GetAsciiRun(int offset = 4)
    {
        if (offset < 0 || offset >= Body.Length) return string.Empty;
        int end = offset;
        while (end < Body.Length && Body[end] >= 0x20 && Body[end] <= 0x7E)
            end++;
        return System.Text.Encoding.ASCII.GetString(Body, offset, end - offset);
    }
}

/// <summary>
/// Capabilities response from the primary port (<c>01 0b ...</c>) — sent
/// unsolicited when a USB Stream Deck plug-event happens, or as the reply
/// to a primary-port <c>03 1c</c> query. Lots of fields are pre-parsed
/// because we always need them when bringing a network surface online.
/// </summary>
/// <param name="IsConnected">True when a USB Stream Deck is plugged into the dock.</param>
/// <param name="ChildSerialNumber">Serial of the connected USB Stream Deck (e.g. "A00SA5202LJPCY"), or empty when nothing's connected.</param>
/// <param name="ChildModelName">Model name of the connected USB Stream Deck (e.g. "Stream Deck MK.2").</param>
/// <param name="ChildVendorId">USB vendor ID (0x0FD9 for Elgato).</param>
/// <param name="ChildProductId">USB product ID (e.g. 0x0080 for Stream Deck MK.2).</param>
/// <param name="SecondaryTcpPort">Dynamic TCP port of the dock's secondary channel — connect here to talk directly to the connected USB Stream Deck. Changes each dock boot.</param>
/// <param name="RawBody">The full body bytes (everything from byte 2 of the inner packet onward), preserved for diagnostics.</param>
public sealed record CapabilitiesEvent(
    bool IsConnected,
    string ChildSerialNumber,
    string ChildModelName,
    ushort ChildVendorId,
    ushort ChildProductId,
    ushort SecondaryTcpPort,
    byte[] RawBody) : StreamDeckIncomingEvent;

/// <summary>
/// HID feature-report reply received on the secondary port (e.g. response
/// to a <see cref="CoraHidOp.GetReport"/> for report ID 0x06 = serial). The
/// payload is the raw HID report bytes including the report ID at byte 0.
/// </summary>
/// <param name="ReportId">HID report ID (byte 0).</param>
/// <param name="Body">Bytes from byte 1 onward — content depends on the report.</param>
public sealed record HidFeatureReportEvent(byte ReportId, byte[] Body) : StreamDeckIncomingEvent;

/// <summary>
/// Button-state event from the secondary port. Format
/// (verified live against an MK.2 + Network Dock):
/// <code>
/// 01 00 0F 00 [k0] [k1] ... [k14] 00 00 ...
/// </code>
/// Byte 2 carries the button count (0x0F = 15 for MK.2). Each per-key byte
/// is 0 = released, 1 = pressed.
/// </summary>
/// <param name="States">One bool per physical key, indexed 0..count-1.</param>
public sealed record ButtonStateEvent(bool[] States) : StreamDeckIncomingEvent;

/// <summary>
/// Encoder press / release state on devices with rotary encoders (Stream
/// Deck Plus). One bool per encoder; index = encoder number.
/// </summary>
public sealed record EncoderPressEvent(bool[] Pressed) : StreamDeckIncomingEvent;

/// <summary>
/// Per-encoder rotation deltas. Each <see cref="sbyte"/> is signed: positive
/// = clockwise, negative = counter-clockwise. One value per encoder; zero
/// means no movement on that encoder this report.
/// </summary>
public sealed record EncoderRotateEvent(sbyte[] Deltas) : StreamDeckIncomingEvent;

/// <summary>Packet that didn't match any known shape — passed through for diagnostics.</summary>
public sealed record UnknownEvent(byte[] RawPayload) : StreamDeckIncomingEvent;
