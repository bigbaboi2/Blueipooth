package com.blueipooth.ble

import android.annotation.SuppressLint
import android.bluetooth.BluetoothManager
import android.bluetooth.le.AdvertiseCallback
import android.bluetooth.le.AdvertiseData
import android.bluetooth.le.AdvertiseSettings
import android.bluetooth.le.ScanCallback
import android.bluetooth.le.ScanResult
import android.bluetooth.le.ScanSettings
import android.content.Context
import android.os.ParcelUuid
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

/**
 * Non-root 2.4GHz BLE RF Proxy using Android's hardware Bluetooth chip on behalf of Windows PC:
 * - Executes real BLE Scans (BluetoothLeScanner) and streams raw AD payloads to Windows HCI.
 * - Broadcasts real BLE Advertisements (BluetoothLeAdvertiser) requested by Windows HCI.
 */
@SuppressLint("MissingPermission")
class BleRfProxyManager(
    context: Context,
    private val onAdvReportJson: (String) -> Unit
) {
    private val btManager = context.getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager
    private val adapter get() = btManager?.adapter
    private val lastReportTimeMs = ConcurrentHashMap<String, Long>()

    @Volatile
    var isScanning: Boolean = false
        private set

    @Volatile
    var isAdvertising: Boolean = false
        private set

    @Volatile
    var totalReportsForwarded: Long = 0
        private set

    private val scanCallback = object : ScanCallback() {
        override fun onScanResult(callbackType: Int, result: ScanResult) {
            handleScanResult(result)
        }

        override fun onBatchScanResults(results: MutableList<ScanResult>) {
            for (r in results) handleScanResult(r)
        }
    }

    private val advertiseCallback = object : AdvertiseCallback() {
        override fun onStartSuccess(settingsInEffect: AdvertiseSettings?) {
            isAdvertising = true
        }

        override fun onStartFailure(errorCode: Int) {
            isAdvertising = false
        }
    }

    fun setScanning(enable: Boolean) {
        val scanner = adapter?.bluetoothLeScanner ?: return
        try {
            if (enable && !isScanning) {
                val settings = ScanSettings.Builder()
                    .setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY)
                    .setReportDelay(0L)
                    .build()
                scanner.startScan(null, settings, scanCallback)
                isScanning = true
            } else if (!enable && isScanning) {
                scanner.stopScan(scanCallback)
                isScanning = false
            }
        } catch (_: SecurityException) {
            isScanning = false
        } catch (_: Exception) {
            isScanning = false
        }
    }

    fun setAdvertising(enable: Boolean, advDataHex: String) {
        val advertiser = adapter?.bluetoothLeAdvertiser ?: return
        try {
            advertiser.stopAdvertising(advertiseCallback)
            isAdvertising = false
            if (!enable) return

            val settings = AdvertiseSettings.Builder()
                .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_LATENCY)
                .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_HIGH)
                .setConnectable(true)
                .build()

            val advData = parseRawAdStructures(hexToBytes(advDataHex))
            advertiser.startAdvertising(settings, advData, advertiseCallback)
        } catch (_: Exception) {
            isAdvertising = false
        }
    }

    fun stopAll() {
        setScanning(false)
        setAdvertising(false, "")
    }

    private fun handleScanResult(result: ScanResult) {
        val addr = result.device?.address ?: return
        val now = System.currentTimeMillis()
        val prev = lastReportTimeMs[addr] ?: 0L
        // Throttle identical MAC reports to ~8 Hz max to keep TLS channel crisp
        if (now - prev < 120L) return
        lastReportTimeMs[addr] = now

        val rawBytes = result.scanRecord?.bytes ?: ByteArray(0)
        // Trim trailing zero padding from 62-byte Android ScanRecord buffer
        var actualLen = rawBytes.size
        while (actualLen > 0 && rawBytes[actualLen - 1].toInt() == 0) {
            actualLen--
        }
        val trimmedHex = bytesToHex(rawBytes, actualLen)
        val devName = (result.scanRecord?.deviceName ?: result.device?.name ?: "")
            .replace("\"", "")
            .replace("\\", "")
        val connectable = result.isConnectable

        totalReportsForwarded++
        val json = """{"address":"$addr","name":"$devName","addrType":1,"rssi":${result.rssi},"connectable":$connectable,"dataHex":"$trimmedHex"}"""
        onAdvReportJson(json)
    }

    private fun parseRawAdStructures(raw: ByteArray): AdvertiseData {
        val builder = AdvertiseData.Builder().setIncludeDeviceName(false)
        var i = 0
        while (i < raw.size) {
            val len = raw[i].toInt() and 0xFF
            if (len == 0 || i + len >= raw.size) break
            val adType = raw[i + 1].toInt() and 0xFF
            val body = raw.copyOfRange(i + 2, i + 1 + len)
            when (adType) {
                0x02, 0x03 -> { // 16-bit Service UUIDs
                    var offset = 0
                    while (offset + 2 <= body.size) {
                        val uuid16 = ByteBuffer.wrap(body, offset, 2).order(ByteOrder.LITTLE_ENDIAN).short.toInt() and 0xFFFF
                        val fullUuid = UUID.fromString(String.format("0000%04x-0000-1000-8000-00805f9b34fb", uuid16))
                        builder.addServiceUuid(ParcelUuid(fullUuid))
                        offset += 2
                    }
                }
                0xFF -> { // Manufacturer Specific Data
                    if (body.size >= 2) {
                        val companyId = ByteBuffer.wrap(body, 0, 2).order(ByteOrder.LITTLE_ENDIAN).short.toInt() and 0xFFFF
                        builder.addManufacturerData(companyId, body.copyOfRange(2, body.size))
                    }
                }
            }
            i += len + 1
        }
        return builder.build()
    }

    companion object {
        fun hexToBytes(hex: String): ByteArray {
            val clean = hex.trim()
            if (clean.length < 2) return ByteArray(0)
            val out = ByteArray(clean.length / 2)
            for (i in out.indices) {
                out[i] = clean.substring(i * 2, i * 2 + 2).toInt(16).toByte()
            }
            return out
        }

        fun bytesToHex(bytes: ByteArray, length: Int = bytes.size): String {
            val sb = StringBuilder(length * 2)
            for (i in 0 until length) {
                sb.append(String.format("%02x", bytes[i].toInt() and 0xFF))
            }
            return sb.toString()
        }
    }
}
