namespace Blueipooth.UsbIp;

/// <summary>
/// Standard USB-IF Wireless Controller / Bluetooth Programming Interface (0xE0 / 0x01 / 0x01)
/// USB 2.0 Descriptors compatible with Windows 10/11 built-in BthUsb.sys.
/// </summary>
public static class BluetoothUsbDescriptors
{
    public const ushort VendorId = 0x0A12;  // CSR Generic Bluetooth Radio (inbox bth.inf)
    public const ushort ProductId = 0x0001; // Bluetooth HCI Adapter
    public const ushort BcdDevice = 0x0889;
    public const string BusId = "1-1";
    public const string SysPath = "/sys/devices/pci0000:00/0000:00:01.2/usb1/1-1";

    // 18-byte USB Device Descriptor
    public static readonly byte[] DeviceDescriptor =
    [
        18,   // bLength
        0x01, // bDescriptorType = DEVICE
        0x00, 0x02, // bcdUSB = 2.00
        0xE0, // bDeviceClass = Wireless Controller
        0x01, // bDeviceSubClass = RF Controller
        0x01, // bDeviceProtocol = Bluetooth Programming Interface
        64,   // bMaxPacketSize0 = 64
        0x12, 0x0A, // idVendor = 0x0A12 (LE)
        0x01, 0x00, // idProduct = 0x0001 (LE)
        0x89, 0x08, // bcdDevice = 0x0889 (LE)
        1,    // iManufacturer
        2,    // iProduct
        3,    // iSerialNumber
        1     // bNumConfigurations
    ];

    // Full Configuration Descriptor:
    // - Config (9B)
    // - Interface 0 Alt 0: 3 Endpoints (EP 0x81 Intr IN, EP 0x02 Bulk OUT, EP 0x82 Bulk IN) (9 + 7*3 = 30B)
    // - Interface 1 Alt 0: Isochronous Voice Inactive (0-bandwidth) (9 + 7*2 = 23B)
    // - Interface 1 Alt 2: Isochronous Voice Active 16-bit/mSBC (17 bytes/ms) (9 + 7*2 = 23B)
    // Total = 85 bytes (0x0055)
    public static readonly byte[] ConfigurationDescriptor =
    [
        // Configuration Descriptor (9 bytes)
        9,    // bLength
        0x02, // bDescriptorType = CONFIGURATION
        85, 0x00, // wTotalLength = 85
        2,    // bNumInterfaces = 2
        1,    // bConfigurationValue = 1
        0,    // iConfiguration
        0xE0, // bmAttributes = Bus-powered + Self-powered + Remote Wakeup
        50,   // bMaxPower = 100mA

        // Interface 0, Alternate Setting 0 (HCI Event & ACL Data)
        9,    // bLength
        0x04, // bDescriptorType = INTERFACE
        0,    // bInterfaceNumber = 0
        0,    // bAlternateSetting = 0
        3,    // bNumEndpoints = 3
        0xE0, // bInterfaceClass = Wireless
        0x01, // bInterfaceSubClass = RF
        0x01, // bInterfaceProtocol = Bluetooth
        0,    // iInterface

        // Endpoint 0x81: Interrupt IN (HCI Events)
        7, 0x05, 0x81, 0x03, 64, 0x00, 0x01,
        // Endpoint 0x02: Bulk OUT (HCI ACL TX)
        7, 0x05, 0x02, 0x02, 64, 0x00, 0x00,
        // Endpoint 0x82: Bulk IN (HCI ACL RX)
        7, 0x05, 0x82, 0x02, 64, 0x00, 0x00,

        // Interface 1, Alternate Setting 0 (SCO Voice Idle - 0 MaxPacket)
        9, 0x04, 1, 0, 2, 0xE0, 0x01, 0x01, 0,
        7, 0x05, 0x03, 0x01, 0x00, 0x00, 0x01,
        7, 0x05, 0x83, 0x01, 0x00, 0x00, 0x01,

        // Interface 1, Alternate Setting 1 (SCO Voice Active - 17B MaxPacket)
        9, 0x04, 1, 1, 2, 0xE0, 0x01, 0x01, 0,
        7, 0x05, 0x03, 0x01, 17, 0x00, 0x01,
        7, 0x05, 0x83, 0x01, 17, 0x00, 0x01
    ];

    public static byte[]? GetStringDescriptor(byte index)
    {
        if (index == 0)
        {
            // English (US) 0x0409
            return [4, 0x03, 0x09, 0x04];
        }

        string? text = index switch
        {
            1 => "Blueipooth Open Systems",
            2 => "Blueipooth Virtual Bluetooth 5.2 Adapter",
            3 => "B17E00000001",
            _ => null
        };

        if (text is null) return null;

        byte[] utf16 = System.Text.Encoding.Unicode.GetBytes(text);
        byte[] desc = new byte[2 + utf16.Length];
        desc[0] = (byte)desc.Length;
        desc[1] = 0x03; // STRING
        Buffer.BlockCopy(utf16, 0, desc, 2, utf16.Length);
        return desc;
    }
}
