using System.IO.Ports;
using SerialScope.Models;

namespace SerialScope.Services;

public sealed class SerialConnection : IDisposable
{
    private readonly object _gate = new();
    private SerialPort? _port;
    private CancellationTokenSource? _cts;
    private Task? _readTask;

    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler<string>? Faulted;

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _port?.IsOpen == true;
            }
        }
    }

    public string? OpenPortName
    {
        get
        {
            lock (_gate)
            {
                return _port?.PortName;
            }
        }
    }

    public void Open(SerialSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.PortName))
        {
            throw new ArgumentException("请选择串口。", nameof(settings));
        }

        lock (_gate)
        {
            if (_port is not null)
            {
                throw new InvalidOperationException("串口已经打开。");
            }

            var port = new SerialPort(settings.PortName, settings.BaudRate, settings.Parity, settings.DataBits, settings.StopBits)
            {
                Handshake = settings.Handshake,
                DtrEnable = settings.DtrEnable,
                RtsEnable = settings.RtsEnable,
                ReadBufferSize = 64 * 1024,
                WriteBufferSize = 16 * 1024,
                ReadTimeout = 250,
                WriteTimeout = 1500,
            };

            try
            {
                port.Open();
            }
            catch
            {
                port.Dispose();
                throw;
            }

            var cts = new CancellationTokenSource();
            _port = port;
            _cts = cts;
            _readTask = Task.Run(() => ReadLoopAsync(port, cts.Token));
        }
    }

    public async Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        SerialPort port;
        lock (_gate)
        {
            port = _port ?? throw new InvalidOperationException("串口尚未连接。");
        }

        if (!port.IsOpen)
        {
            throw new InvalidOperationException("串口已经关闭。");
        }

        await port.BaseStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Close()
    {
        SerialPort? port;
        CancellationTokenSource? cts;
        Task? readTask;

        lock (_gate)
        {
            port = _port;
            cts = _cts;
            readTask = _readTask;
            _port = null;
            _cts = null;
            _readTask = null;
        }

        if (port is null)
        {
            return;
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
        }

        try
        {
            port.Close();
        }
        catch
        {
        }

        try
        {
            port.Dispose();
        }
        catch
        {
        }

        // ReadAsync can remain pending until the port closes. Do not block the UI thread.
        _ = readTask?.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
        cts?.Dispose();
    }

    private async Task ReadLoopAsync(SerialPort port, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await port.BaseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    Faulted?.Invoke(this, $"读取串口失败：{ex.Message}");
                    break;
                }

                if (read <= 0)
                {
                    await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var copy = new byte[read];
                Buffer.BlockCopy(buffer, 0, copy, 0, read);
                try
                {
                    DataReceived?.Invoke(this, copy);
                }
                catch (Exception ex)
                {
                    Faulted?.Invoke(this, $"处理串口数据失败：{ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                Faulted?.Invoke(this, $"串口读取线程异常：{ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        Close();
    }
}
