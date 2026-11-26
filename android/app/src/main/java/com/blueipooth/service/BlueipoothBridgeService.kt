package com.blueipooth.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothProfile
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.ServiceInfo
import android.graphics.Bitmap
import android.media.AudioManager
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.net.wifi.WifiManager
import android.os.BatteryManager
import android.os.Binder
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.PowerManager
import android.os.SystemClock
import android.view.KeyEvent
import java.io.ByteArrayOutputStream
import java.util.Base64
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
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong

data class ScannedBleDevice(
    val address: String,
    val name: String,
    val rssi: Int,
    val connectable: Boolean,
    val lastSeenMs: Long,
    val firstSeenSeq: Long = 0L
)

data class BridgeUiState(
    val isConnected: Boolean = false,
    val pcName: String = "",
    val activeEndpoint: String = "",
    val rttMs: Double = 0.0,
    val audioMode: String = "GamingOpus",
    val micEnabled: Boolean = false,
    val headset: ConnectedHeadsetInfo = ConnectedHeadsetInfo(false, "Loa điện thoại", "", true),
    val bleScanning: Boolean = false,
    val bleScanError: String? = null,
    val bleAdvertising: Boolean = false,
    val bleDevicesFound: Int = 0,
    val bleDevices: List<ScannedBleDevice> = emptyList(),
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
    private val discoveredBleMap = ConcurrentHashMap<String, ScannedBleDevice>()
    private val bleSeqCounter = AtomicLong(0L)
    private val mainHandler = Handler(Looper.getMainLooper())
    private val statusSyncRunnable = object : Runnable {
        override fun run() {
            if (_uiState.value.isConnected) {
                Thread { sendDeviceStatusToPc() }.start()
            }
            mainHandler.postDelayed(this, 1_500L)
        }
    }

    private val bleAutoStopRunnable = Runnable {
        if (bleProxy.isScanning) {
            bleProxy.setScanning(false)
        }
    }

    private var wifiLock: WifiManager.WifiLock? = null
    private var wakeLock: PowerManager.WakeLock? = null
    @Volatile
    private var a2dpProxy: BluetoothProfile? = null
    @Volatile
    private var hfpProxy: BluetoothProfile? = null
    @Volatile
    private var lastArtTrackKey: String = ""
    @Volatile
    private var cachedPhoneArtB64: String = ""

    private lateinit var bleProxy: BleRfProxyManager
    private lateinit var audioBridge: LowLatencyAudioBridge
    private lateinit var lanClient: SecureLanClient

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
        startForegroundCompat(buildNotification("Đang chờ kết nối với PC..."))
        acquireLowLatencyLocks()

        bleProxy = BleRfProxyManager(
            context = this,
            onAdvReportJson = { advJson ->
                if (_uiState.value.isConnected) {
                    lanClient.sendControlJson(ControlMessageType.BLE_ADV_REPORT, advJson)
                }
                _uiState.update { it.copy(bleDevicesFound = bleProxy.uniqueDevicesSeen) }
            },
            onScanStateChanged = { scanning, error ->
                mainHandler.removeCallbacks(bleAutoStopRunnable)
                if (scanning) {
                    discoveredBleMap.clear()
                    mainHandler.postDelayed(bleAutoStopRunnable, 15_000L)
                }
                _uiState.update {
                    it.copy(
                        bleScanning = scanning,
                        bleScanError = error,
                        bleDevicesFound = if (scanning) 0 else it.bleDevicesFound,
                        bleDevices = if (scanning) emptyList() else it.bleDevices
                    )
                }
            },
            onDeviceDiscovered = { addr, name, rssi, connectable ->
                val existing = discoveredBleMap[addr]
                val seq = existing?.firstSeenSeq ?: bleSeqCounter.incrementAndGet()
                val resolvedName = name.ifBlank { existing?.name ?: "" }
                discoveredBleMap[addr] = ScannedBleDevice(
                    address = addr,
                    name = resolvedName,
                    rssi = rssi,
                    connectable = connectable,
                    lastSeenMs = System.currentTimeMillis(),
                    firstSeenSeq = seq
                )
                val stableList = discoveredBleMap.values
                    .sortedBy { it.firstSeenSeq }
                    .take(40)
                _uiState.update {
                    it.copy(
                        bleDevices = stableList,
                        bleDevicesFound = discoveredBleMap.size
                    )
                }
            }
        )

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
                    sendDeviceStatusToPc()
                    updateNotification("Đã kết nối: $pcName ($ep)")
                } else {
                    bleProxy.stopAll()
                    updateNotification("Đang chờ kết nối tự động với PC...")
                }
            },
            onBleScanCommand = { enable ->
                bleProxy.setScanning(enable)
                _uiState.update {
                    it.copy(
                        bleScanning = bleProxy.isScanning,
                        bleScanError = bleProxy.scanError
                    )
                }
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
            },
            onMediaControlCommand = { key ->
                handleRemoteMediaCommand(key)
            }
        )

        audioBridge.start()
        lanClient.startDiscoveryAndAutoReconnect()
        initBluetoothProfileProxies()
        mainHandler.postDelayed(statusSyncRunnable, 4_000L)
    }

    @android.annotation.SuppressLint("MissingPermission")
    private fun initBluetoothProfileProxies() {
        try {
            val btMgr = getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager ?: return
            val adapter = btMgr.adapter ?: return
            val listener = object : BluetoothProfile.ServiceListener {
                override fun onServiceConnected(profile: Int, proxy: BluetoothProfile?) {
                    if (profile == BluetoothProfile.A2DP) a2dpProxy = proxy
                    if (profile == BluetoothProfile.HEADSET) hfpProxy = proxy
                    sendDeviceStatusToPc()
                }

                override fun onServiceDisconnected(profile: Int) {
                    if (profile == BluetoothProfile.A2DP) a2dpProxy = null
                    if (profile == BluetoothProfile.HEADSET) hfpProxy = null
                }
            }
            adapter.getProfileProxy(this, listener, BluetoothProfile.A2DP)
            adapter.getProfileProxy(this, listener, BluetoothProfile.HEADSET)
        } catch (_: Exception) {
        }
    }

    @android.annotation.SuppressLint("MissingPermission")
    private fun getConnectedBtDeviceNames(): List<String> {
        val names = linkedSetOf<String>()
        try {
            a2dpProxy?.connectedDevices?.forEach { dev ->
                dev.name?.takeIf { it.isNotBlank() }?.let { names.add(it) }
            }
            hfpProxy?.connectedDevices?.forEach { dev ->
                dev.name?.takeIf { it.isNotBlank() }?.let { names.add(it) }
            }
            if (names.isEmpty()) {
                val btMgr = getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager
                btMgr?.adapter?.bondedDevices?.forEach { dev ->
                    try {
                        val m = dev.javaClass.getMethod("isConnected")
                        if (m.invoke(dev) == true) {
                            dev.name?.takeIf { it.isNotBlank() }?.let { names.add(it) }
                        }
                    } catch (_: Exception) {
                    }
                }
            }
        } catch (_: Exception) {
        }
        return names.toList()
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

    fun toggleLocalBleScan() {
        bleProxy.setScanning(!bleProxy.isScanning)
    }

    fun clearBleDevices() {
        discoveredBleMap.clear()
        _uiState.update { it.copy(bleDevices = emptyList(), bleDevicesFound = 0) }
    }

    private fun sendDeviceStatusToPc() {
        if (!_uiState.value.isConnected) return
        val batIntent = registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        val level = batIntent?.getIntExtra(BatteryManager.EXTRA_LEVEL, -1) ?: -1
        val scale = batIntent?.getIntExtra(BatteryManager.EXTRA_SCALE, 100) ?: 100
        val pct = if (level >= 0 && scale > 0) (level * 100) / scale else -1
        val status = batIntent?.getIntExtra(BatteryManager.EXTRA_STATUS, -1) ?: -1
        val charging = status == BatteryManager.BATTERY_STATUS_CHARGING || status == BatteryManager.BATTERY_STATUS_FULL

        val btMgr = getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager
        val btOn = btMgr?.adapter?.isEnabled == true
        val connectedBtNames = getConnectedBtDeviceNames()

        val am = getSystemService(Context.AUDIO_SERVICE) as? AudioManager
        val activeBtName = connectedBtNames.firstOrNull()
        val mediaInfo = when {
            am?.isMusicActive == true && activeBtName != null ->
                "Đang phát qua $activeBtName"
            am?.isMusicActive == true && audioBridge.currentHeadset.connected ->
                "Đang phát qua ${audioBridge.currentHeadset.name}"
            am?.isMusicActive == true -> "Đang phát âm thanh trên điện thoại"
            else -> "Không có media đang phát"
        }

        val phoneMedia = queryActivePhoneMedia()
        val maxVol = (am?.getStreamMaxVolume(AudioManager.STREAM_MUSIC) ?: 15).coerceAtLeast(1)
        val curVol = (am?.getStreamVolume(AudioManager.STREAM_MUSIC) ?: 10).coerceIn(0, maxVol)
        val phoneVolPct = Math.round((curVol * 100.0f) / maxVol).coerceIn(0, 100)

        val jsonObj = JSONObject()
            .put("batteryPercent", pct)
            .put("isCharging", charging)
            .put("bluetoothEnabled", btOn)
            .put("connectedBtDevices", connectedBtNames.joinToString(", "))
            .put("mediaInfo", mediaInfo)
            .put("audioJitterMs", audioBridge.measuredJitterQueueMs)
            .put("audioHardwareMs", audioBridge.measuredHardwarePipelineMs)
            .put("audioActivelyPlaying", audioBridge.isAudioActivelyPlaying)
            .put("phoneVolumePercent", phoneVolPct)

        if (phoneMedia != null) {
            jsonObj
                .put("phoneMediaApp", phoneMedia.appName)
                .put("phoneMediaTitle", phoneMedia.title)
                .put("phoneMediaArtist", phoneMedia.artist)
                .put("phoneMediaPlaying", phoneMedia.playing)
                .put("phoneMediaPosMs", phoneMedia.positionMs)
                .put("phoneMediaDurMs", phoneMedia.durationMs)
            if (phoneMedia.artB64.isNotEmpty()) {
                jsonObj.put("phoneMediaArtB64", phoneMedia.artB64)
            }
        }

        lanClient.sendControlJson(ControlMessageType.DEVICE_STATUS, jsonObj.toString())
    }

    private data class PhoneMediaSnapshot(
        val appName: String,
        val title: String,
        val artist: String,
        val playing: Boolean,
        val positionMs: Long,
        val durationMs: Long,
        val artB64: String
    )

    private fun findBestPhoneMediaController(): MediaController? {
        return try {
            val msm = getSystemService(Context.MEDIA_SESSION_SERVICE) as? MediaSessionManager ?: return null
            val comp = ComponentName(this, BlueipoothMediaListener::class.java)
            val controllers = msm.getActiveSessions(comp)
                .filter { it.packageName != "app.nectarlink" && it.packageName != packageName }
            if (controllers.isEmpty()) return null
            controllers.firstOrNull { it.playbackState?.state == PlaybackState.STATE_PLAYING }
                ?: controllers.firstOrNull { !it.metadata?.getString(MediaMetadata.METADATA_KEY_TITLE).isNullOrBlank() }
                ?: controllers.firstOrNull()
        } catch (_: Exception) {
            null
        }
    }

    private fun formatPackageName(pkg: String): String {
        val p = pkg.lowercase()
        return when {
            p.contains("youtube") && p.contains("music") -> "YT Music"
            p.contains("youtube") -> "YouTube"
            p.contains("spotify") -> "Spotify"
            p.contains("trill") || p.contains("tiktok") || p.contains("musically") -> "TikTok"
            p.contains("zing") -> "Zing MP3"
            p.contains("NCT") || p.contains("nhaccuatui") -> "NhacCuaTui"
            p.contains("soundcloud") -> "SoundCloud"
            p.contains("chrome") || p.contains("browser") -> "Browser"
            p.contains("facebook") -> "Facebook"
            else -> pkg.substringAfterLast('.').replaceFirstChar { it.uppercase() }
        }
    }

    private fun queryActivePhoneMedia(): PhoneMediaSnapshot? {
        val ctrl = findBestPhoneMediaController() ?: return null
        val meta = ctrl.metadata
        val pb = ctrl.playbackState
        val title = meta?.getString(MediaMetadata.METADATA_KEY_TITLE)?.trim().orEmpty()
        if (title.isEmpty()) return null

        val artist = (meta?.getString(MediaMetadata.METADATA_KEY_ARTIST)
            ?: meta?.getString(MediaMetadata.METADATA_KEY_AUTHOR)
            ?: meta?.getString(MediaMetadata.METADATA_KEY_ALBUM)
            ?: "").trim()
        val durMs = (meta?.getLong(MediaMetadata.METADATA_KEY_DURATION) ?: 0L).coerceAtLeast(0L)
        val rawPos = (pb?.position ?: 0L).coerceAtLeast(0L)
        val playing = pb?.state == PlaybackState.STATE_PLAYING
        val posMs = if (playing && pb != null && pb.lastPositionUpdateTime > 0L) {
            val delta = (SystemClock.elapsedRealtime() - pb.lastPositionUpdateTime).coerceAtLeast(0L)
            (rawPos + (delta * pb.playbackSpeed).toLong()).coerceAtMost(if (durMs > 0) durMs else Long.MAX_VALUE)
        } else {
            rawPos
        }

        val trackKey = "${ctrl.packageName}|$title|$artist"
        if (trackKey != lastArtTrackKey) {
            lastArtTrackKey = trackKey
            cachedPhoneArtB64 = try {
                val bmp = meta?.getBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART)
                    ?: meta?.getBitmap(MediaMetadata.METADATA_KEY_ART)
                    ?: meta?.getBitmap(MediaMetadata.METADATA_KEY_DISPLAY_ICON)
                if (bmp != null) {
                    val scaled = Bitmap.createScaledBitmap(bmp, 96, 96, true)
                    val baos = ByteArrayOutputStream()
                    scaled.compress(Bitmap.CompressFormat.JPEG, 80, baos)
                    Base64.getEncoder().encodeToString(baos.toByteArray())
                } else ""
            } catch (_: Exception) {
                ""
            }
        }

        return PhoneMediaSnapshot(
            appName = formatPackageName(ctrl.packageName),
            title = title,
            artist = artist.ifEmpty { formatPackageName(ctrl.packageName) },
            playing = playing,
            positionMs = posMs,
            durationMs = durMs,
            artB64 = cachedPhoneArtB64
        )
    }

    private fun handleRemoteMediaCommand(key: String) {
        val ctrl = findBestPhoneMediaController()
        val tc = ctrl?.transportControls
        val pbState = ctrl?.playbackState?.state
        val am = getSystemService(Context.AUDIO_SERVICE) as? AudioManager
        val upper = key.trim().uppercase()

        try {
            when {
                upper == "PLAY_PAUSE" -> {
                    if (tc != null) {
                        if (pbState == PlaybackState.STATE_PLAYING) tc.pause() else tc.play()
                    } else {
                        dispatchKey(am, KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE)
                    }
                }
                upper == "NEXT" -> if (tc != null) tc.skipToNext() else dispatchKey(am, KeyEvent.KEYCODE_MEDIA_NEXT)
                upper == "PREV" -> if (tc != null) tc.skipToPrevious() else dispatchKey(am, KeyEvent.KEYCODE_MEDIA_PREVIOUS)
                upper.startsWith("SEEK_REL:") -> {
                    val deltaMs = upper.substringAfter("SEEK_REL:").toLongOrNull() ?: 0L
                    val snap = queryActivePhoneMedia()
                    if (tc != null && snap != null) {
                        val target = (snap.positionMs + deltaMs).coerceAtLeast(0L)
                        tc.seekTo(if (snap.durationMs > 0) target.coerceAtMost(snap.durationMs) else target)
                    }
                }
                upper.startsWith("SEEK_POS:") -> {
                    val targetMs = upper.substringAfter("SEEK_POS:").toLongOrNull() ?: 0L
                    tc?.seekTo(targetMs.coerceAtLeast(0L))
                }
                upper.startsWith("SET_VOL:") -> {
                    val pct = upper.substringAfter("SET_VOL:").toIntOrNull()?.coerceIn(0, 100) ?: return
                    if (am != null) {
                        val maxV = am.getStreamMaxVolume(AudioManager.STREAM_MUSIC).coerceAtLeast(1)
                        val newV = Math.round((pct * maxV) / 100.0f).coerceIn(0, maxV)
                        am.setStreamVolume(AudioManager.STREAM_MUSIC, newV, 0)
                    }
                }
            }
        } catch (_: Exception) {
        }
        mainHandler.postDelayed({ Thread { sendDeviceStatusToPc() }.start() }, 180L)
    }

    private fun dispatchKey(am: AudioManager?, keyCode: Int) {
        am ?: return
        val now = SystemClock.uptimeMillis()
        am.dispatchMediaKeyEvent(KeyEvent(now, now, KeyEvent.ACTION_DOWN, keyCode, 0))
        am.dispatchMediaKeyEvent(KeyEvent(now, now, KeyEvent.ACTION_UP, keyCode, 0))
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
        mainHandler.removeCallbacks(statusSyncRunnable)
        mainHandler.removeCallbacks(bleAutoStopRunnable)
        try {
            val btMgr = getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager
            a2dpProxy?.let { btMgr?.adapter?.closeProfileProxy(BluetoothProfile.A2DP, it) }
            hfpProxy?.let { btMgr?.adapter?.closeProfileProxy(BluetoothProfile.HEADSET, it) }
        } catch (_: Exception) {
        }
        bleProxy.stopAll()
        audioBridge.stop()
        lanClient.stop()
        try { wifiLock?.release() } catch (_: Exception) {}
        try { wakeLock?.release() } catch (_: Exception) {}
        super.onDestroy()
    }
}
