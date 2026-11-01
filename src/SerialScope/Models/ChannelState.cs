using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace SerialScope.Models;

public sealed class ChannelState : INotifyPropertyChanged
{
    private bool _isVisible = true;
    private string _valueText = "--";
    private string _rangeText = "最小值 --  最大值 --";
    private double? _lastValue;
    private double _minimum;
    private double _maximum;
    private bool _hasData;
    private Color _color;

    public ChannelState(int index, Color color)
    {
        Index = index;
        Name = $"CH{index + 1}";
        _color = color;
        Brush = new SolidColorBrush(color);
        DimBrush = new SolidColorBrush(Color.FromArgb(60, color.R, color.G, color.B));
    }

    public int Index { get; }
    public string Name { get; }
    public string DisplayIndex => $"{Index + 1:00}";
    public Color Color => _color;
    public SolidColorBrush Brush { get; }
    public SolidColorBrush DimBrush { get; }
    public double? LastValue => _lastValue;
    public double Minimum => _minimum;
    public double Maximum => _maximum;
    public bool HasData => _hasData;

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
            {
                return;
            }

            _isVisible = value;
            OnPropertyChanged();
            VisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string ValueText
    {
        get => _valueText;
        private set => SetField(ref _valueText, value);
    }

    public string RangeText
    {
        get => _rangeText;
        private set => SetField(ref _rangeText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? VisibilityChanged;

    public void SetColor(Color color)
    {
        if (_color == color)
        {
            return;
        }

        _color = color;
        Brush.Color = color;
        DimBrush.Color = Color.FromArgb(60, color.R, color.G, color.B);
        OnPropertyChanged(nameof(Color));
        OnPropertyChanged(nameof(Brush));
        OnPropertyChanged(nameof(DimBrush));
    }

    public void Update(double? value, double minimum, double maximum, bool hasData)
    {
        _lastValue = value;
        _minimum = minimum;
        _maximum = maximum;
        _hasData = hasData;
        ValueText = hasData && value.HasValue ? FormatValue(value.Value) : "--";
        RangeText = hasData
            ? $"{FormatValue(minimum)}  ~  {FormatValue(maximum)}"
            : "最小值 --  最大值 --";
    }

    public void Reset()
    {
        _lastValue = null;
        _minimum = 0;
        _maximum = 0;
        _hasData = false;
        ValueText = "--";
        RangeText = "最小值 --  最大值 --";
    }

    internal static string FormatValue(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "--";
        }

        double abs = Math.Abs(value);
        if (abs >= 100000 || (abs > 0 && abs < 0.0001))
        {
            return value.ToString("0.###E+0");
        }

        return value.ToString(abs >= 1000 ? "0.#" : "0.###");
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
