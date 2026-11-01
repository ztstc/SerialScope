namespace SerialScope.Services;

public readonly record struct ChannelSummary(bool HasData, double Last, double Minimum, double Maximum);

public readonly record struct WaveformSnapshot(
    double[] Times,
    double[][] Series,
    double StartTime,
    double EndTime)
{
    public static WaveformSnapshot Empty { get; } = new(Array.Empty<double>(), Array.Empty<double[]>(), 0, 0);
    public int SampleCount => Times.Length;
    public int ChannelCount => Series.Length;
    public bool HasData => SampleCount > 0 && ChannelCount > 0;
}

/// <summary>
/// Thread-safe ring buffer. Frames are written by the acquisition thread and read by the
/// renderer at display refresh rate. The buffer keeps the most recent <see cref="Capacity"/> frames.
/// </summary>
public sealed class WaveformBuffer
{
    public const int DefaultCapacity = 120_000;

    private readonly object _gate = new();
    private readonly double[] _times;
    private double[][] _series;
    private readonly double[] _last;
    private readonly double[] _minimum;
    private readonly double[] _maximum;
    private readonly bool[] _hasData;
    private int _start;
    private int _count;
    private int _channelCount;

    public WaveformBuffer(int capacity = DefaultCapacity)
    {
        if (capacity < 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Capacity = capacity;
        _times = new double[capacity];
        _series = new double[0][];
        _last = new double[AcquisitionEngine.MaxChannels];
        _minimum = new double[AcquisitionEngine.MaxChannels];
        _maximum = new double[AcquisitionEngine.MaxChannels];
        _hasData = new bool[AcquisitionEngine.MaxChannels];
    }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public int ChannelCount
    {
        get
        {
            lock (_gate)
            {
                return _channelCount;
            }
        }
    }

    public double LatestTime
    {
        get
        {
            lock (_gate)
            {
                return _count == 0 ? 0 : _times[Index(_count - 1)];
            }
        }
    }

    public double EarliestTime
    {
        get
        {
            lock (_gate)
            {
                return _count == 0 ? 0 : _times[_start];
            }
        }
    }

    public void ConfigureChannels(int channelCount)
    {
        channelCount = Math.Clamp(channelCount, 0, AcquisitionEngine.MaxChannels);
        lock (_gate)
        {
            _channelCount = channelCount;
            _start = 0;
            _count = 0;
            ResetStatistics();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _channelCount = 0;
            _start = 0;
            _count = 0;
            ResetStatistics();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _start = 0;
            _count = 0;
            ResetStatistics();
        }
    }

    public void Append(ReadOnlySpan<double> values, double timestamp)
    {
        lock (_gate)
        {
            if (_channelCount == 0 || values.Length < _channelCount)
            {
                return;
            }

            // Since int.MaxValue is divisible by capacity there is no ambiguity between
            // full and empty states: _count is tracked separately.
            if (_count == Capacity)
            {
                _start = (_start + 1) % Capacity;
                _count--;
            }

            int write = (_start + _count) % Capacity;
            _times[write] = timestamp;
            for (int channel = 0; channel < _channelCount; channel++)
            {
                double value = values[channel];
                _series[channel][write] = value;
                if (double.IsFinite(value))
                {
                    _last[channel] = value;
                    if (!_hasData[channel])
                    {
                        _minimum[channel] = value;
                        _maximum[channel] = value;
                        _hasData[channel] = true;
                    }
                    else
                    {
                        if (value < _minimum[channel])
                        {
                            _minimum[channel] = value;
                        }

                        if (value > _maximum[channel])
                        {
                            _maximum[channel] = value;
                        }
                    }
                }
            }

            _count++;
        }
    }

    public ChannelSummary[] GetSummaries()
    {
        lock (_gate)
        {
            var summaries = new ChannelSummary[_channelCount];
            for (int i = 0; i < _channelCount; i++)
            {
                summaries[i] = new ChannelSummary(_hasData[i], _last[i], _minimum[i], _maximum[i]);
            }

            return summaries;
        }
    }

    public WaveformSnapshot GetSnapshot(double windowSeconds, double? endTime, int maxPoints)
    {
        windowSeconds = Math.Clamp(windowSeconds, 0.1, 600);
        maxPoints = Math.Clamp(maxPoints, 64, 8192);

        lock (_gate)
        {
            if (_count == 0 || _channelCount == 0)
            {
                return WaveformSnapshot.Empty;
            }

            double latest = _times[Index(_count - 1)];
            double end = Math.Min(endTime ?? latest, latest);
            if (end < _times[_start])
            {
                end = latest;
            }

            double start = end - windowSeconds;
            int first = LowerBound(start);
            int visible = _count - first;
            if (visible <= 0)
            {
                return WaveformSnapshot.Empty;
            }

            int stride = Math.Max(1, (int)Math.Ceiling(visible / (double)maxPoints));
            int outputCount = (visible + stride - 1) / stride;
            var times = new double[outputCount];
            var series = new double[_channelCount][];
            for (int channel = 0; channel < _channelCount; channel++)
            {
                series[channel] = new double[outputCount];
            }

            int outIndex = 0;
            for (int i = first; i < _count && outIndex < outputCount; i += stride, outIndex++)
            {
                int source = Index(i);
                times[outIndex] = _times[source];
                for (int channel = 0; channel < _channelCount; channel++)
                {
                    series[channel][outIndex] = _series[channel][source];
                }
            }

            return new WaveformSnapshot(times, series, start, end);
        }
    }

    public (double[] Times, double[][] Series) ExportAll()
    {
        lock (_gate)
        {
            var times = new double[_count];
            var series = new double[_channelCount][];
            for (int channel = 0; channel < _channelCount; channel++)
            {
                series[channel] = new double[_count];
            }

            for (int i = 0; i < _count; i++)
            {
                int source = Index(i);
                times[i] = _times[source];
                for (int channel = 0; channel < _channelCount; channel++)
                {
                    series[channel][i] = _series[channel][source];
                }
            }

            return (times, series);
        }
    }

    private int Index(int logicalIndex) => (_start + logicalIndex) % Capacity;

    private int LowerBound(double timestamp)
    {
        int low = 0;
        int high = _count;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (_times[Index(mid)] < timestamp)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    public void EnsureChannelArrays()
    {
        lock (_gate)
        {
            EnsureChannelArraysLocked();
        }
    }

    private void ResetStatistics()
    {
        Array.Clear(_last);
        Array.Clear(_minimum);
        Array.Clear(_maximum);
        Array.Clear(_hasData);
        EnsureChannelArraysLocked();
    }

    private void EnsureChannelArraysLocked()
    {
        if (_series.Length == _channelCount)
        {
            return;
        }

        var arrays = new double[_channelCount][];
        for (int i = 0; i < _channelCount; i++)
        {
            arrays[i] = new double[Capacity];
        }

        _series = arrays;
    }
}
