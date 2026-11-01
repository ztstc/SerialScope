using System.Diagnostics;
using SerialScope.Models;

namespace SerialScope.Services;

public readonly record struct AcquisitionStats(
    long TotalBytes,
    long TotalFrames,
    long MalformedFrames,
    double BytesPerSecond,
    double FramesPerSecond,
    int ChannelCount,
    int BufferSamples,
    int BufferCapacity,
    double UptimeSeconds);

/// <summary>
/// Owns the active protocol parser, waveform history, terminal history and counters.
/// Data can be fed from the serial thread or the built-in demo generator.
/// </summary>
public sealed class AcquisitionEngine
{
    public const int MaxChannels = 8;

    private readonly object _sync = new();
    private readonly object _statsGate = new();
    private readonly Stopwatch _clock = new();
    private IProtocolDecoder _decoder = new RawDataDecoder();
    private ProtocolKind _protocol = ProtocolKind.FireWater;
    private readonly WaveformBuffer _buffer = new();
    private readonly TerminalLog _terminal = new();

    private long _bytes;
    private long _frames;
    private long _malformed;
    private int _channelCount;
    private double _lastFrameTimestamp = -1;
    private double _estimatedInterval = 0.005;
    private double _lastTimestamp;
    private double _rateWindowStart;
    private int _rateWindowFrames;

    private long _lastStatsBytes;
    private long _lastStatsFrames;
    private double _lastStatsTime;
    private double _lastBytesPerSecond;
    private double _lastFramesPerSecond;

    public AcquisitionEngine()
    {
        _clock.Start();
    }

    public event Action<int>? ChannelLayoutChanged;

    public ProtocolKind Protocol
    {
        get
        {
            lock (_sync)
            {
                return _protocol;
            }
        }
    }

    public WaveformBuffer Buffer => _buffer;
    public TerminalLog Terminal => _terminal;

    public int ChannelCount
    {
        get
        {
            lock (_sync)
            {
                return _channelCount;
            }
        }
    }

    public void SetProtocol(ProtocolKind protocol)
    {
        lock (_sync)
        {
            _protocol = protocol;
            _decoder = ProtocolDecoderFactory.Create(protocol);
            ResetSessionCore();
        }
    }

    public void ResetSession()
    {
        lock (_sync)
        {
            _decoder.Reset();
            ResetSessionCore();
        }
    }

    private void ResetSessionCore()
    {
        _buffer.Reset();
        _terminal.Clear();
        _channelCount = 0;
        Interlocked.Exchange(ref _bytes, 0);
        Interlocked.Exchange(ref _frames, 0);
        Interlocked.Exchange(ref _malformed, 0);
        _lastFrameTimestamp = -1;
        _lastTimestamp = 0;
        _estimatedInterval = 0.005;
        _rateWindowStart = 0;
        _rateWindowFrames = 0;
        _clock.Restart();

        lock (_statsGate)
        {
            _lastStatsBytes = 0;
            _lastStatsFrames = 0;
            _lastStatsTime = 0;
            _lastBytesPerSecond = 0;
            _lastFramesPerSecond = 0;
        }
    }

    public void Process(byte[] data)
    {
        if (data.Length == 0)
        {
            return;
        }

        Interlocked.Add(ref _bytes, data.Length);
        _terminal.Append(data);

        IReadOnlyList<double[]> frames;
        int malformed;
        lock (_sync)
        {
            frames = _decoder.Decode(data);
            malformed = _decoder.ConsumeMalformedCount();
            if (malformed > 0)
            {
                Interlocked.Add(ref _malformed, malformed);
            }

            foreach (double[] frame in frames)
            {
                AppendFrameLocked(frame);
            }
        }
    }

    private void AppendFrameLocked(double[] source)
    {
        if (source.Length == 0)
        {
            return;
        }

        if (_channelCount == 0)
        {
            int detected = Math.Clamp(source.Length, 1, MaxChannels);
            _buffer.ConfigureChannels(detected);
            _channelCount = detected;
            ChannelLayoutChanged?.Invoke(detected);
        }

        double[] values;
        if (source.Length == _channelCount)
        {
            values = source;
        }
        else
        {
            values = new double[_channelCount];
            Array.Copy(source, values, Math.Min(source.Length, _channelCount));
        }

        double now = _clock.Elapsed.TotalSeconds;
        if (_lastFrameTimestamp < 0)
        {
            _lastTimestamp = now;
        }
        else
        {
            _lastTimestamp += _estimatedInterval;
            if (now - _lastTimestamp > 0.05)
            {
                // Data pause: re-anchor to wall-clock time so gaps remain visible.
                _lastTimestamp = now;
            }
        }

        _lastFrameTimestamp = _lastTimestamp;
        _buffer.Append(values, _lastTimestamp);
        Interlocked.Increment(ref _frames);
        UpdateFrameRate(now);
    }

    private void UpdateFrameRate(double now)
    {
        if (_rateWindowStart <= 0)
        {
            _rateWindowStart = now;
            _rateWindowFrames = 0;
        }

        _rateWindowFrames++;
        double elapsed = now - _rateWindowStart;
        if (elapsed >= 0.6)
        {
            double observed = elapsed / _rateWindowFrames;
            if (observed > 0.0001 && observed < 5)
            {
                // Exponential smoothing avoids a visibly jittery time axis.
                _estimatedInterval = _estimatedInterval <= 0
                    ? observed
                    : (_estimatedInterval * 0.65) + (observed * 0.35);
            }

            _rateWindowStart = now;
            _rateWindowFrames = 0;
        }
    }

    public AcquisitionStats GetStats()
    {
        long bytes = Interlocked.Read(ref _bytes);
        long frames = Interlocked.Read(ref _frames);
        long malformed = Interlocked.Read(ref _malformed);
        double now = _clock.Elapsed.TotalSeconds;
        int channelCount = ChannelCount;
        int bufferSamples = _buffer.Count;

        // Never take _sync while holding _statsGate: reset takes them in the opposite
        // direction. Counts are snapshots and are allowed to be a few milliseconds stale.
        lock (_statsGate)
        {
            if (_lastStatsTime <= 0)
            {
                _lastStatsTime = now;
                _lastStatsBytes = bytes;
                _lastStatsFrames = frames;
                _lastBytesPerSecond = 0;
                _lastFramesPerSecond = 0;
            }
            else
            {
                double elapsed = now - _lastStatsTime;
                if (elapsed >= 0.2)
                {
                    _lastBytesPerSecond = (bytes - _lastStatsBytes) / elapsed;
                    _lastFramesPerSecond = (frames - _lastStatsFrames) / elapsed;
                    _lastStatsBytes = bytes;
                    _lastStatsFrames = frames;
                    _lastStatsTime = now;
                }
            }

            return new AcquisitionStats(
                bytes,
                frames,
                malformed,
                Math.Max(0, _lastBytesPerSecond),
                Math.Max(0, _lastFramesPerSecond),
                channelCount,
                bufferSamples,
                _buffer.Capacity,
                now);
        }
    }
}
