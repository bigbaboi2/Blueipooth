using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Blueipooth.App.Services;
using Blueipooth.Protocol.Control;
using Blueipooth.Protocol.Discovery;
using Blueipooth.UsbIp;
using QRCoder;

namespace Blueipooth.App;

public sealed class BleDeviceViewItem : INotifyPropertyChanged
{
    private string _displayName = "";
    private string _subtitle = "";
    private string _rssiText = "";
    private System.Windows.Media.Brush _rssiBrush = System.Windows.Media.Brushes.LightGray;

    public required string Address { get; init; }

    public required string DisplayName
    {
        get => _displayName;
        set { if (_displayName != value) { _displayName = value; OnChanged(nameof(DisplayName)); } }
    }

    public required string Subtitle
    {
        get => _subtitle;
        set { if (_subtitle != value) { _subtitle = value; OnChanged(nameof(Subtitle)); } }
    }

    public required string RssiText
    {
        get => _rssiText;
        set { if (_rssiText != value) { _rssiText = value; OnChanged(nameof(RssiText)); } }
    }

    public required System.Windows.Media.Brush RssiBrush
    {
        get => _rssiBrush;
        set { if (!Equals(_rssiBrush, value)) { _rssiBrush = value; OnChanged(nameof(RssiBrush)); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

public partial class MainWindow : Window
{
    private readonly BlueipoothCoordinator _coordinator = new();
    private readonly DispatcherTimer _uiRefreshTimer;
    private readonly ConcurrentDictionary<string, BleAdvReportMessage> _bleDevices = new();
    private readonly ConcurrentDictionary<string, long> _bleFirstSeenOrder = new();
    private readonly ObservableCollection<BleDeviceViewItem> _bleViewItems = new();
    private readonly Dictionary<string, BleDeviceViewItem> _bleViewLookup = new();
    private readonly ConcurrentQueue<string> _pendingUiLogs = new();
    private long _bleOrderCounter;
    private AppSettings _settings = new();
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _isExplicitExit;
    private bool _uiInitialized;
    private int _bleListDirty;
    private int _mediaTickCounter;
    private CancellationTokenSource? _jitterDebounceCts;
    private double _latestSpeakerPeak;
    private double _latestMicPeak;

    public MainWindow()
    {
        InitializeComponent();
        LstBleDevices.ItemsSource = _bleViewItems;
        LoadAndApplySettings();
        SetupSystemTray();

        _coordinator.LogAdded += line => _pendingUiLogs.Enqueue(line);
        _coordinator.StateChanged += () => Dispatcher.BeginInvoke(RefreshUiState);
        _coordinator.BleDeviceDiscovered += adv =>
        {
            _bleFirstSeenOrder.GetOrAdd(adv.Address, _ => Interlocked.Increment(ref _bleOrderCounter));
            _bleDevices[adv.Address] = adv;
            Interlocked.Exchange(ref _bleListDirty, 1);
        };
        _coordinator.AudioEngine.SpeakerPeakLevelChanged += lvl => Interlocked.Exchange(ref _latestSpeakerPeak, lvl);
        _coordinator.AudioEngine.MicPeakLevelChanged += lvl => Interlocked.Exchange(ref _latestMicPeak, lvl);

        _uiRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _uiRefreshTimer.Tick += (_, _) => RefreshCountersAndStatus();

        Loaded += OnMainWindowLoaded;
        Closing += OnMainWindowClosing;
    }

    private void LoadAndApplySettings()
    {
        _settings = AppSettings.Load();
        ApplyTheme(_settings.Theme);
        ExpLogs.IsExpanded = _settings.ExpandLogsByDefault;

        RbThemeMidnight.IsChecked = _settings.Theme == AppThemeMode.Midnight;
        RbThemeOcean.IsChecked = _settings.Theme == AppThemeMode.Ocean;
        RbThemeLight.IsChecked = _settings.Theme == AppThemeMode.Light;
        ChkExpandLogsDefault.IsChecked = _settings.ExpandLogsByDefault;

        RbCloseTray.IsChecked = _settings.CloseBehavior == WindowCloseBehavior.MinimizeToTray;
        RbCloseExit.IsChecked = _settings.CloseBehavior == WindowCloseBehavior.ExitApplication;
    }

    private static void ApplyTheme(AppThemeMode theme)
    {
        var res = System.Windows.Application.Current.Resources;
        switch (theme)
        {
            case AppThemeMode.Ocean:
                SetBrush(res, "BgDarkBrush", "#071426");
                SetBrush(res, "SidebarBgBrush", "#0B1E36");
                SetBrush(res, "CardBgBrush", "#102847");
                SetBrush(res, "SubCardBgBrush", "#09182E");
                SetBrush(res, "CardBorderBrush", "#1E4270");
                SetBrush(res, "AccentCyanBrush", "#22D3EE");
                SetBrush(res, "TextPrimaryBrush", "#F0F9FF");
                SetBrush(res, "TextMutedBrush", "#93C5FD");
                SetBrush(res, "SecondaryBtnBgBrush", "#17375E");
                SetBrush(res, "SecondaryBtnHoverBrush", "#1E497A");
                SetBrush(res, "SecondaryBtnFgBrush", "#E0F2FE");
                SetBrush(res, "NavActiveBgBrush", "#164E63");
                break;

            case AppThemeMode.Light:
                SetBrush(res, "BgDarkBrush", "#F1F5F9");
                SetBrush(res, "SidebarBgBrush", "#FFFFFF");
                SetBrush(res, "CardBgBrush", "#FFFFFF");
                SetBrush(res, "SubCardBgBrush", "#F8FAFC");
                SetBrush(res, "CardBorderBrush", "#CBD5E1");
                SetBrush(res, "AccentCyanBrush", "#0284C7");
                SetBrush(res, "TextPrimaryBrush", "#0F172A");
                SetBrush(res, "TextMutedBrush", "#475569");
                SetBrush(res, "SecondaryBtnBgBrush", "#E2E8F0");
                SetBrush(res, "SecondaryBtnHoverBrush", "#CBD5E1");
                SetBrush(res, "SecondaryBtnFgBrush", "#1E293B");
                SetBrush(res, "NavActiveBgBrush", "#E0F2FE");
                break;

            case AppThemeMode.Midnight:
            default:
                SetBrush(res, "BgDarkBrush", "#0B1120");
                SetBrush(res, "SidebarBgBrush", "#11192B");
                SetBrush(res, "CardBgBrush", "#162033");
                SetBrush(res, "SubCardBgBrush", "#0F172A");
                SetBrush(res, "CardBorderBrush", "#263552");
                SetBrush(res, "AccentCyanBrush", "#38BDF8");
                SetBrush(res, "TextPrimaryBrush", "#F8FAFC");
                SetBrush(res, "TextMutedBrush", "#94A3B8");
                SetBrush(res, "SecondaryBtnBgBrush", "#24324A");
                SetBrush(res, "SecondaryBtnHoverBrush", "#314464");
                SetBrush(res, "SecondaryBtnFgBrush", "#E2E8F0");
                SetBrush(res, "NavActiveBgBrush", "#1E3A5F");
                break;
        }
    }

    private static void SetBrush(ResourceDictionary res, string key, string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        res[key] = new SolidColorBrush(color);
    }

    private async void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        _uiInitialized = true;
        RenderQrCode();
        RefreshUiState();
        _uiRefreshTimer.Start();

        try
        {
            await _coordinator.InitializeAsync();
            RenderQrCode();
            RefreshUiState();

            if (!UsbipWin2Manager.IsDriverInstalled || !VirtualAudioCableInstaller.IsInstalled)
            {
                var choice = System.Windows.MessageBox.Show(
                    this,
                    "Máy đang thiếu thành phần cần thiết. Blueipooth có thể tải và cài VB-CABLE cùng driver Bluetooth ảo. Windows sẽ hỏi quyền quản trị và có thể yêu cầu khởi động lại.",
                    "Hoàn tất thiết lập Blueipooth",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (choice == MessageBoxResult.Yes)
                    await InstallMissingComponentsAsync();
            }

            string[] cliArgs = Environment.GetCommandLineArgs();
            int ssIdx = Array.IndexOf(cliArgs, "--screenshot");
            if (ssIdx >= 0 && ssIdx + 1 < cliArgs.Length)
            {
                await Task.Delay(700);
                UpdateLayout();
                int w = Math.Max(1040, (int)ActualWidth);
                int h = Math.Max(720, (int)ActualHeight);
                var dv = new System.Windows.Media.DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
                    if (Content is UIElement rootElem)
                    {
                        var vb = new System.Windows.Media.VisualBrush(rootElem) { Stretch = System.Windows.Media.Stretch.None };
                        dc.DrawRectangle(vb, null, new Rect(0, 0, w, h));
                    }
                }
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(cliArgs[ssIdx + 1]))
                {
                    encoder.Save(fs);
                }
                await ShutdownCleanlyAsync();
            }
        }
        catch (Exception ex)
        {
            TxtLogs.AppendText($"[Error] Initialization error: {ex.Message}{Environment.NewLine}");
        }
    }

    private void OnNavTabChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiInitialized) return;

        PanelConnect.Visibility = TabConnect.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelBluetooth.Visibility = TabBluetooth.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelAudio.Visibility = TabAudio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        // Smooth fade + subtle slide transition
        TabContentHost.Opacity = 0.0;
        TabContentSlide.Y = 8;

        var fade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var slide = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        TabContentHost.BeginAnimation(OpacityProperty, fade);
        TabContentSlide.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void SetupSystemTray()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "Blueipooth — Virtual Bluetooth 5.2 Bridge",
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Mở bảng điều khiển Blueipooth", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Thoát hoàn toàn & Tháo Bluetooth ảo", null, async (_, _) => await ShutdownCleanlyAsync());
        _trayIcon.ContextMenuStrip = menu;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void RenderQrCode()
    {
        var payload = _coordinator.GetPairingQrPayload();
        string uri = payload.ToUri();

        using var generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        using var pngQr = new PngByteQRCode(data);
        byte[] pngBytes = pngQr.GetGraphic(8);

        var bmp = new BitmapImage();
        using (var ms = new MemoryStream(pngBytes))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
        }
        ImgQrCode.Source = bmp;

        var candidates = NetworkDiscoveryService.GetLocalCandidates();
        TxtCandidateIps.Text = candidates.Count > 0
            ? "IPs: " + string.Join(" • ", candidates.Select(c => c.IsUsbInterface ? $"{c.IpAddress} (USB)" : c.IpAddress))
            : "IPs: 127.0.0.1 (Local / ADB USB)";
    }

    private void RefreshUiState()
    {
        bool drvInstalled = UsbipWin2Manager.IsDriverInstalled;
        TxtDriverStatus.Text = drvInstalled ? "Đã cài (WHQL)" : "Chưa cài";
        TxtDriverStatus.Foreground = drvInstalled
            ? System.Windows.Media.Brushes.LightGreen
            : System.Windows.Media.Brushes.Orange;
        bool cableInstalled = VirtualAudioCableInstaller.IsInstalled;
        TxtCableStatus.Text = cableInstalled ? "Đã cài" : "Chưa cài";
        TxtCableStatus.Foreground = cableInstalled
            ? System.Windows.Media.Brushes.LightGreen
            : System.Windows.Media.Brushes.Orange;
        BtnInstallDriver.Visibility = drvInstalled && cableInstalled ? Visibility.Collapsed : Visibility.Visible;

        bool attached = _coordinator.UsbIp.IsDriverAttached;
        TxtAdapterStatus.Text = attached ? "ĐANG BẬT" : "Chưa gắn";
        TxtAdapterStatus.Foreground = attached
            ? System.Windows.Media.Brushes.LightGreen
            : System.Windows.Media.Brushes.Gold;
        BtnToggleVirtualBt.Content = attached
            ? "Ngắt Card Bluetooth Ảo"
            : "Bật Card Bluetooth Ảo";

        var phone = _coordinator.LanServer.CurrentPhone;
        BtnPauseConnection.Content = _coordinator.IsConnectionPaused ? "Tiếp tục kết nối" : "Tạm ngừng kết nối";
        if (phone is not null)
        {
            string transport = phone.IsUsbLink ? "Cáp USB (~1ms)" : $"Wi-Fi/LAN ({phone.RttMs:0.0} ms)";
            TxtPhoneStatus.Text = $"Đã kết nối với {phone.DeviceName}";
            TxtPhoneStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
            TxtCandidateIps.Text = $"IP: {phone.RemoteIp} • {transport}";

            DotTopConn.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 222, 128));
            TxtTopConnBadge.Text = $"Đã nối: {phone.DeviceName}";

            TxtQrRouteChannel.Text = _coordinator.CurrentAudioMode == "NativeA2dp"
                ? (attached ? "Kênh Bluetooth Ảo (Windows A2DP)" : "Kênh Bluetooth Ảo (Chưa bật Adapter)")
                : $"Kênh IP trực tiếp ({(phone.IsUsbLink ? "Cáp USB" : "Wi-Fi/LAN")})";

            var ds = _coordinator.LatestPhoneStatus;
            string? btConnectedList = ds?.ConnectedBtDevices?.Trim();
            if (ds is not null)
            {
                TxtPhoneBattery.Text = ds.BatteryPercent >= 0
                    ? (ds.IsCharging ? $"{ds.BatteryPercent}% (Đang sạc)" : $"{ds.BatteryPercent}%")
                    : "Không rõ";
                TxtPhoneBtState.Text = ds.BluetoothEnabled ? "Đang bật" : "Đang tắt";
                TxtPhoneBtState.Foreground = ds.BluetoothEnabled
                    ? System.Windows.Media.Brushes.LightGreen
                    : System.Windows.Media.Brushes.Orange;
                TxtConnectedBtNames.Text = !string.IsNullOrEmpty(btConnectedList) ? btConnectedList : "Không có";
                TxtConnectedBtNames.Foreground = !string.IsNullOrEmpty(btConnectedList)
                    ? System.Windows.Media.Brushes.LightGreen
                    : System.Windows.Media.Brushes.LightGray;
                TxtPhoneMediaNow.Text = string.IsNullOrWhiteSpace(ds.MediaInfo) ? "Không có media đang phát" : ds.MediaInfo;
            }
            else
            {
                TxtPhoneBattery.Text = "Đang đọc...";
                TxtPhoneBtState.Text = "Đang đọc...";
                TxtPhoneBtState.Foreground = System.Windows.Media.Brushes.LightGray;
                TxtConnectedBtNames.Text = "Đang đọc...";
                TxtConnectedBtNames.Foreground = System.Windows.Media.Brushes.LightGray;
                TxtPhoneMediaNow.Text = "Đang đồng bộ...";
            }

            double oneWayNetMs = phone.RttMs > 0 ? Math.Max(0.2, phone.RttMs / 2.0) : (phone.IsUsbLink ? 0.5 : 1.0);
            int bufMs = _coordinator.CurrentJitterBufferMs;
            const int frameProcMs = Blueipooth.Audio.WasapiOpusAudioEngine.FrameDurationMs; // 10ms

            if (ds is { AudioActivelyPlaying: true })
            {
                double realTotalMs = oneWayNetMs + ds.AudioJitterMs + ds.AudioHardwareMs + frameProcMs;
                TxtAudioLatency.Text = $"Đo thực tế: ~{realTotalMs:0.0} ms";
                TxtAudioLatency.Foreground = System.Windows.Media.Brushes.LightGreen;
                TxtQrAudioLatency.Text = $"Đo real-time: ~{realTotalMs:0.0} ms (Mạng {oneWayNetMs:0.0}ms + Đệm {ds.AudioJitterMs}ms + AudioTrack {ds.AudioHardwareMs}ms + Opus {frameProcMs}ms)";
            }
            else
            {
                double estTotalMs = oneWayNetMs + bufMs + frameProcMs;
                TxtAudioLatency.Text = $"Mạng {oneWayNetMs:0.0} ms (Đệm {bufMs}ms)";
                TxtAudioLatency.Foreground = System.Windows.Media.Brushes.LightSkyBlue;
                TxtQrAudioLatency.Text = $"Mạng real-time: {oneWayNetMs:0.0} ms (RTT {phone.RttMs:0.0}ms) • Chờ phát âm thanh (Đệm {bufMs}ms)";
            }
        }
        else
        {
            TxtPhoneStatus.Text = _coordinator.IsConnectionPaused
                ? "Đang tạm ngừng kết nối"
                : "Mở Blueipooth trên điện thoại rồi quét mã";
            TxtPhoneStatus.Foreground = _coordinator.IsConnectionPaused
                ? System.Windows.Media.Brushes.LightSkyBlue
                : System.Windows.Media.Brushes.Gold;
            TxtCandidateIps.Text = _coordinator.IsConnectionPaused
                ? "Bấm Tiếp tục kết nối để cho phép điện thoại kết nối lại."
                : "IP PC: " + string.Join(" • ", NetworkDiscoveryService.GetLocalCandidates().Select(c => c.IpAddress));

            DotTopConn.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(251, 191, 36));
            TxtTopConnBadge.Text = _coordinator.IsConnectionPaused ? "Đang tạm ngừng" : "Chờ kết nối điện thoại";

            TxtQrRouteChannel.Text = "Chờ kết nối điện thoại...";
            TxtQrAudioLatency.Text = "Độ trễ thực tế: --";
            TxtPhoneBattery.Text = "--";
            TxtPhoneBtState.Text = "--";
            TxtPhoneBtState.Foreground = System.Windows.Media.Brushes.LightGray;
            TxtConnectedBtNames.Text = "--";
            TxtConnectedBtNames.Foreground = System.Windows.Media.Brushes.LightGray;
            TxtPhoneMediaNow.Text = "Chưa kết nối";

            TxtAudioLatency.Text = "Chờ kết nối";
            TxtAudioLatency.Foreground = System.Windows.Media.Brushes.Gray;
        }

        // BLE scan UI state
        bool scanning = _coordinator.IsBleScanning;
        BtnScanBle.Content = scanning ? "Dừng quét" : "Quét Bluetooth";
        BtnScanBle.IsEnabled = phone is not null;
        PbBleScanning.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        TxtBleScanStatus.Text = phone is null
            ? "Hãy kết nối điện thoại trước để gửi yêu cầu quét Bluetooth"
            : scanning
                ? $"Đang quét thiết bị BLE qua điện thoại... ({_bleDevices.Count} thiết bị)"
                : $"Sẵn sàng — đã tìm thấy {_bleDevices.Count} thiết bị BLE";

        string? realBtName = _coordinator.LatestPhoneStatus?.ConnectedBtDevices?.Split(',').FirstOrDefault()?.Trim();
        if (!string.IsNullOrEmpty(realBtName))
        {
            TxtHeadsetName.Text = realBtName;
            TxtPhoneAudioDevice.Text = realBtName;
            TxtDualMac.Text = $"Stereo: {_coordinator.SynthesizedStereoMac ?? "..."} | Mic: {_coordinator.SynthesizedMicMac ?? "N/A"}";
        }
        else if (_coordinator.AttachedHeadset is { Connected: true } hs)
        {
            TxtHeadsetName.Text = hs.Name;
            TxtPhoneAudioDevice.Text = hs.Name;
            TxtDualMac.Text = $"Stereo: {_coordinator.SynthesizedStereoMac ?? "..."} | Mic: {_coordinator.SynthesizedMicMac ?? "N/A"}";
        }
        else
        {
            TxtHeadsetName.Text = "Loa / Tai nghe trên Điện thoại";
            TxtPhoneAudioDevice.Text = phone is not null ? "Loa điện thoại" : "Chưa kết nối";
            TxtDualMac.Text = "Tự tách Dual-MAC khi ĐT nối AirPods/Loa BT";
        }
    }

    private void RefreshCountersAndStatus()
    {
        TxtHciCounters.Text = $"{_coordinator.UsbIp.TotalHciCommands} Cmd / {_coordinator.UsbIp.TotalHciEvents} Evt";

        double speakerPeak = Interlocked.Exchange(ref _latestSpeakerPeak, 0);
        bool speakerActive = _coordinator.AudioEngine.IsStreamingSpeaker ||
                             (_coordinator.CurrentAudioMode == "NativeA2dp" && _coordinator.LanServer.CurrentPhone is not null);
        PbSpeakerLevel.Value = speakerActive
            ? Math.Max(speakerPeak * 100.0, PbSpeakerLevel.Value * 0.55)
            : 0;

        double micPeak = Interlocked.Exchange(ref _latestMicPeak, 0);
        PbMicLevel.Value = Math.Max(micPeak * 100.0, PbMicLevel.Value * 0.55);

        if ((++_mediaTickCounter % 20) == 0)
        {
            TxtPcMediaNow.Text = Blueipooth.Audio.WasapiOpusAudioEngine.GetActivePcMediaSummary();
        }

        if ((_mediaTickCounter % 3) == 0 && Interlocked.Exchange(ref _bleListDirty, 0) != 0)
        {
            RefreshBleList();
        }

        var batch = new StringBuilder();
        for (int i = 0; i < 250 && _pendingUiLogs.TryDequeue(out string? line); i++)
            batch.AppendLine(line);
        if (batch.Length > 0)
        {
            TxtLogs.AppendText(batch.ToString());
            const int maxLogChars = 100_000;
            if (TxtLogs.Text.Length > maxLogChars)
            {
                int removeChars = TxtLogs.Text.Length - maxLogChars + 10_000;
                TxtLogs.Select(0, removeChars);
                TxtLogs.SelectedText = string.Empty;
            }
            TxtLogs.ScrollToEnd();
        }
    }

    private void RefreshBleList(bool rebuildAll = false)
    {
        bool hideUnnamed = ChkHideUnnamedBle.IsChecked == true;
        var filtered = _bleDevices.Values
            .Where(d => !hideUnnamed || (!string.IsNullOrWhiteSpace(d.Name) && d.Connectable))
            .OrderBy(d => _bleFirstSeenOrder.GetValueOrDefault(d.Address, long.MaxValue))
            .Take(50)
            .ToList();

        if (rebuildAll)
        {
            _bleViewItems.Clear();
            _bleViewLookup.Clear();
        }

        var activeAddrs = new HashSet<string>(filtered.Count);
        foreach (var item in filtered)
        {
            activeAddrs.Add(item.Address);
            string label = string.IsNullOrWhiteSpace(item.Name) ? "(Thiết bị BLE ẩn tên)" : item.Name;
            string conn = item.Connectable ? "Có thể kết nối" : "Chỉ phát quảng bá";
            System.Windows.Media.Brush rssiBrush = item.Rssi >= -65
                ? System.Windows.Media.Brushes.LightGreen
                : item.Rssi >= -80
                    ? System.Windows.Media.Brushes.Gold
                    : System.Windows.Media.Brushes.Salmon;
            string rssiText = $"{item.Rssi} dBm";
            string subtitle = $"{item.Address} • {conn}";

            if (_bleViewLookup.TryGetValue(item.Address, out var existing))
            {
                existing.DisplayName = label;
                existing.Subtitle = subtitle;
                existing.RssiText = rssiText;
                existing.RssiBrush = rssiBrush;
            }
            else
            {
                var viewItem = new BleDeviceViewItem
                {
                    Address = item.Address,
                    DisplayName = label,
                    Subtitle = subtitle,
                    RssiText = rssiText,
                    RssiBrush = rssiBrush
                };
                _bleViewLookup[item.Address] = viewItem;
                _bleViewItems.Add(viewItem);
            }
        }

        if (!rebuildAll && _bleViewItems.Count != filtered.Count)
        {
            for (int i = _bleViewItems.Count - 1; i >= 0; i--)
            {
                if (!activeAddrs.Contains(_bleViewItems[i].Address))
                {
                    _bleViewLookup.Remove(_bleViewItems[i].Address);
                    _bleViewItems.RemoveAt(i);
                }
            }
        }

        TxtBleEmptyHint.Visibility = _bleViewItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBleFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiInitialized) return;
        RefreshBleList(rebuildAll: true);
    }

    private async void OnToggleBleScanClick(object sender, RoutedEventArgs e)
    {
        if (_coordinator.LanServer.CurrentPhone is null) return;
        BtnScanBle.IsEnabled = false;
        try
        {
            await _coordinator.RequestBleScanAsync(!_coordinator.IsBleScanning);
        }
        finally
        {
            BtnScanBle.IsEnabled = _coordinator.LanServer.CurrentPhone is not null;
            RefreshUiState();
        }
    }

    private void OnClearBleListClick(object sender, RoutedEventArgs e)
    {
        _bleDevices.Clear();
        _bleFirstSeenOrder.Clear();
        RefreshBleList(rebuildAll: true);
        RefreshUiState();
    }

    private void OnClearLogsClick(object sender, RoutedEventArgs e) => TxtLogs.Clear();

    private async void OnToggleVirtualBtClick(object sender, RoutedEventArgs e)
    {
        BtnToggleVirtualBt.IsEnabled = false;
        try
        {
            bool enable = !_coordinator.UsbIp.IsDriverAttached;
            var (ok, msg) = await _coordinator.SetVirtualBluetoothCardEnabledAsync(enable);
            if (!ok && enable)
            {
                System.Windows.MessageBox.Show(this, msg, "Blueipooth Virtual Bluetooth", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally
        {
            BtnToggleVirtualBt.IsEnabled = true;
            RefreshUiState();
        }
    }

    private async void OnInstallDriverClick(object sender, RoutedEventArgs e)
    {
        BtnInstallDriver.IsEnabled = false;
        try
        {
            await InstallMissingComponentsAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Thiết lập Blueipooth", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            BtnInstallDriver.IsEnabled = true;
            RefreshUiState();
        }
    }

    private async Task InstallMissingComponentsAsync()
    {
        var messages = new List<string>();
        if (!UsbipWin2Manager.IsDriverInstalled)
        {
            try
            {
                bool installed = await UsbipWin2Manager.InstallDriverViaWingetAsync();
                messages.Add(installed
                    ? "Đã cài usbip-win2."
                    : "Chưa cài được usbip-win2. Kiểm tra winget hoặc quyền quản trị.");
            }
            catch (Exception ex)
            {
                messages.Add($"Chưa cài được usbip-win2: {ex.Message}");
            }
        }

        if (!VirtualAudioCableInstaller.IsInstalled)
        {
            try
            {
                var cableResult = await VirtualAudioCableInstaller.InstallAsync();
                messages.Add(cableResult.Message);
            }
            catch (Exception ex)
            {
                messages.Add($"Chưa cài được VB-CABLE: {ex.Message}");
            }
        }

        if (UsbipWin2Manager.IsDriverInstalled && !_coordinator.UsbIp.IsDriverAttached)
        {
            var attachResult = await _coordinator.SetVirtualBluetoothCardEnabledAsync(true);
            messages.Add(attachResult.Message);
        }

        RefreshUiState();
        if (messages.Count > 0)
        {
            bool hasFailure = messages.Any(m =>
                m.Contains("chưa", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("không", StringComparison.OrdinalIgnoreCase));
            System.Windows.MessageBox.Show(
                this,
                string.Join(Environment.NewLine + Environment.NewLine, messages),
                "Thiết lập Blueipooth",
                MessageBoxButton.OK,
                hasFailure ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
    }

    private void OnRefreshQrClick(object sender, RoutedEventArgs e) => RenderQrCode();

    private void OnToggleConnectionPauseClick(object sender, RoutedEventArgs e)
    {
        _coordinator.SetConnectionPaused(!_coordinator.IsConnectionPaused);
        RefreshUiState();
    }

    private async void OnAudioModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiInitialized) return;
        string mode = RbGamingMode.IsChecked == true ? "GamingOpus" : "NativeA2dp";
        bool mic = ChkMicBridge.IsChecked == true;
        int bufMs = (int)SldJitterBuffer.Value;
        await _coordinator.SetAudioModeAsync(mode, mic, bufMs);
    }

    private async void OnJitterSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiInitialized) return;
        int bufMs = (int)Math.Round(e.NewValue);
        TxtBufferVal.Text = $"{bufMs} ms";

        _jitterDebounceCts?.Cancel();
        _jitterDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _jitterDebounceCts = cts;

        try
        {
            await Task.Delay(180, cts.Token);
            string mode = RbGamingMode.IsChecked == true ? "GamingOpus" : "NativeA2dp";
            bool mic = ChkMicBridge.IsChecked == true;
            await _coordinator.SetAudioModeAsync(mode, mic, bufMs);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnNoiseFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiInitialized) return;
        _coordinator.AudioEngine.IsMicNoiseFilterEnabled = ChkMicNoiseFilter.IsChecked == true;
    }

    private void OnOpenBtSettingsClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });

    private void OnMinimizeToTrayClick(object sender, RoutedEventArgs e) => Hide();

    // --- Settings Popup Handlers ---
    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Visible;
        SettingsOverlay.Opacity = 0;
        SettingsModalScale.ScaleX = 0.94;
        SettingsModalScale.ScaleY = 0.94;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160));
        var scale = new DoubleAnimation(0.94, 1.0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        SettingsOverlay.BeginAnimation(OpacityProperty, fade);
        SettingsModalScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        SettingsModalScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    private void OnCloseSettingsClick(object sender, RoutedEventArgs e) =>
        SettingsOverlay.Visibility = Visibility.Collapsed;

    private void OnSettingsBackdropMouseDown(object sender, MouseButtonEventArgs e) =>
        SettingsOverlay.Visibility = Visibility.Collapsed;

    private void OnSettingsCardMouseDown(object sender, MouseButtonEventArgs e) =>
        e.Handled = true;

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiInitialized) return;

        _settings.Theme = RbThemeOcean.IsChecked == true
            ? AppThemeMode.Ocean
            : RbThemeLight.IsChecked == true
                ? AppThemeMode.Light
                : AppThemeMode.Midnight;

        _settings.ExpandLogsByDefault = ChkExpandLogsDefault.IsChecked == true;
        _settings.CloseBehavior = RbCloseExit.IsChecked == true
            ? WindowCloseBehavior.ExitApplication
            : WindowCloseBehavior.MinimizeToTray;

        ApplyTheme(_settings.Theme);
        _settings.Save();
    }

    private async void OnExitApplicationClick(object sender, RoutedEventArgs e) => await ShutdownCleanlyAsync();

    private async void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExplicitExit && _settings.CloseBehavior == WindowCloseBehavior.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            _trayIcon?.ShowBalloonTip(
                2000,
                "Blueipooth đang chạy ngầm",
                "Kết nối vẫn hoạt động. Dùng nút ⚙ Cài đặt hoặc biểu tượng khay hệ thống để thoát hẳn.",
                System.Windows.Forms.ToolTipIcon.Info);
            return;
        }

        await ShutdownCleanlyAsync();
    }

    private async Task ShutdownCleanlyAsync()
    {
        if (_isExplicitExit) return;
        _isExplicitExit = true;
        _uiRefreshTimer.Stop();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        await _coordinator.DisposeAsync();
        System.Windows.Application.Current.Shutdown();
    }
}
