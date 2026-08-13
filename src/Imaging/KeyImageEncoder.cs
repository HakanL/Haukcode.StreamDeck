namespace Haukcode.StreamDeck.Imaging;

/// <summary>
/// Dependency-free generation of the solid-black JPEG frames the library
/// needs internally. Callers who want real imagery encode their own JPEGs
/// (any image library) and use the byte[] overloads on
/// <see cref="IStreamDeckDevice"/>.
/// </summary>
public static class KeyImageEncoder
{
    /// <summary>
    /// Create a solid black baseline JPEG at the given dimensions. Used during
    /// device activation to push the dock out of its built-in setup-mode
    /// screen — the deck ignores its setup screen once any image has been
    /// received.
    /// </summary>
    /// <remarks>
    /// Hand-written encoder rather than an image-library dependency: a solid
    /// black image needs only DC coefficients, so the scan is a fixed bit
    /// pattern per 8×8 MCU. Emits a standard 3-component YCbCr 4:4:4 baseline
    /// JFIF that any decoder (and the deck firmware) accepts.
    /// </remarks>
    public static byte[] CreateBlankJpeg(int width, int height)
    {
        if (width <= 0) width = 72;
        if (height <= 0) height = 72;

        using var ms = new MemoryStream();

        // SOI
        ms.WriteByte(0xFF); ms.WriteByte(0xD8);

        // APP0 / JFIF 1.1, no thumbnail
        WriteSegment(ms, 0xE0, [(byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);

        // DQT: single table (id 0) with all coefficients = 16, shared by all
        // components. DC quantizer 16 divides the black-level DC of -1024
        // exactly (-64), so decoded output is exact black.
        var dqt = new byte[65];
        dqt[0] = 0x00; // 8-bit precision, table id 0
        for (int i = 1; i < 65; i++)
            dqt[i] = 16;
        WriteSegment(ms, 0xDB, dqt);

        // SOF0: 8-bit baseline, 3 components, 4:4:4 (1×1 sampling everywhere)
        WriteSegment(ms, 0xC0,
        [
            0x08,
            (byte)(height >> 8), (byte)height,
            (byte)(width >> 8), (byte)width,
            0x03,
            0x01, 0x11, 0x00, // Y:  id 1, 1×1, quant table 0
            0x02, 0x11, 0x00, // Cb: id 2, 1×1, quant table 0
            0x03, 0x11, 0x00, // Cr: id 3, 1×1, quant table 0
        ]);

        // Minimal custom Huffman tables — only the codes the scan actually
        // uses. DHT payload = class/id, 16 code-length counts, then values.
        // Luma DC: category 0 → '0', category 7 → '10' (first block's -64).
        WriteSegment(ms, 0xC4, [0x00, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x00, 0x07]);
        // Luma AC: end-of-block → '0'.
        WriteSegment(ms, 0xC4, [0x10, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x00]);
        // Chroma DC: category 0 → '0'.
        WriteSegment(ms, 0xC4, [0x01, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x00]);
        // Chroma AC: end-of-block → '0'.
        WriteSegment(ms, 0xC4, [0x11, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x00]);

        // SOS: Y uses tables 0/0, chroma uses 1/1
        WriteSegment(ms, 0xDA,
        [
            0x03,
            0x01, 0x00,
            0x02, 0x11,
            0x03, 0x11,
            0x00, 0x3F, 0x00,
        ]);

        // Entropy-coded scan. Per MCU: Y DC, Y EOB, Cb DC, Cb EOB, Cr DC, Cr EOB.
        // Black = Y sample -128 after level shift → DC coefficient -1024 →
        // quantized -64 (category 7, extra bits -64 + 127 = 63). Chroma DC is 0.
        // Every DC after the first block has diff 0, and all AC blocks are EOB,
        // so each MCU past the first is six '0' bits.
        int mcuCount = ((width + 7) / 8) * ((height + 7) / 8);
        var writer = new BitWriter(ms);

        writer.Write(0b10, 2);      // Y DC category 7
        writer.Write(63, 7);        // extra bits for -64
        writer.Write(0, 4);         // Y EOB, Cb DC, Cb EOB... (four '0' codes)
        writer.Write(0, 1);         // ...Cr DC
        writer.Write(0, 1);         // Cr EOB

        for (int i = 1; i < mcuCount; i++)
            writer.Write(0, 6);

        writer.Flush();

        // EOI
        ms.WriteByte(0xFF); ms.WriteByte(0xD9);

        return ms.ToArray();
    }

    private static void WriteSegment(MemoryStream ms, byte marker, byte[] payload)
    {
        int length = payload.Length + 2;
        ms.WriteByte(0xFF);
        ms.WriteByte(marker);
        ms.WriteByte((byte)(length >> 8));
        ms.WriteByte((byte)length);
        ms.Write(payload, 0, payload.Length);
    }

    /// <summary>
    /// MSB-first bit writer with JPEG byte stuffing (0xFF → 0xFF 0x00) and
    /// 1-bit padding of the final partial byte.
    /// </summary>
    private sealed class BitWriter(MemoryStream ms)
    {
        private int bitBuffer;
        private int bitCount;

        public void Write(int value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                this.bitBuffer = (this.bitBuffer << 1) | ((value >> i) & 1);
                this.bitCount++;
                if (this.bitCount == 8)
                    EmitByte();
            }
        }

        public void Flush()
        {
            while (this.bitCount != 0)
            {
                this.bitBuffer = (this.bitBuffer << 1) | 1;
                this.bitCount++;
                if (this.bitCount == 8)
                    EmitByte();
            }
        }

        private void EmitByte()
        {
            byte b = (byte)this.bitBuffer;
            ms.WriteByte(b);
            if (b == 0xFF)
                ms.WriteByte(0x00);
            this.bitBuffer = 0;
            this.bitCount = 0;
        }
    }
}
