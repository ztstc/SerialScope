using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SerialScope.Models;

namespace SerialScope.Services;

public interface IProtocolDecoder
{
    IReadOnlyList<double[]> Decode(ReadOnlySpan<byte> data);
    int ConsumeMalformedCount();
    void Reset();
}

public static class ProtocolDecoderFactory
{
    public static IProtocolDecoder Create(ProtocolKind kind) => kind switch
    {
        ProtocolKind.FireWater => new FireWaterDecoder(),
        ProtocolKind.JustFloat => new JustFloatDecoder(),
        _ => new RawDataDecoder(),
    };
}

/// <summary>FireWater: comma separated ASCII numbers, one frame per newline.</summary>
public sealed class FireWaterDecoder : IProtocolDecoder
{
    private const int MaxFrameBytes = 64 * 1024;
    private readonly List<byte> _buffer = new(8192);
    private int _malformed;

    public IReadOnlyList<double[]> Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length > 0)
        {
            foreach (byte value in data)
            {
                _buffer.Add(value);
            }
        }

        List<double[]>? frames = null;
        int consumed = 0;

        while (true)
        {
            int newline = _buffer.IndexOf((byte)'\n', consumed);
            if (newline < 0)
            {
                break;
            }

            int lineStart = consumed;
            int lineLength = newline - lineStart;
            consumed = newline + 1;

            if (lineLength > 0 && _buffer[newline - 1] == (byte)'\r')
            {
                lineLength--;
            }

            if (lineLength <= 0)
            {
                continue;
            }

            if (lineLength > 4096)
            {
                _malformed++;
                continue;
            }

            string line = Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(_buffer).Slice(lineStart, lineLength)).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            double[]? values = ParseLine(line);
            if (values is null)
            {
                _malformed++;
                continue;
            }

            frames ??= new List<double[]>(8);
            frames.Add(values);
        }

        if (consumed > 0)
        {
            _buffer.RemoveRange(0, consumed);
        }

        if (_buffer.Count > MaxFrameBytes)
        {
            _buffer.Clear();
            _malformed++;
        }

        return frames ?? (IReadOnlyList<double[]>)Array.Empty<double[]>();
    }

    private static double[]? ParseLine(string line)
    {
        string[] parts = line.Split(new[] { ',', ';', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        int count = Math.Min(parts.Length, AcquisitionEngine.MaxChannels);
        var values = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || double.IsInfinity(value))
            {
                return null;
            }

            values[i] = value;
        }

        return values;
    }

    public int ConsumeMalformedCount()
    {
        int value = _malformed;
        _malformed = 0;
        return value;
    }

    public void Reset()
    {
        _buffer.Clear();
        _malformed = 0;
    }
}

/// <summary>JustFloat: little-endian float32 values terminated by 00 00 80 7F (+Infinity).</summary>
public sealed class JustFloatDecoder : IProtocolDecoder
{
    private static ReadOnlySpan<byte> Tail => new byte[] { 0x00, 0x00, 0x80, 0x7F };
    private const int MaxBufferedBytes = 64 * 1024;
    private readonly List<byte> _buffer = new(4096);
    private int _malformed;

    public IReadOnlyList<double[]> Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length > 0)
        {
            foreach (byte value in data)
            {
                _buffer.Add(value);
            }
        }

        List<double[]>? frames = null;

        while (true)
        {
            ReadOnlySpan<byte> span = CollectionsMarshal.AsSpan(_buffer);
            int tailIndex = span.IndexOf(Tail);
            if (tailIndex < 0)
            {
                break;
            }

            ReadOnlySpan<byte> payload = span[..tailIndex];
            if (payload.Length > 0 && payload.Length % sizeof(float) == 0)
            {
                int valuesInFrame = payload.Length / sizeof(float);
                if (valuesInFrame <= AcquisitionEngine.MaxChannels)
                {
                    var values = new double[valuesInFrame];
                    for (int i = 0; i < valuesInFrame; i++)
                    {
                        float raw = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(i * sizeof(float), sizeof(float)));
                        values[i] = float.IsFinite(raw) ? raw : double.NaN;
                    }

                    frames ??= new List<double[]>(8);
                    frames.Add(values);
                }
                else
                {
                    _malformed++;
                }
            }
            else if (payload.Length > 0)
            {
                _malformed++;
            }

            _buffer.RemoveRange(0, tailIndex + Tail.Length);
        }

        if (_buffer.Count > MaxBufferedBytes)
        {
            // Keep a few bytes in case the tail itself was split across reads.
            _buffer.RemoveRange(0, _buffer.Count - (Tail.Length - 1));
            _malformed++;
        }

        return frames ?? (IReadOnlyList<double[]>)Array.Empty<double[]>();
    }

    public int ConsumeMalformedCount()
    {
        int value = _malformed;
        _malformed = 0;
        return value;
    }

    public void Reset()
    {
        _buffer.Clear();
        _malformed = 0;
    }
}

/// <summary>RawData does not produce waveform frames.</summary>
public sealed class RawDataDecoder : IProtocolDecoder
{
    public IReadOnlyList<double[]> Decode(ReadOnlySpan<byte> data) => Array.Empty<double[]>();
    public int ConsumeMalformedCount() => 0;
    public void Reset() { }
}
