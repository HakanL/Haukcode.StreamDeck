using Haukcode.StreamDeck.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Haukcode.StreamDeck.Tests;

/// <summary>
/// Validates the dependency-free blank JPEG generator against a real JPEG
/// decoder (ImageSharp, test-only dependency): the output must decode, have
/// the requested dimensions, and be solid black.
/// </summary>
public class KeyImageEncoderTests
{
    [Theory]
    [InlineData(72, 72)]    // MK.2 key
    [InlineData(96, 96)]    // XL key
    [InlineData(120, 120)]  // Plus key
    [InlineData(144, 112)]  // Studio key
    [InlineData(800, 100)]  // Plus LCD strip
    [InlineData(100, 100)]
    [InlineData(71, 71)]    // non-multiple-of-8 dimensions
    [InlineData(1, 1)]
    public void CreateBlankJpeg_DecodesToSolidBlackAtRequestedSize(int width, int height)
    {
        byte[] jpeg = KeyImageEncoder.CreateBlankJpeg(width, height);

        using var image = Image.Load<Rgba32>(jpeg);

        Assert.Equal(width, image.Width);
        Assert.Equal(height, image.Height);

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                foreach (var pixel in accessor.GetRowSpan(y))
                {
                    Assert.Equal(0, pixel.R);
                    Assert.Equal(0, pixel.G);
                    Assert.Equal(0, pixel.B);
                }
            }
        });
    }

    [Fact]
    public void CreateBlankJpeg_ZeroSizeFallsBackTo72()
    {
        byte[] jpeg = KeyImageEncoder.CreateBlankJpeg(0, 0);

        using var image = Image.Load<Rgba32>(jpeg);

        Assert.Equal(72, image.Width);
        Assert.Equal(72, image.Height);
    }
}
