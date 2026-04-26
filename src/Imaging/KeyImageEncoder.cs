namespace Haukcode.StreamDeck.Imaging;

/// <summary>
/// Encodes <see cref="Image{Rgba32}"/> frames into the byte format required by
/// Stream Deck devices. Handles JPEG quality, optional 180° rotation, and
/// resizing to the target key image dimensions.
/// </summary>
public static class KeyImageEncoder
{
    /// <summary>Default JPEG quality used for key image encoding.</summary>
    public const int DefaultJpegQuality = 90;

    /// <summary>
    /// Encode <paramref name="image"/> as a JPEG byte array at the device's
    /// native key image resolution. Resizes to
    /// <paramref name="targetWidth"/> × <paramref name="targetHeight"/> if
    /// the source is a different size.
    /// </summary>
    /// <param name="image">Source image (any resolution, any pixel format).</param>
    /// <param name="targetWidth">Target key image width in pixels.</param>
    /// <param name="targetHeight">Target key image height in pixels.</param>
    /// <param name="quality">JPEG quality 1–100. Default 90.</param>
    /// <param name="rotate180">Rotate 180° before encoding. Required by some USB HID models.</param>
    public static byte[] EncodeJpeg(
        Image<Rgba32> image,
        int targetWidth,
        int targetHeight,
        int quality = DefaultJpegQuality,
        bool rotate180 = false)
    {
        using var processed = image.Clone(ctx =>
        {
            ctx.Resize(targetWidth, targetHeight);
            if (rotate180)
                ctx.Rotate(RotateMode.Rotate180);
        });

        using var ms = new MemoryStream();
        processed.Save(ms, new JpegEncoder { Quality = quality });
        return ms.ToArray();
    }

    /// <summary>
    /// Create a solid black JPEG at the given dimensions. Used during device
    /// activation to push the dock out of its built-in setup-mode screen —
    /// the deck ignores its setup screen once any image has been received.
    /// </summary>
    public static byte[] CreateBlankJpeg(int width, int height, int quality = DefaultJpegQuality)
    {
        if (width <= 0) width = 72;
        if (height <= 0) height = 72;
        using var image = new Image<Rgba32>(width, height);
        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder { Quality = quality });
        return ms.ToArray();
    }
}
