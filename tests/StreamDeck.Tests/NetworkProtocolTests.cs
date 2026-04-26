using Haukcode.StreamDeck.Network.Cora;

namespace Haukcode.StreamDeck.Tests;

/// <summary>
/// Tests for the Stream Deck Network Dock TCP protocol layers — <see cref="CoraFrame"/>
/// (link-level framing), <see cref="StreamDeckPrimaryProtocol"/> (5343 dock
/// channel), and <see cref="StreamDeckSecondaryProtocol"/> (dynamic per-deck
/// channel). Byte sequences are lifted verbatim from Wireshark captures of
/// the official Stream Deck software talking to a real Network Dock plus a
/// connected Stream Deck MK.2.
/// </summary>
public class NetworkProtocolTests
{
    // =========================================================================
    // CoraFrame — link-level framing
    // =========================================================================

    [Fact]
    public void CoraFrame_Encode_PrimaryGetSerialRequest_MatchesPcapBytes()
    {
        // From PCAP 1, packet 7: client→dock primary-port GetSerial.
        // Header: magic, flags=0, hidOp=0, msgId=0, payloadLength=1024.
        // Payload starts with 03 84, rest zero-padded.
        var payload = StreamDeckPrimaryProtocol.BuildGetSerial();
        var frame = new CoraFrame(0, 0, 0, payload).Encode();

        Assert.Equal(1040, frame.Length); // 16-byte header + 1024-byte payload
        Assert.Equal(new byte[] { 0x43, 0x93, 0x8A, 0x41 }, frame[..4]);
        Assert.Equal(0, frame[4]); Assert.Equal(0, frame[5]);   // flags
        Assert.Equal(0, frame[6]);                               // hidOp
        Assert.Equal(0, frame[7]);                               // reserved
        for (int i = 8; i < 12; i++) Assert.Equal(0, frame[i]); // msgId
        // payloadLength = 1024 = 0x00000400 LE → 00 04 00 00
        Assert.Equal(0x00, frame[12]);
        Assert.Equal(0x04, frame[13]);
        Assert.Equal(0x00, frame[14]);
        Assert.Equal(0x00, frame[15]);
        Assert.Equal(0x03, frame[16]);
        Assert.Equal(0x84, frame[17]);
    }

    [Fact]
    public void CoraFrame_Decode_PrimaryKeepAlivePing()
    {
        // From PCAP 1, packet 3: dock→client primary-port keep-alive.
        byte[] frameBytes =
        [
            0x43, 0x93, 0x8A, 0x41,
            0x00, 0x00,
            0x00,
            0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x02, 0x00, 0x00, // payloadLength = 512
        ];
        var fullFrame = new byte[16 + 512];
        frameBytes.CopyTo(fullFrame, 0);
        fullFrame[16] = 0x01;
        fullFrame[17] = 0x0A;
        fullFrame[18] = 0x02;
        fullFrame[20] = 0x01;

        Assert.True(CoraFrame.TryDecode(fullFrame, out var frame, out var consumed));
        Assert.Equal(528, consumed);
        Assert.Equal(0, frame.Flags);
        Assert.Equal(0, frame.HidOp);
        Assert.Equal(0u, frame.MessageId);
        Assert.Equal(512, frame.Payload.Length);
        Assert.Equal(0x01, frame.Payload[0]);
        Assert.Equal(0x0A, frame.Payload[1]);
    }

    [Fact]
    public void CoraFrame_Decode_SecondaryGetSerialRequest()
    {
        // From PCAP 2: client→dock secondary-port GetReport(0x06).
        byte[] header =
        [
            0x43, 0x93, 0x8A, 0x41,
            0x00, 0xC0,             // flags = 0xC000 LE
            0x02,                   // hidOp = GetReport
            0x00,
            0xB4, 0x07, 0x00, 0x00, // msgId = 1972
            0x20, 0x00, 0x00, 0x00, // payloadLength = 32
        ];
        var full = new byte[16 + 32];
        header.CopyTo(full, 0);
        full[16] = 0x06;

        Assert.True(CoraFrame.TryDecode(full, out var frame, out var consumed));
        Assert.Equal(48, consumed);
        Assert.Equal<ushort>(0xC000, frame.Flags);
        Assert.Equal(CoraHidOp.GetReport, frame.HidOp);
        Assert.Equal(1972u, frame.MessageId);
        Assert.Equal(32, frame.Payload.Length);
        Assert.Equal(StreamDeckSecondaryProtocol.ReportSerialNumber, frame.Payload[0]);
    }

    [Fact]
    public void CoraFrame_TryDecode_PartialBuffer_ReturnsFalse()
    {
        byte[] partial =
        [
            0x43, 0x93, 0x8A, 0x41,
            0, 0, 0, 0,
            0, 0, 0, 0,
            0x00, 0x04, 0x00, 0x00, // payloadLength = 1024
        ];
        var buf = new byte[16 + 100]; // shy of a full payload
        partial.CopyTo(buf, 0);

        Assert.False(CoraFrame.TryDecode(buf, out _, out _));
    }

    [Fact]
    public void CoraFrame_TryDecode_BadMagic_ReturnsFalse()
    {
        byte[] bad = new byte[20];
        bad[0] = 0xFF; bad[1] = 0xFF; bad[2] = 0xFF; bad[3] = 0xFF;
        Assert.False(CoraFrame.TryDecode(bad, out _, out _));
    }

    [Fact]
    public void CoraFrame_RoundTrip()
    {
        var original = new CoraFrame(
            Flags: CoraFlags.Verbatim | CoraFlags.ReqAck,
            HidOp: CoraHidOp.GetReport,
            MessageId: 0xDEADBEEF,
            Payload: [0x06, 0x01, 0x02, 0x03]);
        var encoded = original.Encode();

        Assert.True(CoraFrame.TryDecode(encoded, out var decoded, out _));
        Assert.Equal(original.Flags, decoded.Flags);
        Assert.Equal(original.HidOp, decoded.HidOp);
        Assert.Equal(original.MessageId, decoded.MessageId);
        Assert.Equal(original.Payload, decoded.Payload);
    }

    // =========================================================================
    // Primary protocol — encoding
    // =========================================================================

    [Fact]
    public void Primary_BuildGetSerial_StartsWith0384()
    {
        var p = StreamDeckPrimaryProtocol.BuildGetSerial();
        Assert.Equal(1024, p.Length);
        Assert.Equal(0x03, p[0]);
        Assert.Equal(0x84, p[1]);
        for (int i = 2; i < p.Length; i++) Assert.Equal(0, p[i]);
    }

    [Fact]
    public void Primary_BuildSetBrightness_ClampsAndEncodes()
    {
        Assert.Equal(50, StreamDeckPrimaryProtocol.BuildSetBrightness(50)[2]);
        Assert.Equal(100, StreamDeckPrimaryProtocol.BuildSetBrightness(150)[2]);
        Assert.Equal(0, StreamDeckPrimaryProtocol.BuildSetBrightness(-10)[2]);
    }

    [Fact]
    public void Primary_BuildKeepAliveAck_Is1024Bytes_EchoesConnectionNumber()
    {
        // Matches the official Stream Deck app's wire pattern observed in
        // PCAP 2: 1024-byte payload, leading 03 1a [conn_no], rest zero.
        var ack = StreamDeckPrimaryProtocol.BuildKeepAliveAck(connectionNumber: 0x05);
        Assert.Equal(1024, ack.Length);
        Assert.Equal(0x03, ack[0]);
        Assert.Equal(0x1A, ack[1]);
        Assert.Equal(0x05, ack[2]);
        for (int i = 3; i < ack.Length; i++) Assert.Equal(0, ack[i]);
    }

    // =========================================================================
    // Primary protocol — decoding
    // =========================================================================

    [Fact]
    public void Primary_Parse_KeepAlivePing_From010A()
    {
        byte[] data = [0x01, 0x0A, 0x02, 0x00, 0x01, 0x00, 0x00, 0x00];
        var ev = Assert.IsType<KeepAlivePingEvent>(StreamDeckPrimaryProtocol.Parse(data));
        Assert.Equal(0, ev.ConnectionNumber);
    }

    [Fact]
    public void Primary_Parse_SerialResponse_From0384()
    {
        // PCAP packet 8: 03 84 00 0e ABXIA54810EUYZ
        byte[] data =
        [
            0x03, 0x84, 0x00, 0x0E,
            0x41, 0x42, 0x58, 0x49, 0x41, 0x35, 0x34, 0x38,
            0x31, 0x30, 0x45, 0x55, 0x59, 0x5A
        ];
        var feat = Assert.IsType<FeatureResponseEvent>(StreamDeckPrimaryProtocol.Parse(data));
        Assert.Equal(0x84, feat.SubCommand);
        Assert.Equal<ushort>(14, feat.LengthField);
        Assert.Equal("ABXIA54810EUYZ", feat.GetLengthPrefixedString());
    }

    [Fact]
    public void Primary_Parse_FirmwareResponse_From0383()
    {
        // Live capture: 03 83 00 00 62 85 62 35 31 2e 30 31 2e 30 31 33 00...
        byte[] data =
        [
            0x03, 0x83, 0x00, 0x00,
            0x62, 0x85, 0x62, 0x35,
            0x31, 0x2E, 0x30, 0x31, 0x2E, 0x30, 0x31, 0x33,
            0x00, 0x00, 0x00
        ];
        var feat = Assert.IsType<FeatureResponseEvent>(StreamDeckPrimaryProtocol.Parse(data));
        Assert.Equal(0x83, feat.SubCommand);
        Assert.Equal<ushort>(0, feat.LengthField);
        Assert.Equal("1.01.013", feat.GetAsciiRun());
    }

    [Fact]
    public void Primary_Parse_CapabilitiesResponse_ExtractsConnectedDevice()
    {
        // Live capture for an MK.2 plugged into a Network Dock.
        var body = new byte[260];
        body[0] = 0x01; body[1] = 0x0B;
        body[2] = 0x7C; body[3] = 0x00;         // length 124
        body[4] = 0x02;                          // connected flag
        body[5] = 0x03; body[6] = 0x05;         // grid type / cols / rows
        body[8] = 0x48; body[9] = 0x00;         // key width 72
        body[10] = 0x48; body[11] = 0x00;       // key height 72
        body[26] = 0xD9; body[27] = 0x0F;       // VID 0x0FD9 (Elgato)
        body[28] = 0x80; body[29] = 0x00;       // PID 0x0080 (MK.2)

        // Model name at offset 62 (verified live — using 64 truncates "Stream Deck MK.2").
        var model = System.Text.Encoding.ASCII.GetBytes("Stream Deck MK.2");
        model.CopyTo(body, 62);

        var serial = System.Text.Encoding.ASCII.GetBytes("A00SA5202LJPCY");
        serial.CopyTo(body, 94);

        // Secondary port 0x4E22 = 20002 (LE: 22 4E)
        body[126] = 0x22; body[127] = 0x4E;

        var ev = Assert.IsType<CapabilitiesEvent>(StreamDeckPrimaryProtocol.Parse(body));
        Assert.True(ev.IsConnected);
        Assert.Equal<ushort>(0x0FD9, ev.ChildVendorId);
        Assert.Equal<ushort>(0x0080, ev.ChildProductId);
        Assert.Equal("Stream Deck MK.2", ev.ChildModelName);
        Assert.Equal("A00SA5202LJPCY", ev.ChildSerialNumber);
        Assert.Equal<ushort>(20002, ev.SecondaryTcpPort);
    }

    [Fact]
    public void Primary_Parse_TruncatedPacket_ReturnsUnknown()
    {
        Assert.IsType<UnknownEvent>(StreamDeckPrimaryProtocol.Parse([0x01]));
        Assert.IsType<UnknownEvent>(StreamDeckPrimaryProtocol.Parse([]));
    }

    [Fact]
    public void Primary_Parse_UnknownTopLevelKind_ReturnsUnknown()
    {
        Assert.IsType<UnknownEvent>(StreamDeckPrimaryProtocol.Parse([0x05, 0x00, 0x00, 0x00]));
    }

    // =========================================================================
    // Secondary protocol — encoding
    // =========================================================================

    [Fact]
    public void Secondary_BuildGetSerialNumber_StartsWith06_32Bytes()
    {
        var p = StreamDeckSecondaryProtocol.BuildGetSerialNumber();
        Assert.Equal(32, p.Length);
        Assert.Equal(StreamDeckSecondaryProtocol.ReportSerialNumber, p[0]);
        for (int i = 1; i < p.Length; i++) Assert.Equal(0, p[i]);
    }

    // =========================================================================
    // Secondary protocol — decoding
    // =========================================================================

    [Fact]
    public void Secondary_Parse_ButtonPress_Key14_FromLivePcap()
    {
        // PCAP 2 button press: 01 00 0F 00 [0..13]=0 [14]=1 then zeros.
        var payload = new byte[64];
        payload[0] = 0x01;
        payload[1] = 0x00;
        payload[2] = 0x0F; // 15 buttons (MK.2)
        payload[3] = 0x00;
        payload[4 + 14] = 0x01; // key 14 pressed

        var ev = Assert.IsType<ButtonStateEvent>(StreamDeckSecondaryProtocol.Parse(payload));
        Assert.Equal(15, ev.States.Length);
        for (int i = 0; i < 14; i++) Assert.False(ev.States[i], $"key {i} should be released");
        Assert.True(ev.States[14]);
    }

    [Fact]
    public void Secondary_Parse_ButtonRelease_AllZeros()
    {
        var payload = new byte[64];
        payload[0] = 0x01;
        payload[2] = 0x0F;

        var ev = Assert.IsType<ButtonStateEvent>(StreamDeckSecondaryProtocol.Parse(payload));
        for (int i = 0; i < 15; i++) Assert.False(ev.States[i]);
    }

    [Fact]
    public void Secondary_Parse_ButtonPress_Key10_FromLivePcap()
    {
        var payload = new byte[64];
        payload[0] = 0x01;
        payload[2] = 0x0F;
        payload[4 + 10] = 0x01;

        var ev = Assert.IsType<ButtonStateEvent>(StreamDeckSecondaryProtocol.Parse(payload));
        Assert.True(ev.States[10]);
        for (int i = 0; i < 15; i++)
            if (i != 10) Assert.False(ev.States[i]);
    }

    [Fact]
    public void Secondary_Parse_KeepAlive_SharesPrimaryFormat()
    {
        byte[] data = [0x01, 0x0A, 0x02, 0x00, 0x01, 0x07, 0x00, 0x00];
        var ev = Assert.IsType<KeepAlivePingEvent>(StreamDeckSecondaryProtocol.Parse(data));
        Assert.Equal(0x07, ev.ConnectionNumber);
    }

    [Fact]
    public void Secondary_Parse_HidFeatureReport_Serial()
    {
        // Live PCAP 2: 06 0E A00SA5202LJPCY
        byte[] payload =
        [
            0x06, 0x0E,
            0x41, 0x30, 0x30, 0x53, 0x41, 0x35, 0x32, 0x30,
            0x32, 0x4C, 0x4A, 0x50, 0x43, 0x59
        ];

        var ev = Assert.IsType<HidFeatureReportEvent>(StreamDeckSecondaryProtocol.Parse(payload));
        Assert.Equal(StreamDeckSecondaryProtocol.ReportSerialNumber, ev.ReportId);
        Assert.Equal("A00SA5202LJPCY", StreamDeckSecondaryProtocol.DecodeLengthPrefixedString(ev));
    }

    // =========================================================================
    // CoraFrameReader — streaming reader
    // =========================================================================

    [Fact]
    public void Reader_DrainsCompleteFrame()
    {
        var reader = new CoraFrameReader();
        var encoded = new CoraFrame(0, 0, 1, [0x03, 0x84]).Encode();
        reader.Append(encoded);

        var frames = reader.DrainFrames();
        Assert.Single(frames);
        Assert.Equal(2, frames[0].Payload.Length);
        Assert.Equal(0x03, frames[0].Payload[0]);
        Assert.Equal(0x84, frames[0].Payload[1]);
    }

    [Fact]
    public void Reader_HandlesByteByByteAppend()
    {
        var reader = new CoraFrameReader();
        var encoded = new CoraFrame(CoraFlags.Verbatim, CoraHidOp.Write, 99, [0x01, 0x02, 0x03]).Encode();

        for (int i = 0; i < encoded.Length - 1; i++)
        {
            reader.Append(encoded.AsSpan(i, 1));
            Assert.Empty(reader.DrainFrames());
        }
        reader.Append(encoded.AsSpan(encoded.Length - 1, 1));
        Assert.Single(reader.DrainFrames());
    }

    [Fact]
    public void Reader_DrainsMultipleFramesInOneAppend()
    {
        var reader = new CoraFrameReader();
        var f1 = new CoraFrame(0, 0, 1, [0x01]).Encode();
        var f2 = new CoraFrame(0, 0, 2, [0x02, 0x03]).Encode();
        var f3 = new CoraFrame(0, 0, 3, [0x04, 0x05, 0x06]).Encode();

        var combined = new byte[f1.Length + f2.Length + f3.Length];
        f1.CopyTo(combined, 0);
        f2.CopyTo(combined, f1.Length);
        f3.CopyTo(combined, f1.Length + f2.Length);

        reader.Append(combined);
        var frames = reader.DrainFrames();
        Assert.Equal(3, frames.Count);
        Assert.Equal(1u, frames[0].MessageId);
        Assert.Equal(2u, frames[1].MessageId);
        Assert.Equal(3u, frames[2].MessageId);
    }

    [Fact]
    public void Reader_SkipsBareByteGreeting()
    {
        // The Network Dock sends bare 00 00 / 00 00 00 00 00 00 packets on
        // connection setup. The reader resyncs to the next CORA magic.
        var reader = new CoraFrameReader();
        var greeting = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        var frame = new CoraFrame(0, 0, 7, [0xAA, 0xBB]).Encode();

        var combined = new byte[greeting.Length + frame.Length];
        greeting.CopyTo(combined, 0);
        frame.CopyTo(combined, greeting.Length);

        reader.Append(combined);
        var frames = reader.DrainFrames();
        Assert.Single(frames);
        Assert.Equal(7u, frames[0].MessageId);
    }

    [Fact]
    public void Reader_HandlesPartialFrame_RetainsRemainder()
    {
        var reader = new CoraFrameReader();
        var frame = new CoraFrame(0, 0, 5, [0x11, 0x22, 0x33, 0x44]).Encode();

        reader.Append(frame.AsSpan(0, 18)); // header + 2 of 4 payload bytes
        Assert.Empty(reader.DrainFrames());

        reader.Append(frame.AsSpan(18));
        var frames = reader.DrainFrames();
        Assert.Single(frames);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, frames[0].Payload);
    }

    [Fact]
    public void Secondary_Parse_HidFeatureReport_Firmware()
    {
        // Live PCAP 2: 05 0c b2 cd b1 35 31 2e 30 32 2e 30 30 30
        byte[] payload =
        [
            0x05, 0x0C, 0xB2, 0xCD, 0xB1, 0x35,
            0x31, 0x2E, 0x30, 0x32, 0x2E, 0x30, 0x30, 0x30
        ];

        var ev = Assert.IsType<HidFeatureReportEvent>(StreamDeckSecondaryProtocol.Parse(payload));
        Assert.Equal(StreamDeckSecondaryProtocol.ReportFirmwareVersion, ev.ReportId);
        Assert.Equal(13, ev.Body.Length);
        Assert.Equal<byte>(0x0C, ev.Body[0]);
    }
}
