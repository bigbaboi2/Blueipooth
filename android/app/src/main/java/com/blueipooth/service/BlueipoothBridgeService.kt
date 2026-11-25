package com.blueipooth.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.Binder
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import com.blueipooth.audio.ConnectedHeadsetInfo
import com.blueipooth.audio.LowLatencyAudioBridge
import com.blueipooth.ble.BleRfProxyManager
import com.blueipooth.net.DiscoveredPcHost
import com.blueipooth.net.SecureLanClient
import com.blueipooth.protocol.ControlMessageType
import com.blueipooth.ui.MainActivity
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import org.json.JSONObject

data class BridgeUiState(
    val isConnected: Boolean = false,
    val pcName: String = "",
    val activeEndpoint: String = "",
    val rttMs: Double = 0.0,
    val audioMode: String = "GamingOpus",
    val micEnabled: Boolean = false,
    val headset: ConnectedHeadsetInfo = ConnectedHeadsetInfo(false, "Loa điện thoại", "", true),
    val bleScanning: Boolean = false,
    val bleAdvertising: Boolean = false,
    val blePacketsCount: Long = 0,
    val discoveredHosts: List<DiscoveredPcHost> = emptyList(),
    val lastError: String? = null
)

class BlueipoothBridgeService : Service() {

    companion object {
        const val CHANNEL_ID = "blueipooth_bridge_channel"
        const val NOTIF_ID = 4955
    }

    inner class LocalBinder : Binder() {
        val service: BlueipoothBridgeService get() = this@BlueipoothBridgeService
    }

    private val binder = LocalBinder()
    private val _uiState = MutableStateFlow(BridgeUiState())
    val uiState: StateFlow<BridgeUiState> = _uiState.asStateFlow()

    private var wifiLock: WifiManager.WifiLock? = null
    private var wakeLock: PowerManager.WakeLock? = null

    private lateinit var bleProxy: BleRfProxyManager
    private lateinit var audioBridge: LowLatencyAudioBridge
    private lateinit var lanClient: SecureLanClient

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
        startForegroundCompat(buildNotification("Đang chờ kết nối với PC..."))
        acquireLowLatencyLocks()

        bleProxy = BleRfProxyManager(this) { advJson ->
            lanClient.sendControlJson(ControlMessageType.BLE_ADV_REPORT, advJson)
            _uiState.update { it.copy(blePacketsCount = bleProxy.totalReportsForwarded) }
        }

        audioBridge = LowLatencyAudioBridge(
            context = this,
            onHeadsetChanged = { hs ->
                _uiState.update { it.copy(headset = hs) }
                sendHeadsetStateToPc(hs)
            },
            onMicOpusFrameReady = { buf, len ->
                lanClient.sendMicOpusFrame(buf, len)
            }
        )

        lanClient = SecureLanClient(
            context = this,
            onConnectionState = { connected, pcName, ep, rtt ->
                _uiState.update {
                    it.copy(
                        isConnected = connected,
                        pcName = pcName,
                        activeEndpoint = ep,
                        rttMs = rtt,
                        lastError = if (connected) null else it.lastError
                    )
                }
                if (connected) {
                    audioBridge.refreshBluetoothHeadset()
                    sendHeadsetStateToPc(audioBridge.currentHeadset)
                    updateNotification("Đã kết nối: $pcName ($ep)")
                } else {
                    bleProxy.stopAll()
                    updateNotification("Đang chờ kết nối tự động với PC...")
                }
            },
            onBleScanCommand = { enable ->
                bleProxy.setScanning(enable)
                _uiState.update { it.copy(bleScanning = bleProxy.isScanning) }
            },
            onBleAdvCommand = { enable, hex ->
                bleProxy.setAdvertising(enable, hex)
                _uiState.update { it.copy(bleAdvertising = bleProxy.isAdvertising) }
            },
            onAudioConfig = { mode, mic, jitterMs ->
                audioBridge.applyAudioConfig(mic, jitterMs)
                _uiState.update { it.copy(audioMode = mode, micEnabled = mic) }
            },
            onStereoOpusPacket = { seq, opus ->
                audioBridge.pushIncomingPcStereoPacket(seq, opus)
            },
            onDiscoveredHostsUpdated = { hosts ->
                _uiState.update { it.copy(discoveredHosts = hosts) }
            }
        )

        audioBridge.start()
        lanClient.startDiscoveryAndAutoReconnect()
    }

    fun pairWithQrUri(uri: String) {
        _uiState.update { it.copy(lastError = null) }
        lanClient.connectWithQr(uri) { err ->
            _uiState.update { it.copy(lastError = err) }
        }
    }

    fun setMicMuted(muted: Boolean) {
        val enabled = !muted
        audioBridge.applyAudioConfig(enabled)
        _uiState.update { it.copy(micEnabled = enabled) }
    }

    fun sendMediaKey(key: String) {
        val json = JSONObject().put("key", key).toString()
        lanClient.sendControlJson(ControlMessageType.MEDIA_CONTROL, json)
    }

    fun disconnectFromPc() {
        lanClient.disconnectSession(byUser = true)
    }

    fun reconnectToPc() {
        lanClient.resumeAutoReconnect()
    }

    private fun sendHeadsetStateToPc(hs: ConnectedHeadsetInfo) {
        val json = JSONObject()
            .put("connected", hs.connected)
            .put("name", hs.name)
            .put("mac", hs.mac)
            .put("hasMic", hs.hasMic)
            .toString()
        lanClient.sendControlJson(ControlMessageType.HEADSET_STATE, json)
    }

    private fun acquireLowLatencyLocks() {
        val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        val lockMode = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            WifiManager.WIFI_MODE_FULL_LOW_LATENCY
        } else {
            @Suppress("DEPRECATION")
            WifiManager.WIFI_MODE_FULL_HIGH_PERF
        }
        wifiLock = wm.createWifiLock(lockMode, "Blueipooth::LowLatencyWifiLock").apply {
            setReferenceCounted(false)
            acquire()
        }

        val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "Blueipooth::BridgeWakeLock").apply {
            setReferenceCounted(false)
            acquire()
        }
    }

    private fun startForegroundCompat(notification: Notification) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            var fgType = ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE or
                ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R &&
                checkSelfPermission(android.Manifest.permission.RECORD_AUDIO) == android.content.pm.PackageManager.PERMISSION_GRANTED
            ) {
                fgType = fgType or ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE
            }
            startForeground(NOTIF_ID, notification, fgType)
        } else {
            startForeground(NOTIF_ID, notification)
        }
    }

    private fun createNotificationChannel() {
        val nm = getSystemService(NotificationManager::class.java)
        val channel = NotificationChannel(
            CHANNEL_ID,
            "Blueipooth Bridge Service",
            NotificationManager.IMPORTANCE_LOW
        )
        nm.createNotificationChannel(channel)
    }

    private fun buildNotification(statusText: String): Notification {
        val pendingIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        return Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("Blueipooth Virtual Bluetooth Bridge")
            .setContentText(statusText)
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setContentIntent(pendingIntent)
            .setOngoing(true)
            .build()
    }

    private fun updateNotification(statusText: String) {
        val nm = getSystemService(NotificationManager::class.java)
        nm.notify(NOTIF_ID, buildNotification(statusText))
    }

    override fun onBind(intent: Intent?): IBinder = binder

    override fun onDestroy() {
        bleProxy.stopAll()
        audioBridge.stop()
        lanClient.stop()
        try { wifiLock?.release() } catch (_: Exception) {}
        try { wakeLock?.release() } catch (_: Exception) {}
        super.onDestroy()
    }
}
