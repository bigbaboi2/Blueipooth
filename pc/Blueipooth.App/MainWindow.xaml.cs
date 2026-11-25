using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Blueipooth.App.Services;
using Blueipooth.Protocol.Control;
using Blueipooth.Protocol.Discovery;
using Blueipooth.UsbIp;
using QRCoder;

namespace Blueipooth.App;

public partial class MainWindow : Window
{
    private readonly BlueipoothCoordinator _coordinator = new();
    private readonly DispatcherTimer _uiRefreshTimer;
    private readonly ConcurrentDictionary<string, BleAdvReportMessage> _bleDevices = new();
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _isExplicitExit;
    private bool _uiInitialized;

    public MainWindow()
    {
        InitializeComponent();
        SetupSystemTray();

        _coordinator.LogAdded += line => Dispatcher.BeginInvoke(() =>
        {
            TxtLogs.AppendText(line + Environment.NewLine);
            TxtLogs.ScrollToEnd();
        });
        _coordinator.StateChanged += () => Dispatcher.BeginInvoke(RefreshUiState);
        _coordinator.BleDeviceDiscovered += adv =>
        {
            _bleDevices[adv.Address] = adv;
            Dispatcher.BeginInvoke(RefreshBleList);
        };
        _coordinator.AudioEngine.SpeakerPeakLevelChanged += lvl =>
            Dispatcher.BeginInvoke(() => PbSpeakerLevel.Value = lvl * 100.0);
        _coordinator.AudioEngine.MicPeakLevelChanged += lvl =>
            Dispatcher.BeginInvoke(() => PbMicLevel.Value = lvl * 100.0);

        _uiRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiRefreshTimer.Tick += (_, _) => RefreshCountersAndStatus();

        Loaded += OnMainWindowLoaded;
        Closing += OnMainWindowClosing;
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
                var rtb = new RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
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
        TxtDriverStatus.Text = drvInstalled ? "Đã cài (WHQL Signed)" : "Chưa cài đặt";
        TxtDriverStatus.Foreground = drvInstalled
            ? System.Windows.Media.Brushes.LightGreen
            : System.Windows.Media.Brushes.Orange;
        BtnInstallDriver.Visibility = drvInstalled ? Visibility.Collapsed : Visibility.Visible;

        bool attached = _coordinator.UsbIp.IsDriverAttached;
        TxtAdapterStatus.Text = attached ? "ĐANG HOẠT ĐỘNG (BthUsb.sys)" : "Chưa gắn vào Windows";
        TxtAdapterStatus.Foreground = attached
            ? System.Windows.Media.Brushes.LightGreen
            : System.Windows.Media.Brushes.Gold;
        BtnToggleVirtualBt.Content = attached
            ? "Ngắt Card Bluetooth Ảo khỏi Windows"
            : "Bật Card Bluetooth Ảo trên Windows";

        var phone = _coordinator.LanServer.CurrentPhone;
        if (phone is not null)
        {
            string transport = phone.IsUsbLink ? "Cáp USB (~1ms)" : $"Wi-Fi/LAN ({phone.RttMs:0.0} ms)";
            TxtPhoneStatus.Text = $"Đã kết nối: {phone.DeviceName} • {transport}";
            TxtPhoneStatus.Foreground = System.Windows.Media.Brushes.LightGreen;

            double oneWayNetMs = phone.IsUsbLink ? 1.0 : Math.Max(1.0, phone.RttMs / 2.0);
            double totalAudioMs = oneWayNetMs + 30.0; // 30ms Jitter Buffer
            TxtAudioLatency.Text = $"~{totalAudioMs:0} ms (Mạng {oneWayNetMs:0.0}ms + Đệm 30ms)";
            TxtAudioLatency.Foreground = System.Windows.Media.Brushes.LightSkyBlue;
        }
        else
        {
            TxtPhoneStatus.Text = "Chưa kết nối — Mở App Android quét QR";
            TxtPhoneStatus.Foreground = System.Windows.Media.Brushes.Gold;
            TxtAudioLatency.Text = "Chờ kết nối...";
            TxtAudioLatency.Foreground = System.Windows.Media.Brushes.Gray;
        }

        if (_coordinator.AttachedHeadset is { Connected: true } hs)
        {
            TxtHeadsetName.Text = hs.Name;
            TxtDualMac.Text = $"Stereo: {_coordinator.SynthesizedStereoMac ?? "..."} | Mic: {_coordinator.SynthesizedMicMac ?? "N/A"}";
        }
        else
        {
            TxtHeadsetName.Text = "Loa / Tai nghe trên Điện thoại";
            TxtDualMac.Text = "Tự tách Dual-MAC khi ĐT nối AirPods/Loa BT";
        }
    }

    private void RefreshCountersAndStatus()
    {
        TxtHciCounters.Text = $"{_coordinator.UsbIp.TotalHciCommands} Cmd / {_coordinator.UsbIp.TotalHciEvents} Evt";
        if (!_coordinator.AudioEngine.IsStreamingSpeaker)
        {
            PbSpeakerLevel.Value = 0;
        }
        PbMicLevel.Value = Math.Max(0, PbMicLevel.Value * 0.6);
    }

    private void RefreshBleList()
    {
        LstBleDevices.Items.Clear();
        foreach (var item in _bleDevices.Values.OrderByDescending(d => d.Rssi).Take(30))
        {
            string label = string.IsNullOrWhiteSpace(item.Name) ? "(Thiết bị BLE ẩn tên)" : item.Name;
            LstBleDevices.Items.Add($"{item.Address} | {item.Rssi,4} dBm | {label}");
        }
    }

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
            await UsbipWin2Manager.InstallDriverViaWingetAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Lỗi gọi winget: {ex.Message}", "Blueipooth", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            BtnInstallDriver.IsEnabled = true;
            RefreshUiState();
        }
    }

    private void OnRefreshQrClick(object sender, RoutedEventArgs e) => RenderQrCode();

    private async void OnAudioModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiInitialized) return;
        string mode = RbGamingMode.IsChecked == true ? "GamingOpus" : "NativeA2dp";
        bool mic = ChkMicBridge.IsChecked == true;
        await _coordinator.SetAudioModeAsync(mode, mic);
    }

    private void OnNoiseFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiInitialized) return;
        _coordinator.AudioEngine.IsMicNoiseFilterEnabled = ChkMicNoiseFilter.IsChecked == true;
    }

    private void OnOpenDeviceManagerClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });

    private void OnOpenBtSettingsClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });

    private void OnMinimizeToTrayClick(object sender, RoutedEventArgs e) => Hide();

    private async void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            Hide();
            _trayIcon?.ShowBalloonTip(
                2000,
                "Blueipooth đang chạy ngầm",
                "Card Bluetooth ảo & Cầu nối âm thanh vẫn đang giữ kết nối. Nhấp đúp biểu tượng khay hệ thống để mở lại.",
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
