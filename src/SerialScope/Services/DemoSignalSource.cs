using System.Diagnostics;
using System.Globalization;
using System.Text;
using SerialScope.Models;

namespace SerialScope.Services;

/// <summary>Generates deterministic-looking multi-channel data without any serial hardware.</summary>
public sealed class DemoSignalSource : IDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _task;

    public event Action<byte[]>? DataGenerated;

    public bool IsRunning => _cts is not null;

    public void Start(ProtocolKind protocol)
    {
        Stop();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _task = Task.Run(() => RunAsync(protocol, cts.Token));
    }

    public void Stop()
    {
        CancellationTokenSource? cts = _cts;
        _cts = null;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch
        {
        }

        Task? task = _task;
        _task = null;
        if (task is not null)
        {
            _ = task.ContinueWith(static t =>
            {
                _ = t.Exception;
            }, TaskScheduler.Default);
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(250).ConfigureAwait(false);
            cts.Dispose();
        });
    }

    private async Task RunAsync(ProtocolKind protocol, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        long sampleIndex = 0;
        double lastRawEmit = -1;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                double now = clock.Elapsed.TotalSeconds;

                if (protocol == ProtocolKind.RawData)
                {
                    if (now - lastRawEmit < 0.05)
                    {
                        continue;
                    }

                    lastRawEmit = now;
                    double[] rawValues = Generate(now);
                    string line = string.Create(CultureInfo.InvariantCulture,
                        $"[DEMO] t={now,7:F3}s  " +
                        $"CH1={rawValues[0],8:F2}  CH2={rawValues[1],8:F2}  CH3={rawValues[2],8:F2}  " +
                        $"CH4={rawValues[3],8:F2}  CH5={rawValues[4],8:F2}  CH6={rawValues[5],8:F2}\r\n");
                    Emit(Encoding.UTF8.GetBytes(line));
                    continue;
                }

                const double samplePeriod = 0.005;
                long target = (long)(now / samplePeriod);
                int count = (int)Math.Clamp(target - sampleIndex, 1, 16);
                using var stream = new MemoryStream(64 * 4 * count);
                for (int i = 0; i < count; i++)
                {
                    double sampleTime = (sampleIndex + i) * samplePeriod;
                    double[] values = Generate(sampleTime);
                    WriteFrame(protocol, stream, values);
                }

                sampleIndex += count;
                Emit(stream.ToArray());
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Demo signal stopped: {ex}");
        }
    }

    private void Emit(byte[] data)
    {
        try
        {
            DataGenerated?.Invoke(data);
        }
        catch
        {
        }
    }

    private static double[] Generate(double t)
    {
        double noise = (Random.Shared.NextDouble() * 2) - 1;
        return new[]
        {
            55 + (28 * Math.Sin(2 * Math.PI * 0.45 * t)) + (4 * Math.Sin((2 * Math.PI * 5.1 * t) + 0.8)) + (noise * 0.6),
            24 * Math.Cos((2 * Math.PI * 0.23 * t) + 0.8) + (7 * Math.Sin(2 * Math.PI * 2.2 * t)) + noise,
            -18 + (16 * Math.Sin((2 * Math.PI * 0.7 * t) + 1.4)),
            14 * Math.Sin(2 * Math.PI * (0.35 * t + (0.5 * Math.Sin(2 * Math.PI * 0.06 * t)))),
            Triangle(0.75 * t + 0.18) * 26 - 8,
            6 * Math.Sin(2 * Math.PI * 0.11 * t) + (noise * 2.2),
        };
    }

    private static double Triangle(double phase)
    {
        phase -= Math.Floor(phase);
        return 1 - (4 * Math.Abs(phase - 0.5));
    }

    private static void WriteFrame(ProtocolKind protocol, MemoryStream stream, double[] values)
    {
        if (protocol == ProtocolKind.FireWater)
        {
            string line = string.Create(CultureInfo.InvariantCulture,
                $"{values[0]:F4},{values[1]:F4},{values[2]:F4},{values[3]:F4},{values[4]:F4},{values[5]:F4}\n");
            byte[] bytes = Encoding.ASCII.GetBytes(line);
            stream.Write(bytes, 0, bytes.Length);
            return;
        }

        Span<byte> buffer = stackalloc byte[sizeof(float) * values.Length + 4];
        for (int i = 0; i < values.Length; i++)
        {
            BitConverter.TryWriteBytes(buffer.Slice(i * 4, 4), (float)values[i]);
        }

        buffer[^4] = 0x00;
        buffer[^3] = 0x00;
        buffer[^2] = 0x80;
        buffer[^1] = 0x7F;
        stream.Write(buffer);
    }

    public void Dispose() => Stop();
}
