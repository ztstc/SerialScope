using System.Collections.ObjectModel;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SerialScope.Models;
using SerialScope.Services;
using Windows.Foundation;
using Windows.UI;

namespace SerialScope.Controls;

public sealed partial class WaveformView : UserControl
{
    private Color _backgroundColor = Color.FromArgb(255, 31, 31, 31);
    private Color _plotColor = Color.FromArgb(255, 27, 27, 27);
    private Color _gridColor = Color.FromArgb(255, 51, 51, 51);
    private Color _gridStrongColor = Color.FromArgb(255, 69, 69, 69);
    private Color _axisTextColor = Color.FromArgb(255, 154, 154, 154);
    private Color _mutedTextColor = Color.FromArgb(255, 192, 192, 192);
    private Color _borderColor = Color.FromArgb(255, 58, 58, 58);
    private Color _tooltipBackground = Color.FromArgb(248, 43, 43, 43);
    private Color _tooltipBorder = Color.FromArgb(255, 74, 74, 74);
    private Color _tooltipTextColor = Color.FromArgb(255, 245, 245, 245);
    private Color _crosshairColor = Color.FromArgb(180, 255, 255, 255);
    private int _lineGlowAlpha = 24;

    private readonly DispatcherQueueTimer _timer;
    private WaveformSnapshot _snapshot = WaveformSnapshot.Empty;
    private WaveformBuffer? _source;
    private ObservableCollection<ChannelState>? _channels;
    private CanvasTextFormat? _axisFormat;
    private CanvasTextFormat? _smallFormat;
    private CanvasTextFormat? _tooltipFormat;
    private CanvasTextFormat? _tooltipValueFormat;
    private CanvasStrokeStyle? _dashStyle;
    private Point? _cursorPosition;
    private Rect _lastPlotRect;
    private bool _isActive = true;
    private bool _isPaused;
    private bool _dirty = true;
    private bool _loggedFirstDataDraw;
    private double _windowSeconds = 10;
    private double _pausedEndTime;
    private double _yMin = -10;
    private double _yMax = 10;

    public WaveformView()
    {
        InitializeComponent();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.Tick += OnTimerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        ActualThemeChanged += (_, _) =>
        {
            UpdateThemeColors();
            _dirty = true;
            ScopeCanvas.Invalidate();
        };
        UpdateThemeColors();
    }

    private void UpdateThemeColors()
    {
        bool light = ActualTheme != ElementTheme.Dark;
        if (light)
        {
            _backgroundColor = Color.FromArgb(255, 255, 255, 255);
            _plotColor = Color.FromArgb(255, 255, 255, 255);
            _gridColor = Color.FromArgb(255, 237, 237, 237);
            _gridStrongColor = Color.FromArgb(255, 214, 214, 214);
            _axisTextColor = Color.FromArgb(255, 107, 107, 107);
            _mutedTextColor = Color.FromArgb(255, 68, 68, 68);
            _borderColor = Color.FromArgb(255, 221, 221, 221);
            _tooltipBackground = Color.FromArgb(248, 255, 255, 255);
            _tooltipBorder = Color.FromArgb(255, 192, 192, 192);
            _tooltipTextColor = Color.FromArgb(255, 26, 26, 26);
            _crosshairColor = Color.FromArgb(180, 0, 0, 0);
            _lineGlowAlpha = 18;
        }
        else
        {
            _backgroundColor = Color.FromArgb(255, 31, 31, 31);
            _plotColor = Color.FromArgb(255, 27, 27, 27);
            _gridColor = Color.FromArgb(255, 51, 51, 51);
            _gridStrongColor = Color.FromArgb(255, 69, 69, 69);
            _axisTextColor = Color.FromArgb(255, 154, 154, 154);
            _mutedTextColor = Color.FromArgb(255, 192, 192, 192);
            _borderColor = Color.FromArgb(255, 58, 58, 58);
            _tooltipBackground = Color.FromArgb(248, 43, 43, 43);
            _tooltipBorder = Color.FromArgb(255, 74, 74, 74);
            _tooltipTextColor = Color.FromArgb(255, 245, 245, 245);
            _crosshairColor = Color.FromArgb(180, 255, 255, 255);
            _lineGlowAlpha = 24;
        }
    }

    public WaveformBuffer? Source
    {
        get => _source;
        set
        {
            _source = value;
            _snapshot = WaveformSnapshot.Empty;
            _dirty = true;
        }
    }

    public ObservableCollection<ChannelState>? Channels
    {
        get => _channels;
        set
        {
            _channels = value;
            _dirty = true;
        }
    }

    public double WindowSeconds
    {
        get => _windowSeconds;
        set
        {
            _windowSeconds = Math.Clamp(value, 0.5, 120);
            if (_isPaused)
            {
                CapturePausedSnapshot();
            }

            _dirty = true;
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (_isPaused == value)
            {
                return;
            }

            _isPaused = value;
            if (_isPaused)
            {
                _pausedEndTime = _source?.LatestTime ?? 0;
                CapturePausedSnapshot();
            }
            else
            {
                _snapshot = WaveformSnapshot.Empty;
                _dirty = true;
            }
        }
    }

    public bool AutoScale { get; set; } = true;

    public double ManualYMin { get; set; } = -50;

    public double ManualYMax { get; set; } = 100;

    public void SetActive(bool active)
    {
        _isActive = active;
        if (active)
        {
            _dirty = true;
            _timer.Start();
            ScopeCanvas.Invalidate();
        }
        else
        {
            _timer.Stop();
        }
    }

    public void Refresh()
    {
        _dirty = true;
        ScopeCanvas.Invalidate();
    }

    /// <summary>Clears the frozen snapshot so an empty buffer also clears a paused chart.</summary>
    public void ClearDisplay()
    {
        _snapshot = WaveformSnapshot.Empty;
        if (_isPaused)
        {
            _pausedEndTime = _source?.LatestTime ?? 0;
        }

        _dirty = true;
        ScopeCanvas.Invalidate();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateThemeColors();
        SerialScope.Program.Log($"WaveformView loaded size={ActualWidth:0}x{ActualHeight:0}");
        if (_isActive)
        {
            _timer.Start();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _dirty = true;
        ScopeCanvas.Invalidate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
    }

    private void OnTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_isActive)
        {
            return;
        }

        if (!_isPaused)
        {
            int maxPoints = Math.Max(128, (int)(ScopeCanvas.ActualWidth * 2));
            _snapshot = _source?.GetSnapshot(_windowSeconds, null, maxPoints) ?? WaveformSnapshot.Empty;
            ScopeCanvas.Invalidate();
            _dirty = false;
        }
        else if (_dirty)
        {
            ScopeCanvas.Invalidate();
            _dirty = false;
        }
    }

    private void CapturePausedSnapshot()
    {
        int maxPoints = Math.Max(128, (int)(ScopeCanvas.ActualWidth * 2));
        _snapshot = _source?.GetSnapshot(_windowSeconds, _pausedEndTime, maxPoints) ?? WaveformSnapshot.Empty;
        _dirty = true;
        ScopeCanvas.Invalidate();
    }

    private void ScopeCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _cursorPosition = e.GetCurrentPoint(ScopeCanvas).Position;
        ScopeCanvas.Invalidate();
    }

    private void ScopeCanvas_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _cursorPosition = null;
        ScopeCanvas.Invalidate();
    }

    private void EnsureFormats()
    {
        if (_axisFormat is not null)
        {
            return;
        }

        _axisFormat = new CanvasTextFormat
        {
            FontFamily = "Consolas",
            FontSize = 10,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        _smallFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = 10,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        _tooltipFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = 11,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        _tooltipValueFormat = new CanvasTextFormat
        {
            FontFamily = "Consolas",
            FontSize = 11,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        _dashStyle = new CanvasStrokeStyle
        {
            DashStyle = CanvasDashStyle.Dash,
            StartCap = CanvasCapStyle.Round,
            EndCap = CanvasCapStyle.Round,
        };
    }

    private void ScopeCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        EnsureFormats();
        CanvasDrawingSession ds = args.DrawingSession;
        ds.Clear(_backgroundColor);

        float width = (float)ScopeCanvas.ActualWidth;
        float height = (float)ScopeCanvas.ActualHeight;
        if (width < 120 || height < 100)
        {
            return;
        }

        var plot = new Rect(64, 18, Math.Max(20, width - 84), Math.Max(20, height - 52));
        _lastPlotRect = plot;
        ds.FillRectangle(plot, _plotColor);

        if (!_loggedFirstDataDraw && _snapshot.HasData)
        {
            _loggedFirstDataDraw = true;
            SerialScope.Program.Log($"Waveform first data draw: {_snapshot.SampleCount} samples, {width:0}x{height:0}");
        }

        ComputeYRange(out double yMin, out double yMax, out double yStep);
        _yMin = yMin;
        _yMax = yMax;

        const int xDivisions = 5;
        int yDivisions = Math.Clamp((int)Math.Round((yMax - yMin) / yStep), 2, 10);

        // Grid.
        for (int i = 0; i <= xDivisions; i++)
        {
            float x = (float)(plot.Left + (plot.Width * i / xDivisions));
            Color color = i == 0 || i == xDivisions ? _gridStrongColor : _gridColor;
            ds.DrawLine(x, (float)plot.Top, x, (float)plot.Bottom, color, 1);
        }

        for (int i = 0; i <= yDivisions; i++)
        {
            float y = (float)(plot.Top + (plot.Height * i / yDivisions));
            Color color = i == 0 || i == yDivisions ? _gridStrongColor : _gridColor;
            ds.DrawLine((float)plot.Left, y, (float)plot.Right, y, color, 1);
        }

        // Axis labels.
        double endTime = _snapshot.HasData ? _snapshot.EndTime : _windowSeconds;
        double startTime = endTime - _windowSeconds;

        for (int i = 0; i <= xDivisions; i++)
        {
            double fraction = (double)i / xDivisions;
            float x = (float)(plot.Left + (plot.Width * fraction));
            string label = i == xDivisions ? "现在" : $"-{_windowSeconds * (1 - fraction):0.#}s";
            DrawCenteredText(ds, label, x, (float)plot.Bottom + 9, _axisTextColor, _axisFormat!);
        }

        for (int i = 0; i <= yDivisions; i++)
        {
            double value = yMax - (yStep * i);
            float y = (float)(plot.Top + (plot.Height * i / yDivisions));
            using var layout = new CanvasTextLayout(ScopeCanvas, FormatAxis(value, yStep), _axisFormat!, 52, 16);
            ds.DrawTextLayout(layout, (float)plot.Left - 58, y - 8, _axisTextColor);
        }

        // Highlight zero when it is visible.
        if (yMin < 0 && yMax > 0)
        {
            float zeroY = MapY(0, plot, yMin, yMax);
            ds.DrawLine((float)plot.Left, zeroY, (float)plot.Right, zeroY, _borderColor, 1, _dashStyle);
        }

        if (_channels is not null)
        {
            using (ds.CreateLayer(1f, plot))
            {
                int channelCount = Math.Min(_snapshot.ChannelCount, _channels.Count);
                for (int c = 0; c < channelCount; c++)
                {
                    ChannelState channel = _channels[c];
                    if (!channel.IsVisible)
                    {
                        continue;
                    }

                    DrawSeries(ds, _snapshot.Series[c], _snapshot.Times, plot, yMin, yMax, channel.Color);
                }
            }
        }

        ds.DrawRectangle(plot, _borderColor, 1);

        if (_snapshot.HasData && _cursorPosition is Point cursor)
        {
            DrawCursor(ds, cursor, plot, yMin, yMax);
        }
    }

    private void DrawSeries(CanvasDrawingSession ds, double[] values, double[] times, Rect plot, double yMin, double yMax, Color color)
    {
        if (values.Length == 0)
        {
            return;
        }

        if (values.Length == 1)
        {
            if (double.IsFinite(values[0]))
            {
                float x = MapX(times[0], plot, _snapshot.StartTime, _snapshot.EndTime);
                float y = MapY(values[0], plot, yMin, yMax);
                ds.FillCircle(x, y, 2.4f, color);
            }

            return;
        }

        using var path = new CanvasPathBuilder(ScopeCanvas);
        bool figureOpen = false;
        int finiteCount = 0;

        for (int i = 0; i < values.Length; i++)
        {
            double value = values[i];
            if (!double.IsFinite(value))
            {
                figureOpen = false;
                continue;
            }

            float x = MapX(times[i], plot, _snapshot.StartTime, _snapshot.EndTime);
            float y = MapY(value, plot, yMin, yMax);
            // Clamp absurd y values so Direct2D does not receive gigantic coordinates.
            y = Math.Clamp(y, (float)plot.Top - 1000, (float)plot.Bottom + 1000);

            if (!figureOpen)
            {
                path.BeginFigure(x, y);
                figureOpen = true;
            }
            else
            {
                path.AddLine(x, y);
            }

            finiteCount++;
        }

        if (figureOpen)
        {
            path.EndFigure(CanvasFigureLoop.Open);
        }

        if (finiteCount == 0)
        {
            return;
        }

        using CanvasGeometry geometry = CanvasGeometry.CreatePath(path);
        ds.DrawGeometry(geometry, Color.FromArgb((byte)_lineGlowAlpha, color.R, color.G, color.B), 5.5f);
        ds.DrawGeometry(geometry, color, 1.6f);
    }

    private void DrawCursor(CanvasDrawingSession ds, Point cursor, Rect plot, double yMin, double yMax)
    {
        if (cursor.X < plot.Left || cursor.X > plot.Right || cursor.Y < plot.Top || cursor.Y > plot.Bottom)
        {
            return;
        }

        double fraction = (cursor.X - plot.Left) / plot.Width;
        double targetTime = _snapshot.StartTime + ((_snapshot.EndTime - _snapshot.StartTime) * fraction);
        int index = NearestIndex(_snapshot.Times, targetTime);
        if (index < 0)
        {
            return;
        }

        double sampleTime = _snapshot.Times[index];
        float x = MapX(sampleTime, plot, _snapshot.StartTime, _snapshot.EndTime);
        ds.DrawLine(x, (float)plot.Top, x, (float)plot.Bottom, _crosshairColor, 1, _dashStyle);

        int channelCount = Math.Min(_snapshot.ChannelCount, _channels?.Count ?? 0);
        int visibleRows = 0;
        for (int c = 0; c < channelCount; c++)
        {
            if (_channels![c].IsVisible)
            {
                visibleRows++;
            }
        }

        if (visibleRows == 0)
        {
            return;
        }

        float tooltipWidth = 196;
        float headerHeight = 28;
        float rowHeight = 20;
        float padding = 10;
        float tooltipHeight = headerHeight + (rowHeight * visibleRows) + padding;
        float left = x + 14;
        if (left + tooltipWidth > plot.Right)
        {
            left = x - tooltipWidth - 14;
        }

        left = Math.Clamp(left, (float)plot.Left + 4, (float)plot.Right - tooltipWidth - 4);
        float top = (float)plot.Top + 14;
        var tooltipRect = new Rect(left, top, tooltipWidth, tooltipHeight);

        ds.FillRoundedRectangle(tooltipRect, 8, 8, _tooltipBackground);
        ds.DrawRoundedRectangle(tooltipRect, 8, 8, _tooltipBorder, 1);

        double relative = sampleTime - _snapshot.EndTime;
        ds.DrawText($"采样点  T {relative:+0.000;-0.000;0.000}s", new Vector2(left + padding, top + 8), _mutedTextColor, _smallFormat);

        float rowY = top + headerHeight;
        for (int c = 0; c < channelCount; c++)
        {
            ChannelState channel = _channels![c];
            if (!channel.IsVisible)
            {
                continue;
            }

            ds.FillCircle(left + padding + 4, rowY + 10, 3.5f, channel.Color);
            ds.DrawText(channel.Name, new Vector2(left + padding + 14, rowY + 2), _tooltipTextColor, _tooltipFormat);

            double value = _snapshot.Series[c][index];
            string text = double.IsFinite(value) ? ChannelState.FormatValue(value) : "--";
            using var layout = new CanvasTextLayout(ScopeCanvas, text, _tooltipValueFormat!, 82, 18);
            float textWidth = (float)layout.LayoutBounds.Width;
            ds.DrawTextLayout(layout, left + tooltipWidth - padding - textWidth, rowY + 2, _tooltipTextColor);

            // Dot on the curve.
            if (double.IsFinite(value))
            {
                float pointY = MapY(value, plot, yMin, yMax);
                if (pointY >= plot.Top - 4 && pointY <= plot.Bottom + 4)
                {
                    ds.FillCircle(x, pointY, 3.2f, _backgroundColor);
                    ds.DrawCircle(x, pointY, 3.2f, channel.Color, 1.4f);
                }
            }

            rowY += rowHeight;
        }
    }

    private void ComputeYRange(out double min, out double max, out double step)
    {
        const int divisions = 5;
        if (AutoScale)
        {
            double dataMin = double.PositiveInfinity;
            double dataMax = double.NegativeInfinity;
            int channelCount = Math.Min(_snapshot.ChannelCount, _channels?.Count ?? int.MaxValue);
            for (int c = 0; c < channelCount; c++)
            {
                if (_channels is not null && !_channels[c].IsVisible)
                {
                    continue;
                }

                double[] values = _snapshot.Series[c];
                for (int i = 0; i < values.Length; i++)
                {
                    double value = values[i];
                    if (double.IsFinite(value))
                    {
                        if (value < dataMin) dataMin = value;
                        if (value > dataMax) dataMax = value;
                    }
                }
            }

            if (!double.IsFinite(dataMin) || !double.IsFinite(dataMax))
            {
                min = -10;
                max = 10;
                step = 4;
                return;
            }

            if (dataMax - dataMin < 1e-9)
            {
                double pad = Math.Max(1, Math.Abs(dataMax) * 0.05);
                dataMin -= pad;
                dataMax += pad;
            }
            else
            {
                double padding = (dataMax - dataMin) * 0.08;
                dataMin -= padding;
                dataMax += padding;
            }

            step = NiceStep((dataMax - dataMin) / divisions);
            min = Math.Floor(dataMin / step) * step;
            max = Math.Ceiling(dataMax / step) * step;
            if (max - min < step)
            {
                max = min + step;
            }

            return;
        }

        min = Math.Min(ManualYMin, ManualYMax);
        max = Math.Max(ManualYMin, ManualYMax);
        if (max - min < 1e-9)
        {
            max = min + 1;
        }

        step = (max - min) / divisions;
    }

    private static double NiceStep(double roughStep)
    {
        if (roughStep <= 0 || !double.IsFinite(roughStep))
        {
            return 1;
        }

        double exponent = Math.Floor(Math.Log10(roughStep));
        double fraction = roughStep / Math.Pow(10, exponent);
        double niceFraction = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10;
        return niceFraction * Math.Pow(10, exponent);
    }

    private static string FormatAxis(double value, double step)
    {
        if (Math.Abs(value) < step / 10000)
        {
            return "0";
        }

        if (Math.Abs(value) >= 100000 || (Math.Abs(value) > 0 && Math.Abs(value) < 0.001))
        {
            return value.ToString("0.##E+0");
        }

        int decimals = step >= 1 ? 0 : step >= 0.1 ? 1 : step >= 0.01 ? 2 : 3;
        return value.ToString($"F{decimals}");
    }

    private static float MapX(double time, Rect plot, double start, double end)
    {
        double span = Math.Max(1e-9, end - start);
        return (float)(plot.Left + ((time - start) / span * plot.Width));
    }

    private static float MapY(double value, Rect plot, double min, double max)
    {
        double span = Math.Max(1e-9, max - min);
        return (float)(plot.Bottom - ((value - min) / span * plot.Height));
    }

    private static int NearestIndex(double[] times, double target)
    {
        if (times.Length == 0)
        {
            return -1;
        }

        int low = 0;
        int high = times.Length - 1;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (times[mid] < target)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        if (low > 0 && Math.Abs(times[low - 1] - target) < Math.Abs(times[low] - target))
        {
            return low - 1;
        }

        return low;
    }

    private void DrawCenteredText(CanvasDrawingSession ds, string text, float centerX, float y, Color color, CanvasTextFormat format)
    {
        using var layout = new CanvasTextLayout(ScopeCanvas, text, format, 120, 16);
        float width = (float)layout.LayoutBounds.Width;
        ds.DrawTextLayout(layout, centerX - (width / 2), y, color);
    }
}
