using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialScope.Models;
using SerialScope.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;

namespace SerialScope;

public sealed partial class MainWindow : Window
{
    private readonly AcquisitionEngine _engine = new();
    private readonly SerialConnection _serial = new();
    private readonly DemoSignalSource _demo = new();
    private readonly ObservableCollection<ChannelState> _channels = new();
    private readonly DispatcherQueueTimer _uiTimer;
    private readonly DispatcherQueueTimer _repeatTimer;
    private readonly Style? _accentButtonStyle;

    private ProtocolKind _protocol = ProtocolKind.FireWater;
    private bool _initialized;
    private bool _isSerialConnected;
    private bool _isDemo;
    private bool _isPaused;
    private bool _isTerminalActive;
    private bool _isClosing;
    private bool _isSending;
    private bool _suppressDemoToggle;
    private bool _firstActivationHandled;
    private int _lastTerminalVersion = -1;

    public MainWindow()
    {
        InitializeComponent();

        _accentButtonStyle = ConnectButton.Style;
        _uiTimer = DispatcherQueue.CreateTimer();
        _uiTimer.Interval = TimeSpan.FromMilliseconds(250);
        _uiTimer.Tick += OnUiTimerTick;

        _repeatTimer = DispatcherQueue.CreateTimer();
        _repeatTimer.Interval = TimeSpan.FromMilliseconds(500);
        _repeatTimer.Tick += OnRepeatTimerTick;

        _engine.ChannelLayoutChanged += OnChannelLayoutChanged;
        _serial.DataReceived += OnSerialDataReceived;
        _serial.Faulted += OnSerialFaulted;
        _demo.DataGenerated += OnDemoDataGenerated;

        ScopeView.Source = _engine.Buffer;
        ScopeView.Channels = _channels;
        ChannelList.ItemsSource = _channels;

        ConfigureWindow();
        InitializeControls();
        _engine.SetProtocol(_protocol);

        _initialized = true;
        ApplyPageSelection(terminal: false);
        SetSendEnabled(false);
        UpdatePauseVisuals();
        SetSerialInputsEnabled(true);
        ClearSessionUi();
        _uiTimer.Start();
        UpdateUiStats();

        RootLayout.ActualThemeChanged += (_, _) => ApplyTheme();
        Closed += OnWindowClosed;
        Activated += OnWindowActivated;

        try
        {
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        }
        catch
        {
        }

        if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase)))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isDemo)
                {
                    StartDemo();
                }
            });
        }
    }

    private void ConfigureWindow()
    {
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            double dpiScale = GetDpiForWindow(hwnd) / 96.0;
            var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;

            int widthDip = 1440;
            int heightDip = 900;
            bool explicitSize = false;

            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (arg.StartsWith("--window=", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = arg["--window=".Length..].Split('x', 'X');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h)
                        && w >= 900 && h >= 600)
                    {
                        widthDip = w;
                        heightDip = h;
                        explicitSize = true;
                    }
                }
            }

            if (!explicitSize)
            {
                int maxWidth = Math.Max(720, (int)(workArea.Width / dpiScale));
                int maxHeight = Math.Max(480, (int)(workArea.Height / dpiScale));
                widthDip = Math.Min(widthDip, maxWidth);
                heightDip = Math.Min(heightDip, maxHeight);
            }

            int width = (int)(widthDip * dpiScale);
            int height = (int)(heightDip * dpiScale);
            int x = width <= workArea.Width ? workArea.X + ((workArea.Width - width) / 2) : workArea.X;
            int y = height <= workArea.Height ? workArea.Y + ((workArea.Height - height) / 2) : workArea.Y;
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        catch (Exception ex)
        {
            Program.Log("Window sizing failed: " + ex);
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_firstActivationHandled)
        {
            return;
        }

        _firstActivationHandled = true;
        ConfigureWindow();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(500);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ConfigureWindow();
        };
        timer.Start();
    }

    private void InitializeControls()
    {
        var baudRates = new[]
        {
            "9600", "19200", "38400", "57600", "115200", "230400", "460800", "500000",
            "921600", "1000000", "1500000", "2000000", "3000000",
        };
        BaudCombo.ItemsSource = baudRates;
        BaudCombo.SelectedIndex = 4;
        BaudCombo.Text = "115200";

        DataBitsCombo.ItemsSource = new[] { "5", "6", "7", "8" };
        DataBitsCombo.SelectedIndex = 3;

        ParityCombo.ItemsSource = new[] { "None", "Odd", "Even", "Mark", "Space" };
        ParityCombo.SelectedIndex = 0;

        StopBitsCombo.ItemsSource = new[] { "1", "1.5", "2" };
        StopBitsCombo.SelectedIndex = 0;

        HandshakeCombo.ItemsSource = new[] { "None", "RTS / CTS", "XON / XOFF", "RTS + XON" };
        HandshakeCombo.SelectedIndex = 0;

        YMinBox.Value = -50;
        YMaxBox.Value = 100;
        ScopeView.AutoScale = true;
        ScopeView.WindowSeconds = 10;
        ScopeView.ManualYMin = -50;
        ScopeView.ManualYMax = 100;

        LoadPorts(preserveSelection: false);
    }

    private void LoadPorts(bool preserveSelection)
    {
        string? previous = preserveSelection ? PortCombo.SelectedItem as string : null;
        string[] ports;
        try
        {
            ports = SerialPort.GetPortNames()
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            ports = Array.Empty<string>();
            ShowError("无法枚举串口", ex.Message);
        }

        PortCombo.ItemsSource = ports;
        if (previous is not null && ports.Contains(previous, StringComparer.OrdinalIgnoreCase))
        {
            PortCombo.SelectedItem = previous;
        }
        else if (ports.Length > 0)
        {
            PortCombo.SelectedIndex = 0;
        }
        else
        {
            PortCombo.SelectedIndex = -1;
        }

        PortCombo.PlaceholderText = ports.Length > 0 ? "选择端口" : "未检测到端口";
        RefreshPortsButton.IsEnabled = !_isSerialConnected;
    }

    private void OnUiTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_initialized || _isClosing)
        {
            return;
        }

        UpdateUiStats();
        UpdateChannelValues();
        UpdateEmptyState();
        if (_isTerminalActive)
        {
            UpdateTerminal(force: false);
        }
    }

    private void UpdateUiStats()
    {
        AcquisitionStats stats = _engine.GetStats();
        TrafficStatsText.Text = $"RX {FormatByteRate(stats.BytesPerSecond)}   ·   {stats.FramesPerSecond:0.#} Hz   ·   {FormatCount(stats.TotalFrames)} 帧";
        TerminalBytesText.Text = FormatBytes(stats.TotalBytes);
    }

    private void UpdateChannelValues()
    {
        ChannelSummary[] summaries = _engine.Buffer.GetSummaries();
        if (summaries.Length != _channels.Count)
        {
            if (summaries.Length > 0)
            {
                RebuildChannels(summaries.Length);
            }

            return;
        }

        for (int i = 0; i < _channels.Count; i++)
        {
            ChannelSummary summary = summaries[i];
            _channels[i].Update(
                summary.HasData ? summary.Last : null,
                summary.Minimum,
                summary.Maximum,
                summary.HasData);
        }
    }

    private void UpdateEmptyState()
    {
        EmptyStateOverlay.Visibility = _engine.Buffer.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnChannelLayoutChanged(int channelCount)
    {
        if (_isClosing)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() => RebuildChannels(channelCount));
    }

    private void RebuildChannels(int channelCount)
    {
        if (_isClosing)
        {
            return;
        }

        channelCount = Math.Clamp(channelCount, 0, AcquisitionEngine.MaxChannels);
        if (_channels.Count == channelCount)
        {
            return;
        }

        _channels.Clear();
        Color[] palette = ThemePalette.ChannelColors(RootLayout.ActualTheme);
        for (int i = 0; i < channelCount; i++)
        {
            var channel = new ChannelState(i, palette[i % palette.Length]);
            channel.VisibilityChanged += (_, _) => ScopeView.Refresh();
            _channels.Add(channel);
        }

        ChannelCountText.Text = channelCount.ToString(CultureInfo.InvariantCulture);
        ChannelsEmptyState.Visibility = channelCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChannelList.Visibility = channelCount == 0 ? Visibility.Collapsed : Visibility.Visible;
        ScopeView.Refresh();
        UpdateEmptyState();
    }

    private void ApplyTheme()
    {
        Color[] palette = ThemePalette.ChannelColors(RootLayout.ActualTheme);
        for (int i = 0; i < _channels.Count; i++)
        {
            _channels[i].SetColor(palette[i % palette.Length]);
        }

        ScopeView.Refresh();
    }

    private void UpdateTerminal(bool force)
    {
        if (!_isTerminalActive && !force)
        {
            return;
        }

        int version = _engine.Terminal.Version;
        if (!force && version == _lastTerminalVersion)
        {
            return;
        }

        _lastTerminalVersion = version;
        bool hex = HexToggleButton.IsChecked == true;
        bool autoScroll = AutoScrollCheckBox.IsChecked == true;
        double horizontalOffset = TerminalScrollViewer.HorizontalOffset;
        double verticalOffset = TerminalScrollViewer.VerticalOffset;
        TerminalText.Text = _engine.Terminal.BuildDisplay(hex, timestamps: hex);

        if (autoScroll)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!_isClosing && AutoScrollCheckBox.IsChecked == true)
                {
                    TerminalScrollViewer.ChangeView(null, TerminalScrollViewer.ScrollableHeight, null, true);
                }
            });
        }
        else
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!_isClosing && AutoScrollCheckBox.IsChecked != true)
                {
                    TerminalScrollViewer.ChangeView(horizontalOffset, verticalOffset, null, true);
                }
            });
        }
    }

    private void OnSerialDataReceived(object? sender, byte[] data)
    {
        _engine.Process(data);
    }

    private void OnDemoDataGenerated(byte[] data)
    {
        _engine.Process(data);
    }

    private void OnSerialFaulted(object? sender, string message)
    {
        if (_isClosing)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosing || !_isSerialConnected)
            {
                return;
            }

            _isSerialConnected = false;
            _serial.Close();
            StopRepeatSend();
            SetSerialInputsEnabled(true);
            SetSendEnabled(false);
            RestoreConnectButton();
            ShowError("串口连接已中断", message);
            UpdateUiStats();
        });
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_isSerialConnected)
        {
            DisconnectSerial();
            return;
        }

        if (!TryReadSerialSettings(out SerialSettings settings, out string error))
        {
            ShowError("串口参数有误", error);
            return;
        }

        if (_isDemo)
        {
            StopDemo();
        }

        StopRepeatSend();
        StatusInfoBar.IsOpen = false;
        _engine.SetProtocol(_protocol);
        ClearSessionUi();

        try
        {
            _serial.Open(settings);
        }
        catch (Exception ex)
        {
            _engine.ResetSession();
            ClearSessionUi();
            RestoreConnectButton();
            ShowError("无法打开串口", ex.Message);
            return;
        }

        _isSerialConnected = true;
        SetSerialInputsEnabled(false);
        SetSendEnabled(true);
        ShowDisconnectButton();
        UpdateUiStats();
        UpdateEmptyState();
    }

    private void DisconnectSerial()
    {
        if (!_isSerialConnected)
        {
            return;
        }

        StopRepeatSend();
        _isSerialConnected = false;
        _serial.Close();
        SetSerialInputsEnabled(true);
        SetSendEnabled(false);
        RestoreConnectButton();
        UpdateUiStats();
        UpdateEmptyState();
    }

    private bool TryReadSerialSettings(out SerialSettings settings, out string error)
    {
        settings = new SerialSettings();
        error = string.Empty;

        string? port = PortCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(port))
        {
            error = "未选择串口。";
            return false;
        }

        string baudText = BaudCombo.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baudText) && BaudCombo.SelectedItem is not null)
        {
            baudText = BaudCombo.SelectedItem.ToString() ?? string.Empty;
        }

        if (!int.TryParse(baudText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int baud)
            || baud < 50 || baud > 4_000_000)
        {
            error = "请输入有效的波特率。";
            return false;
        }

        settings.PortName = port;
        settings.BaudRate = baud;
        settings.DataBits = DataBitsCombo.SelectedIndex switch
        {
            0 => 5,
            1 => 6,
            2 => 7,
            _ => 8,
        };
        settings.Parity = ParityCombo.SelectedIndex switch
        {
            1 => Parity.Odd,
            2 => Parity.Even,
            3 => Parity.Mark,
            4 => Parity.Space,
            _ => Parity.None,
        };
        settings.StopBits = StopBitsCombo.SelectedIndex switch
        {
            1 => StopBits.OnePointFive,
            2 => StopBits.Two,
            _ => StopBits.One,
        };
        settings.Handshake = HandshakeCombo.SelectedIndex switch
        {
            1 => Handshake.RequestToSend,
            2 => Handshake.XOnXOff,
            3 => Handshake.RequestToSendXOnXOff,
            _ => Handshake.None,
        };
        settings.DtrEnable = DtrCheckBox.IsChecked == true;
        settings.RtsEnable = RtsCheckBox.IsChecked == true;
        return true;
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e)
    {
        LoadPorts(preserveSelection: true);
    }

    private void SetSerialInputsEnabled(bool enabled)
    {
        PortCombo.IsEnabled = enabled;
        RefreshPortsButton.IsEnabled = enabled;
        BaudCombo.IsEnabled = enabled;
        DataBitsCombo.IsEnabled = enabled;
        ParityCombo.IsEnabled = enabled;
        StopBitsCombo.IsEnabled = enabled;
        HandshakeCombo.IsEnabled = enabled;
        DtrCheckBox.IsEnabled = enabled;
        RtsCheckBox.IsEnabled = enabled;
        AdvancedExpander.IsEnabled = enabled;
    }

    private void SetSendEnabled(bool enabled)
    {
        SendButton.IsEnabled = enabled;
        RepeatToggleButton.IsEnabled = enabled;
        if (!enabled)
        {
            StopRepeatSend();
        }
    }

    private void ShowDisconnectButton()
    {
        ConnectButton.Style = null;
        ConnectButton.Content = "断开";
    }

    private void RestoreConnectButton()
    {
        ConnectButton.Style = _accentButtonStyle;
        ConnectButton.Content = "连接";
    }

    private void DemoToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _suppressDemoToggle)
        {
            return;
        }

        if (DemoToggle.IsOn)
        {
            StartDemo();
        }
        else
        {
            StopDemo();
        }
    }

    private void StartDemo()
    {
        if (_isDemo)
        {
            return;
        }

        if (_isSerialConnected)
        {
            DisconnectSerial();
        }

        StopRepeatSend();
        StatusInfoBar.IsOpen = false;
        _engine.SetProtocol(_protocol);
        ClearSessionUi();

        _isDemo = true;
        _suppressDemoToggle = true;
        DemoToggle.IsOn = true;
        _suppressDemoToggle = false;
        _demo.Start(_protocol);
        SetSerialInputsEnabled(true);
        SetSendEnabled(false);
        RestoreConnectButton();
        UpdateUiStats();
        UpdateEmptyState();

        if (_protocol == ProtocolKind.RawData)
        {
            SwitchToPage(terminal: true);
        }
    }

    private void StopDemo()
    {
        _isDemo = false;
        _demo.Stop();
        _suppressDemoToggle = true;
        DemoToggle.IsOn = false;
        _suppressDemoToggle = false;
        SetSerialInputsEnabled(true);
        SetSendEnabled(false);
        RestoreConnectButton();
        UpdateUiStats();
        UpdateEmptyState();
    }

    private void StartDemoFromEmpty_Click(object sender, RoutedEventArgs e)
    {
        if (!_isDemo)
        {
            StartDemo();
        }
        else
        {
            SwitchToPage(terminal: false);
        }
    }

    private void OpenTerminalFromEmpty_Click(object sender, RoutedEventArgs e)
    {
        SwitchToPage(terminal: true);
    }

    private void ProtocolCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        var protocol = (ProtocolKind)Math.Clamp(ProtocolCombo.SelectedIndex, 0, 2);
        if (protocol == _protocol)
        {
            return;
        }

        _protocol = protocol;
        _engine.SetProtocol(protocol);
        ClearSessionUi();
        HexToggleButton.IsChecked = protocol == ProtocolKind.JustFloat;
        PauseToggleButton.IsEnabled = protocol != ProtocolKind.RawData;

        if (_isDemo)
        {
            _demo.Stop();
            _demo.Start(protocol);
        }

        if (protocol == ProtocolKind.RawData)
        {
            SwitchToPage(terminal: true);
        }

        UpdateUiStats();
    }

    private void ViewSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_initialized)
        {
            return;
        }

        ApplyPageSelection(sender.SelectedItem == TerminalSelectorItem);
    }

    private void SwitchToPage(bool terminal)
    {
        SelectorBarItem item = terminal ? TerminalSelectorItem : WaveSelectorItem;
        if (ViewSelector.SelectedItem == item)
        {
            ApplyPageSelection(terminal);
        }
        else
        {
            ViewSelector.SelectedItem = item;
        }
    }

    private void ApplyPageSelection(bool terminal)
    {
        _isTerminalActive = terminal;
        WavePage.Visibility = terminal ? Visibility.Collapsed : Visibility.Visible;
        TerminalPage.Visibility = terminal ? Visibility.Visible : Visibility.Collapsed;
        ScopeView.SetActive(!terminal);
        if (terminal)
        {
            UpdateTerminal(force: true);
        }
    }

    private void ClearSessionUi()
    {
        _channels.Clear();
        ChannelCountText.Text = "0";
        ChannelList.Visibility = Visibility.Collapsed;
        ChannelsEmptyState.Visibility = Visibility.Visible;
        _lastTerminalVersion = -1;
        TerminalText.Text = string.Empty;
        TerminalBytesText.Text = "0 B";
        ScopeView.IsPaused = false;
        _isPaused = false;
        UpdatePauseVisuals();
        ScopeView.ClearDisplay();
        UpdateEmptyState();
    }

    private void PauseToggle_Click(object sender, RoutedEventArgs e)
    {
        _isPaused = PauseToggleButton.IsChecked == true;
        ScopeView.IsPaused = _isPaused;
        UpdatePauseVisuals();
    }

    private void UpdatePauseVisuals()
    {
        if (PauseToggleButton is null)
        {
            return;
        }

        PauseToggleButton.IsChecked = _isPaused;
        PauseIcon.Glyph = _isPaused ? "\uE768" : "\uE769";
        PauseButtonText.Text = _isPaused ? "继续" : "暂停";
        PauseToggleButton.IsEnabled = _protocol != ProtocolKind.RawData;
    }

    private void ClearWave_Click(object sender, RoutedEventArgs e)
    {
        _engine.Buffer.Clear();
        foreach (ChannelState channel in _channels)
        {
            channel.Reset();
        }

        ScopeView.ClearDisplay();
        UpdateEmptyState();
        UpdateChannelValues();
        UpdateUiStats();
    }

    private void TimeWindow_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        double seconds = 10;
        if (TimeWindowCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag
            && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            seconds = parsed;
        }

        ScopeView.WindowSeconds = seconds;
        ScopeView.Refresh();
    }

    private void AutoScaleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        bool auto = AutoScaleToggle.IsOn;
        ScopeView.AutoScale = auto;
        ManualRangePanel.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
        UpdateManualYRange();
        ScopeView.Refresh();
    }

    private void YRangeBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_initialized)
        {
            return;
        }

        UpdateManualYRange();
        ScopeView.Refresh();
    }

    private void UpdateManualYRange()
    {
        double min = YMinBox.Value;
        double max = YMaxBox.Value;
        if (double.IsNaN(min) || double.IsInfinity(min))
        {
            min = -50;
        }

        if (double.IsNaN(max) || double.IsInfinity(max))
        {
            max = 100;
        }

        if (max <= min)
        {
            max = min + 1;
        }

        ScopeView.ManualYMin = min;
        ScopeView.ManualYMax = max;
    }

    private void HexToggle_Click(object sender, RoutedEventArgs e)
    {
        _lastTerminalVersion = -1;
        UpdateTerminal(force: true);
    }

    private void CopyTerminal_Click(object sender, RoutedEventArgs e)
    {
        string text = TerminalText.Text ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            ShowError("复制失败", ex.Message);
        }
    }

    private void ClearTerminal_Click(object sender, RoutedEventArgs e)
    {
        _engine.Terminal.Clear();
        _lastTerminalVersion = -1;
        TerminalText.Text = string.Empty;
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.Buffer.Count == 0)
        {
            ShowInfo("没有可导出的数据", "请先连接串口或启动演示模式。");
            return;
        }

        (double[] times, double[][] series) = _engine.Buffer.ExportAll();
        if (series.Length == 0)
        {
            ShowInfo("没有可导出的波形", "当前协议没有波形通道。");
            return;
        }

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"SerialScope_{DateTime.Now:yyyyMMdd_HHmmss}",
            };
            picker.FileTypeChoices.Add("CSV 文件", new List<string> { ".csv" });
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            string csv = await Task.Run(() => BuildCsv(times, series));
            await FileIO.WriteTextAsync(file, csv, Windows.Storage.Streams.UnicodeEncoding.Utf8);
        }
        catch (Exception ex)
        {
            ShowError("导出失败", ex.Message);
        }
    }

    private static string BuildCsv(double[] times, double[][] series)
    {
        var builder = new StringBuilder(times.Length * (12 + (series.Length * 12)));
        builder.Append("Time(s)");
        for (int channel = 0; channel < series.Length; channel++)
        {
            builder.Append(",CH").Append(channel + 1);
        }

        builder.AppendLine();
        for (int row = 0; row < times.Length; row++)
        {
            builder.Append(times[row].ToString("F6", CultureInfo.InvariantCulture));
            for (int channel = 0; channel < series.Length; channel++)
            {
                builder.Append(',');
                double value = series[channel][row];
                if (double.IsFinite(value))
                {
                    builder.Append(value.ToString("G9", CultureInfo.InvariantCulture));
                }
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        await SendPayloadAsync();
    }

    private async Task<bool> SendPayloadAsync()
    {
        if (!_isSerialConnected)
        {
            ShowError("串口未连接", "请先连接串口。");
            return false;
        }

        if (_isSending)
        {
            return false;
        }

        if (!TryBuildPayload(out byte[] payload, out string error))
        {
            ShowError("发送内容有误", error);
            return false;
        }

        _isSending = true;
        SendButton.IsEnabled = false;
        try
        {
            using var cts = new CancellationTokenSource(4000);
            await _serial.WriteAsync(payload, cts.Token);
            return true;
        }
        catch (Exception ex)
        {
            ShowError("发送失败", ex.Message);
            if (RepeatToggleButton.IsChecked == true)
            {
                StopRepeatSend();
            }

            return false;
        }
        finally
        {
            _isSending = false;
            SendButton.IsEnabled = _isSerialConnected;
        }
    }

    private bool TryBuildPayload(out byte[] payload, out string error)
    {
        payload = Array.Empty<byte>();
        error = string.Empty;
        string text = SendTextBox.Text ?? string.Empty;
        if (text.Length == 0)
        {
            error = "请输入要发送的内容。";
            return false;
        }

        byte[] body;
        if (SendHexRadio.IsChecked == true)
        {
            if (!TryParseHex(text, out body, out error))
            {
                return false;
            }
        }
        else
        {
            body = Encoding.UTF8.GetBytes(text);
        }

        byte[] ending = GetLineEnding();
        if (ending.Length == 0)
        {
            payload = body;
        }
        else
        {
            payload = new byte[body.Length + ending.Length];
            Buffer.BlockCopy(body, 0, payload, 0, body.Length);
            Buffer.BlockCopy(ending, 0, payload, body.Length, ending.Length);
        }

        return true;
    }

    private byte[] GetLineEnding() => EndingCombo.SelectedIndex switch
    {
        1 => new byte[] { 0x0D },
        2 => new byte[] { 0x0A },
        3 => new byte[] { 0x0D, 0x0A },
        _ => Array.Empty<byte>(),
    };

    private static bool TryParseHex(string input, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = string.Empty;
        var digits = new StringBuilder(input.Length);

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (char.IsWhiteSpace(c) || c is ',' or ';' or '-' or ':' or '|')
            {
                continue;
            }

            if (c == '0' && i + 1 < input.Length && (input[i + 1] == 'x' || input[i + 1] == 'X'))
            {
                i++;
                continue;
            }

            if (!Uri.IsHexDigit(c))
            {
                error = $"HEX 内容包含非法字符 '{c}'。";
                return false;
            }

            digits.Append(c);
        }

        if (digits.Length == 0)
        {
            error = "请输入要发送的 HEX 字节。";
            return false;
        }

        if (digits.Length % 2 != 0)
        {
            error = "HEX 字节数必须为偶数。";
            return false;
        }

        bytes = new byte[digits.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(digits.ToString(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return true;
    }

    private void RepeatToggle_Click(object sender, RoutedEventArgs e)
    {
        if (RepeatToggleButton.IsChecked != true)
        {
            StopRepeatSend();
            return;
        }

        if (!_isSerialConnected)
        {
            RepeatToggleButton.IsChecked = false;
            return;
        }

        if (!TryBuildPayload(out _, out string error))
        {
            RepeatToggleButton.IsChecked = false;
            ShowError("发送内容有误", error);
            return;
        }

        double interval = RepeatIntervalBox.Value;
        if (double.IsNaN(interval) || interval < 50)
        {
            interval = 500;
        }

        _repeatTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(interval, 50, 60000));
        _repeatTimer.Start();
    }

    private async void OnRepeatTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_isSerialConnected)
        {
            StopRepeatSend();
            return;
        }

        if (_isSending)
        {
            return;
        }

        bool success = await SendPayloadAsync();
        if (!success)
        {
            StopRepeatSend();
        }
    }

    private void StopRepeatSend()
    {
        if (_repeatTimer is null)
        {
            return;
        }

        _repeatTimer.Stop();
        if (RepeatToggleButton is not null)
        {
            RepeatToggleButton.IsChecked = false;
        }
    }

    private void ShowError(string title, string message)
    {
        StatusInfoBar.Severity = InfoBarSeverity.Error;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }

    private void ShowInfo(string title, string message)
    {
        StatusInfoBar.Severity = InfoBarSeverity.Informational;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }

    private static string FormatBytes(double bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes:0} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024:0.0} KB";
        }

        return $"{bytes / (1024 * 1024):0.0} MB";
    }

    private static string FormatByteRate(double bytesPerSecond) => FormatBytes(bytesPerSecond) + "/s";

    private static string FormatCount(long count)
    {
        if (count >= 1_000_000)
        {
            return $"{count / 1_000_000.0:0.0}M";
        }

        if (count >= 1_000)
        {
            return $"{count / 1_000.0:0.#}k";
        }

        return count.ToString(CultureInfo.InvariantCulture);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _isClosing = true;
        _uiTimer.Stop();
        _repeatTimer.Stop();
        _demo.Dispose();
        _serial.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
