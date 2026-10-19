using System;
using System.Text;

namespace Trdo.Services.Metadata;

/// <summary>
/// Decodes raw ICY metadata bytes. The protocol has no charset signaling, so this works out
/// the most plausible encoding from the bytes themselves.
/// </summary>
public static class IcyTextDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // CodePagesEncodingProvider ships in the shared framework, so no package is needed, and
    // using the instance directly avoids registering a process-wide provider.
    private static readonly Lazy<Encoding?> Windows1251 = new(() =>
    {
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(1251);
        }
        catch (Exception)
        {
            return null;
        }
    });

    /// <summary>
    /// Tries strict UTF-8 first (the common case). Otherwise the bytes came from a legacy
    /// codepage: Windows-1251 when they look like Cyrillic text, else Latin-1, which maps bytes
    /// 1:1 so nothing is lost to U+FFFD.
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding? cyrillic = Windows1251.Value;
            return cyrillic is not null && LooksLikeWindows1251Cyrillic(bytes)
                ? cyrillic.GetString(bytes)
                : Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>
    /// Cyrillic text in Windows-1251 is made of words whose letters are almost all high bytes
    /// (0xC0-0xFF), whereas Western text in a legacy codepage has the odd accented letter inside
    /// an otherwise ASCII word. Judging word by word keeps "Café" or "Björk" from being read as
    /// Cyrillic while still tolerating ASCII artists and the ICY "StreamTitle=" wrapper.
    /// </summary>
    internal static bool LooksLikeWindows1251Cyrillic(ReadOnlySpan<byte> bytes)
    {
        int runLength = 0;
        int runHigh = 0;
        int longestHighRun = 0;

        // The trailing sentinel flushes the last word.
        for (int i = 0; i <= bytes.Length; i++)
        {
            byte b = i < bytes.Length ? bytes[i] : (byte)0;
            bool isHigh = b >= 0x80;

            if (isHigh && !IsWindows1251CyrillicLetter(b))
            {
                return false;
            }

            if (isHigh || b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z')
            {
                runLength++;
                if (isHigh)
                {
                    runHigh++;
                }

                continue;
            }

            if (runHigh > 0)
            {
                if (runHigh * 10 < runLength * 7)
                {
                    return false;
                }

                longestHighRun = Math.Max(longestHighRun, runHigh);
            }

            runLength = 0;
            runHigh = 0;
        }

        return longestHighRun >= 2;
    }

    private static bool IsWindows1251CyrillicLetter(byte b) =>
        b >= 0xC0
        // Ё ё, plus the Ukrainian/Belarusian letters Є є І і Ї ї Ў ў
        || b is 0xA8 or 0xB8 or 0xAA or 0xBA or 0xB2 or 0xB3 or 0xAF or 0xBF or 0xA1 or 0xA2;
}
