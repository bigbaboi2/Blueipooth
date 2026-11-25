# Blueipooth — Card Bluetooth 5.2 Ảo & Cầu Nối Âm Thanh / BLE Qua LAN & USB

**Blueipooth** giúp các máy bàn (Desktop PC) không có card Bluetooth vật lý sở hữu một **Card Bluetooth 5.2 thực thụ ngay trong Windows Device Manager** (sử dụng driver chữ ký số WHQL `usbip-win2` + `BthUsb.sys` gốc của Microsoft, **không cần tắt Secure Boot**), đồng thời mượn điện thoại Android (**không cần Root**) làm:

1. **Trạm thu phát sóng BLE 2.4GHz thật (`BLE RF Proxy`)**: Chuyển tiếp lệnh quét (`LE Scan`) và phát sóng (`LE Advertise`) từ Windows ra môi trường thật qua chip Bluetooth của điện thoại.
2. **Cầu nối Tai nghe / Loa / Mic Bluetooth Chế độ Kép (`Smart Dual-Mode Audio Bridge`)**:
   - **Gaming / Discord Siêu Trễ Thấp (`WASAPI 48kHz -> Opus 5ms UDP`)**: Đẩy thẳng âm thanh Stereo 48kHz qua Wi-Fi (`WIFI_MODE_FULL_LOW_LATENCY`) hoặc cáp sạc USB (`~1ms`) ra AirPods / Loa Bluetooth đang nối với điện thoại.
   - **Tuyệt chiêu `Dual-MAC` trên Windows Bluetooth**: Tách riêng địa chỉ MAC của Loa Stereo (`A2DP Sink`) và Mic (`HFP Hands-Free`) để Windows không bao giờ tự ép tụt chất lượng nhạc/game xuống Mono rè khi bật Mic.

---

## Khởi chạy nhanh

### 1. Cài đặt công cụ & Driver WHQL `usbip-win2` (Chạy 1 lần đầu)
```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\setup-dev.ps1
```

### 2. Biên dịch & Chạy toàn bộ Kiểm thử Tự động (PC `.exe` + Bumble + Android `.apk`)
```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-all.ps1
```

### 3. Sử dụng thực tế
1. Mở ứng dụng Windows: `pc\Blueipooth.App\bin\Release\net10.0-windows\Blueipooth.App.exe`
2. Bấm **"Bật Card Bluetooth Ảo trên Windows"** -> Kiểm tra `Device Manager` thấy xuất hiện mục **Bluetooth**.
3. Cài đặt file `android\app\build\outputs\apk\debug\app-debug.apk` lên điện thoại Android.
4. Mở App **Blueipooth** trên điện thoại -> Bấm **"Quét Mã QR Trên Màn Hình PC"** (những lần sau vào chung Wi-Fi hoặc cắm cáp USB máy sẽ tự động kết nối ngầm!).
