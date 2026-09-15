using System.Text;

namespace ModStudio.Core;

public static class TextFileEncoding
{
    public static (Encoding Encoding, int Preamble) Detect(byte[] bytes, bool isPrefix = false)
    {
        if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 254) return (new UnicodeEncoding(false, false, true), 2);
        if (bytes.Length >= 2 && bytes[0] == 254 && bytes[1] == 255) return (new UnicodeEncoding(true, false, true), 2);
        try { var utf8 = new UTF8Encoding(false, true); _ = utf8.GetDecoder().GetCharCount(bytes, 0, bytes.Length, flush: !isPrefix); return (utf8, 0); }
        catch (DecoderFallbackException) { return (Encoding.Latin1, 0); }
    }
    /// <summary>A prefix may end partway through a Unicode character. Buffer that tail rather than treating it as invalid encoding.</summary>
    public static bool LooksLikeText(byte[] bytes, bool isPrefix = false)
    {
        var (encoding, preamble) = Detect(bytes, isPrefix);
        try
        {
            var chars = new char[encoding.GetMaxCharCount(bytes.Length - preamble)];
            var count = encoding.GetDecoder().GetChars(bytes, preamble, bytes.Length - preamble, chars, 0, flush: !isPrefix);
            return !chars.Take(count).Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n' or '\f'));
        }
        catch (DecoderFallbackException) { return false; }
    }
}
