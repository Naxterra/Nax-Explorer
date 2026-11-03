using System.Text;

namespace NaxExplorer.Core;

/// <summary>Decides whether a file holds text, and in which encoding, from its first bytes.</summary>
public static class TextSniffer
{
    public const int SampleSize = 8192;

    private static readonly Encoding Utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static TextSniffer()
    {
        // Code page 1252 is only available once the provider is registered, so it is set here and not in an initializer.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Legacy = Encoding.GetEncoding(1252);
    }

    /// <summary>The Windows code page that text without a byte order mark falls back to when it is not valid UTF-8.</summary>
    public static Encoding Legacy { get; }

    /// <summary>
    /// The encoding of the sample, or null when it looks like binary data. A byte order mark wins. Without one, NUL
    /// bytes mean binary unless they fit UTF-16 text, valid UTF-8 means UTF-8, and anything else is Windows-1252.
    /// </summary>
    public static Encoding? Detect(ReadOnlySpan<byte> sample)
    {
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return Encoding.UTF8;
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00])) return Encoding.UTF32;
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return Encoding.Unicode;
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return Encoding.BigEndianUnicode;
        if (sample.Length == 0) return Encoding.UTF8;

        int zeros = 0, zerosAtOdd = 0, zerosAtEven = 0, controls = 0;
        for (int i = 0; i < sample.Length; i++)
        {
            byte b = sample[i];
            if (b == 0)
            {
                zeros++;
                if ((i & 1) == 1) zerosAtOdd++;
                else zerosAtEven++;
            }
            else if (b < 32 && b is not (9 or 10 or 12 or 13 or 27))
            {
                controls++;
            }
        }
        if (zeros > 0)
        {
            // ASCII text in UTF-16 has a zero in every second byte and almost nowhere else.
            int half = sample.Length / 2;
            if (zerosAtOdd > half * 0.6 && zerosAtEven < half * 0.05) return Encoding.Unicode;
            if (zerosAtEven > half * 0.6 && zerosAtOdd < half * 0.05) return Encoding.BigEndianUnicode;
            return null;
        }
        if (controls > sample.Length / 50 + 1) return null;
        return IsUtf8(sample) ? Encoding.UTF8 : Legacy;
    }

    private static bool IsUtf8(ReadOnlySpan<byte> sample)
    {
        // A sample may end in the middle of a character, so up to three trailing bytes are allowed to be incomplete.
        for (int cut = 0; cut <= 3 && cut < sample.Length; cut++)
        {
            try
            {
                Utf8Strict.GetCharCount(sample[..^cut]);
                return true;
            }
            catch (DecoderFallbackException)
            {
            }
        }
        return false;
    }

    /// <summary>Opens a file for reading without stopping other programs from writing, renaming or deleting it.</summary>
    public static FileStream OpenShared(string path, int bufferSize = 64 * 1024) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, FileOptions.SequentialScan);

    /// <summary>
    /// Reads the start of a text file for the preview. Returns null for binary files. <paramref name="truncated"/> tells
    /// whether the file is longer than what was read.
    /// </summary>
    public static string? ReadPreview(string path, int maxChars, out bool truncated)
    {
        truncated = false;
        using FileStream stream = OpenShared(path);
        byte[] sample = new byte[SampleSize];
        int read = stream.ReadAtLeast(sample, sample.Length, throwOnEndOfStream: false);
        Encoding? encoding = Detect(sample.AsSpan(0, read));
        if (encoding is null) return null;
        stream.Position = 0;
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        char[] buffer = new char[maxChars];
        int chars = reader.ReadBlock(buffer, 0, buffer.Length);
        truncated = reader.Peek() >= 0;
        return new string(buffer, 0, chars);
    }
}
