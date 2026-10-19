using Microsoft.VisualStudio.TestTools.UnitTesting;
using Trdo.Helpers;

namespace Trdo.Tests;

/// <summary>
/// Covers the header-only size sniffing that lets the app shrink oversized album art before
/// decoding it, and the store that keeps embedded art out of base64 strings.
/// </summary>
[TestClass]
public sealed class AlbumArtMemoryTests
{
    [TestMethod]
    public void Png_ReadsDimensionsFromIhdr()
    {
        byte[] png = new byte[33];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(png, 0);
        // IHDR: 3000 x 2000
        png[18] = 0x0B;
        png[19] = 0xB8;
        png[22] = 0x07;
        png[23] = 0xD0;

        Assert.IsTrue(ImageFormat.TryGetDimensions(png, out int width, out int height));
        Assert.AreEqual(3000, width);
        Assert.AreEqual(2000, height);
    }

    [TestMethod]
    public void Jpeg_SkipsAppSegmentsToReachFrameHeader()
    {
        byte[] jpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x06, 0x00, 0x00, 0x00, 0x00, // APP0, 4 bytes of payload
            0xFF, 0xC4, 0x00, 0x04, 0x00, 0x00,             // DHT must not be mistaken for a frame
            0xFF, 0xC0, 0x00, 0x11, 0x08,                   // SOF0, precision 8
            0x0B, 0xB8,                                     // height 3000
            0x07, 0xD0,                                     // width 2000
            0x03, 0x01, 0x22, 0x00,
        ];

        Assert.IsTrue(ImageFormat.TryGetDimensions(jpeg, out int width, out int height));
        Assert.AreEqual(2000, width);
        Assert.AreEqual(3000, height);
    }

    [TestMethod]
    public void Gif_ReadsLogicalScreenSize()
    {
        byte[] gif = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x40, 0x01, 0xF0, 0x00];

        Assert.IsTrue(ImageFormat.TryGetDimensions(gif, out int width, out int height));
        Assert.AreEqual(320, width);
        Assert.AreEqual(240, height);
    }

    [TestMethod]
    public void TruncatedOrUnknownData_ReportsNoDimensions()
    {
        Assert.IsFalse(ImageFormat.TryGetDimensions([0xFF, 0xD8, 0xFF, 0xE0], out _, out _));
        Assert.IsFalse(ImageFormat.TryGetDimensions([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], out _, out _));
        Assert.IsFalse(ImageFormat.TryGetDimensions([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12], out _, out _));
    }

    [TestMethod]
    public void EmbeddedArt_SameContentSharesOneUrlAndRoundTrips()
    {
        byte[] image = [0xFF, 0xD8, 0xFF, 0x01, 0x02, 0x03, 0xA1];

        string? first = EmbeddedArtStore.Register(image);
        string? second = EmbeddedArtStore.Register(image);

        Assert.IsNotNull(first);
        Assert.AreEqual(first, second);
        Assert.IsTrue(EmbeddedArtStore.IsEmbeddedUrl(first));
        CollectionAssert.AreEqual(image, EmbeddedArtStore.TryGet(first!));
    }

    [TestMethod]
    public void EmbeddedArt_OldestPictureIsEvictedPastTheLimit()
    {
        string?[] urls = new string?[EmbeddedArtStore.MaxEntries + 1];
        for (int i = 0; i < urls.Length; i++)
        {
            urls[i] = EmbeddedArtStore.Register([0xFF, 0xD8, 0xFF, 0xEE, 0xEE, (byte)i]);
        }

        Assert.IsNull(EmbeddedArtStore.TryGet(urls[0]!));
        Assert.IsNotNull(EmbeddedArtStore.TryGet(urls[^1]!));
    }

    [TestMethod]
    public void EmbeddedArt_EmptyOrOversizedImageIsRejected()
    {
        Assert.IsNull(EmbeddedArtStore.Register([]));
        Assert.IsNull(EmbeddedArtStore.Register(new byte[EmbeddedArtStore.MaxImageBytes + 1]));
    }
}
