using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;
using Trdo.Services.Metadata;

namespace Trdo.Tests;

/// <summary>
/// ICY metadata has no charset signaling. A Russian listener reported station titles shown as
/// "Ðàéìîíä Ïàóëñ" - Windows-1251 bytes decoded as Latin-1.
/// </summary>
[TestClass]
public sealed class IcyTextDecoderTests
{
    private static byte[] Cp1251(string text) =>
        CodePagesEncodingProvider.Instance.GetEncoding(1251)!.GetBytes(text);

    [TestMethod]
    public void Utf8_IsDecodedAsUtf8()
    {
        const string title = "Раймонд Паулс - Долгая Дорога В Дюнах";
        Assert.AreEqual(title, IcyTextDecoder.Decode(Encoding.UTF8.GetBytes(title)));
    }

    [TestMethod]
    public void Windows1251Cyrillic_IsDecodedAsCyrillic()
    {
        const string title = "Раймонд Паулс - Долгая Дорога В Дюнах";
        Assert.AreEqual(title, IcyTextDecoder.Decode(Cp1251(title)));
    }

    [TestMethod]
    public void Windows1251Cyrillic_WithAsciiArtist_IsDecodedAsCyrillic()
    {
        const string title = "StreamTitle='DJ Smash - Моя Любовь';";
        Assert.AreEqual(title, IcyTextDecoder.Decode(Cp1251(title)));
    }

    [TestMethod]
    public void LegacyWesternText_FallsBackToLatin1()
    {
        const string title = "Björk - Café del Mar";
        Assert.AreEqual(title, IcyTextDecoder.Decode(Encoding.Latin1.GetBytes(title)));
    }

    [TestMethod]
    public void Ascii_IsUnchanged()
    {
        Assert.AreEqual("Daft Punk - Something About Us", IcyTextDecoder.Decode(Encoding.ASCII.GetBytes("Daft Punk - Something About Us")));
    }
}
