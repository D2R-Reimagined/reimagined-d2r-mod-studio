using System.Text;

namespace ModStudio.Core;

public static class TextFileEncoding
{
    public static (Encoding Encoding, int Preamble) Detect(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 254) return (new UnicodeEncoding(false, false, true), 2);
        if (bytes.Length >= 2 && bytes[0] == 254 && bytes[1] == 255) return (new UnicodeEncoding(true, false, true), 2);
        try { var utf8 = new UTF8Encoding(false, true); _ = utf8.GetString(bytes); return (utf8, 0); }
        catch (DecoderFallbackException) { return (Encoding.Latin1, 0); }
    }
    public static bool LooksLikeText(byte[] bytes)
    {
        var (encoding, preamble) = Detect(bytes);
        try { return !encoding.GetString(bytes, preamble, bytes.Length - preamble).Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n' or '\f')); }
        catch (DecoderFallbackException) { return false; }
    }
}
