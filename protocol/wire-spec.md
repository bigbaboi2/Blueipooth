# Blueipooth Wire Protocol v1 Specification

## 1. Overview
Blueipooth uses a dual-plane architecture over LAN (Wi-Fi / Ethernet) or USB Cable (`RNDIS` / `ADB` tunnel):
- **Control Plane (`TCP Port 49550`, TLS 1.3 with Certificate Pinning)**: Reliable ordered framing for pairing, BLE RF proxy commands/events, headset attachment sync, and RTT latency heartbeats.
- **Media Plane (`UDP Port 49551`, AES-128-GCM)**: Ultra-low-latency real-time Opus audio frames (`5ms` or `10ms` @ `48kHz`) for PC Speaker -> Phone Bluetooth Headset (`StreamId = 0x01`) and Phone/Headset Mic -> PC (`StreamId = 0x02`).

## 2. QR Code Pairing URI
```text
blueipooth://pair?v=1&name=<UrlEncodedPcName>&ips=<CommaSeparatedIPs>&port=49550&uport=49551&fp=<CertSha256Hex>&key=<Base64UrlAes128SessionKey>&tok=<OneTimeHexToken>
```
- `fp`: SHA-256 fingerprint of the PC's self-signed ECDSA P-256 TLS certificate.
- `key`: 16-byte AES-128-GCM media session key (rotated upon successful TLS `PAIR_HELLO` handshake).
- `tok`: 16-byte one-time pairing token verified inside the encrypted TLS channel.

## 3. Control Plane Framing (TCP + TLS 1.3)
Every message on TCP Port `49550` uses a 5-byte header followed by a UTF-8 JSON (or binary-attached) payload:
```text
+-----------------------------------+---------------+------------------------+
| PayloadLength (4B, UInt32 BE)     | MsgType (1B)  | Payload (N bytes)      |
+-----------------------------------+---------------+------------------------+
```

### Message Types (`MsgType`)
| Code | Name | Direction | Description |
| :--- | :--- | :--- | :--- |
| `0x01` | `PAIR_HELLO` | Android -> PC | Sends device name, token, Android version, and UDP port |
| `0x02` | `PAIR_ACCEPT` | PC -> Android | Confirms pairing, rotates/confirms AES-128-GCM media key |
| `0x03` | `PING` | Both | Contains `clientTimestampUs` for RTT calculation |
| `0x04` | `PONG` | Both | Echoes `clientTimestampUs` + `remoteTimestampUs` |
| `0x10` | `BLE_SCAN_CMD` | PC -> Android | `{ "enable": true, "filterDuplicates": false }` |
| `0x11` | `BLE_ADV_REPORT` | Android -> PC | `{ "address": "...", "addrType": 1, "rssi": -58, "connectable": true, "dataHex": "..." }` |
| `0x12` | `BLE_ADV_CMD` | PC -> Android | `{ "enable": true, "advDataHex": "...", "scanRspHex": "..." }` |
| `0x13` | `BLE_GATT_REQ` | PC -> Android | Connect / Disconnect / Discover / Read / Write GATT characteristic |
| `0x14` | `BLE_GATT_RSP` | Android -> PC | GATT operation result or notification event |
| `0x20` | `HEADSET_STATE` | Android -> PC | `{ "connected": true, "name": "AirPods Pro", "mac": "...", "hasMic": true }` |
| `0x21` | `AUDIO_CONFIG` | PC -> Android | `{ "mode": "GamingOpus" \| "NativeA2dp", "sampleRate": 48000, "channels": 2, "frameMs": 5, "micEnabled": true }` |
| `0x22` | `MEDIA_CONTROL` | Android -> PC | `{ "key": "PLAY_PAUSE" \| "NEXT" \| "PREV" \| "VOL_UP" \| "VOL_DOWN" }` |

## 4. Media Plane Framing (UDP Port 49551)
```text
0                   1                   2                   3
0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|         Magic (0xB17E)        |   StreamId    |     Flags     |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                     SequenceNumber (UInt32 LE)                |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                                                               |
+                     TimestampUs (UInt64 LE)                   +
|                                                               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                  AES-128-GCM Tag (16 Bytes)                   |
|                              ...                              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|             Encrypted Opus Frame Payload (N Bytes)            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```
- **Nonce (12 bytes)** for AES-128-GCM is derived deterministically without extra wire overhead: `[StreamId (1B)][Flags (1B)][0x0000 (2B)][TimestampUs (8B)]` XORed with `[0..3]=SequenceNumber`.
- `StreamId`: `0x01` = PC Stereo Speaker Output (`48kHz`, 2ch Opus), `0x02` = Android Mic Input (`48kHz`, 1ch Opus).

## 5. Local IPC Protocol (`.NET Host` <-> `Bumble HCI Engine`)
- **Port `49552` (TCP Localhost)**: Standard **HCI H4** byte stream (`0x01` Command, `0x02` ACL, `0x03` SCO, `0x04` Event) bridging `Blueipooth.UsbIp` and `bumble-engine`.
- **Port `49553` (TCP Localhost)**: Newline-delimited JSON (`NDJSON`) control/event bridge forwarding `BLE_*`, `HEADSET_STATE`, and `A2DP_PCM` events between `.NET Host` and `bumble-engine`.
