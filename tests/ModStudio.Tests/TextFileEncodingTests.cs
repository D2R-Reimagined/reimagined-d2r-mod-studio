using System.Text;
using ModStudio.Core;

internal static class TextFileEncodingTests
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var character in new[] { "é", "者", "😀" })
        {
            var encoded = Encoding.UTF8.GetBytes(character);
            for (int split = 1; split < encoded.Length; split++)
            {
                var prefix = Encoding.UTF8.GetBytes(new string(' ', 8192 - split)).Concat(encoded.Take(split)).ToArray();
                check(TextFileEncoding.LooksLikeText(prefix, isPrefix: true) && TextFileEncoding.Detect(prefix, isPrefix: true).Encoding.CodePage == Encoding.UTF8.CodePage,
                    $"UTF8 probe retains Unicode encoding at byte {split} of {encoded.Length}");
            }
        }
        foreach (var encoding in new[] { Encoding.Unicode, Encoding.BigEndianUnicode })
        {
            var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("text 😀")).ToArray();
            for (int tail = 1; tail <= 3; tail++)
                check(TextFileEncoding.LooksLikeText(bytes[..^tail], isPrefix: true), $"UTF16 {encoding.CodePage} probe buffers partial code units and surrogate pairs ({tail} bytes)");
            check(!TextFileEncoding.LooksLikeText(bytes[..^1]), "Complete UTF16 file still rejects a truncated code unit");
        }
        check(!TextFileEncoding.LooksLikeText([0x41, 0, 0xE8], isPrefix: true), "Unicode prefix handling does not hide binary control bytes");
        check(!TextFileEncoding.LooksLikeText([0x41, 0x80, 0xE8], isPrefix: true), "Invalid UTF8 inside the probe still falls back and detects binary controls");
        check(TextFileEncoding.LooksLikeText([0x63, 0x61, 0x66, 0xE9]), "Complete Latin1 text retains its existing fallback");
    }
}
