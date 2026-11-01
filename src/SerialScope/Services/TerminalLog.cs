using System.Text;

namespace SerialScope.Services;

/// <summary>Fixed-size raw byte history used by the terminal page.</summary>
public sealed class TerminalLog
{
    private const int MaxBytes = 192 * 1024;
    private const int MaxChunks = 6000;
    private readonly object _gate = new();
    private readonly Queue<Chunk> _chunks = new();
    private int _byteCount;
    private int _version;

    private readonly record struct Chunk(byte[] Data, DateTime Timestamp);

    public int Version => Volatile.Read(ref _version);

    public int ByteCount
    {
        get
        {
            lock (_gate)
            {
                return _byteCount;
            }
        }
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        var copy = data.ToArray();
        lock (_gate)
        {
            _chunks.Enqueue(new Chunk(copy, DateTime.Now));
            _byteCount += copy.Length;
            while ((_byteCount > MaxBytes || _chunks.Count > MaxChunks) && _chunks.Count > 1)
            {
                Chunk removed = _chunks.Dequeue();
                _byteCount -= removed.Data.Length;
            }
        }

        Interlocked.Increment(ref _version);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _chunks.Clear();
            _byteCount = 0;
        }

        Interlocked.Increment(ref _version);
    }

    public string BuildDisplay(bool hex, bool timestamps, int maxChars = 140_000)
    {
        Chunk[] chunks;
        lock (_gate)
        {
            chunks = _chunks.ToArray();
        }

        var builder = new StringBuilder(Math.Min(maxChars, 32_768));
        foreach (Chunk chunk in chunks)
        {
            string stamp = timestamps ? $"[{chunk.Timestamp:HH:mm:ss.fff}] " : string.Empty;

            if (hex)
            {
                builder.Append(stamp).Append("RX  ");
                for (int i = 0; i < chunk.Data.Length; i++)
                {
                    builder.Append(chunk.Data[i].ToString("X2")).Append(' ');
                    if ((i + 1) % 28 == 0 && i + 1 < chunk.Data.Length)
                    {
                        builder.AppendLine();
                        builder.Append(' ', stamp.Length + 4);
                    }
                }

                builder.AppendLine();
            }
            else
            {
                if (timestamps)
                {
                    builder.Append(stamp);
                }

                AppendSanitizedText(builder, chunk.Data);
            }
        }

        string text = builder.ToString();
        if (text.Length > maxChars)
        {
            int start = text.Length - maxChars;
            int newline = text.IndexOf('\n', start);
            start = newline >= 0 ? newline + 1 : start;
            text = "… 较早的日志已省略 …\r\n" + text[start..];
        }

        return text;
    }

    private static void AppendSanitizedText(StringBuilder builder, byte[] data)
    {
        string text = Encoding.UTF8.GetString(data);
        foreach (char c in text)
        {
            if (c == '\r' || c == '\n' || c == '\t' || c >= ' ')
            {
                builder.Append(c);
            }
        }
    }
}
