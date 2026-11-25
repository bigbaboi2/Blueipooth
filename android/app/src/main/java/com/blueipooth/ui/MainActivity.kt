package com.blueipooth.ui

import android.Manifest
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.os.Build
import android.os.Bundle
import android.os.IBinder
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationBarItemDefaults
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import com.blueipooth.service.BlueipoothBridgeService
import com.blueipooth.service.BridgeUiState
import com.blueipooth.service.ScannedBleDevice

private enum class AndroidTab(val index: Int, val label: String, val iconGlyph: String) {
    BLUETOOTH(0, "Bluetooth", "📡"),
    QR_CONNECT(1, "Kết nối QR", "📷"),
    AUDIO(2, "Âm thanh", "🎧")
}

class MainActivity : ComponentActivity() {

    private var bridgeService by mutableStateOf<BlueipoothBridgeService?>(null)
    private var bound = false

    private val connection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName?, service: IBinder?) {
            val binder = service as? BlueipoothBridgeService.LocalBinder
            bridgeService = binder?.service
            bound = true
        }

        override fun onServiceDisconnected(name: ComponentName?) {
            bridgeService = null
            bound = false
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val svcIntent = Intent(this, BlueipoothBridgeService::class.java)
        ContextCompat.startForegroundService(this, svcIntent)
        bindService(svcIntent, connection, Context.BIND_AUTO_CREATE)

        setContent {
            val scheme = darkColorScheme(
                primary = Color(0xFF38BDF8),
                secondary = Color(0xFF4ADE80),
                background = Color(0xFF0B1120),
                surface = Color(0xFF162033)
            )
            MaterialTheme(colorScheme = scheme) {
                Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
                    PermissionRequester()
                    val svc = bridgeService
                    if (svc != null) {
                        val state by svc.uiState.collectAsState()
                        MainAppShell(
                            state = state,
                            onPairUri = { svc.pairWithQrUri(it) },
                            onToggleMic = { enabled -> svc.setMicMuted(!enabled) },
                            onMediaKey = { svc.sendMediaKey(it) },
                            onDisconnect = { svc.disconnectFromPc() },
                            onReconnect = { svc.reconnectToPc() },
                            onToggleBleScan = { svc.toggleLocalBleScan() },
                            onClearBleDevices = { svc.clearBleDevices() }
                        )
                    } else {
                        Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                            Text("Đang khởi động Blueipooth...", color = Color.White, fontSize = 15.sp)
                        }
                    }
                }
            }
        }
    }

    override fun onDestroy() {
        if (bound) {
            unbindService(connection)
            bound = false
        }
        super.onDestroy()
    }
}

@Composable
private fun PermissionRequester() {
    val launcher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { }

    LaunchedEffect(Unit) {
        val perms = buildList {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                add(Manifest.permission.BLUETOOTH_SCAN)
                add(Manifest.permission.BLUETOOTH_ADVERTISE)
                add(Manifest.permission.BLUETOOTH_CONNECT)
            } else {
                add(Manifest.permission.ACCESS_FINE_LOCATION)
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                add(Manifest.permission.POST_NOTIFICATIONS)
            }
            add(Manifest.permission.RECORD_AUDIO)
            add(Manifest.permission.CAMERA)
        }
        launcher.launch(perms.toTypedArray())
    }
}

@Composable
private fun MainAppShell(
    state: BridgeUiState,
    onPairUri: (String) -> Unit,
    onToggleMic: (Boolean) -> Unit,
    onMediaKey: (String) -> Unit,
    onDisconnect: () -> Unit,
    onReconnect: () -> Unit,
    onToggleBleScan: () -> Unit,
    onClearBleDevices: () -> Unit
) {
    // Default tab when opening is QR_CONNECT (center tab)
    var currentTab by remember { mutableStateOf(AndroidTab.QR_CONNECT) }

    Scaffold(
        containerColor = Color(0xFF0B1120),
        topBar = {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .background(Color(0xFF11192B))
                    .padding(horizontal = 16.dp, vertical = 14.dp),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    text = "BLUEIPOOTH",
                    fontSize = 19.sp,
                    fontWeight = FontWeight.ExtraBold,
                    color = Color(0xFF38BDF8)
                )
                ConnectionStatusPill(state = state)
            }
        },
        bottomBar = {
            NavigationBar(
                containerColor = Color(0xFF11192B),
                contentColor = Color.White,
                tonalElevation = 0.dp
            ) {
                AndroidTab.entries.forEach { tab ->
                    val selected = currentTab == tab
                    NavigationBarItem(
                        selected = selected,
                        onClick = { currentTab = tab },
                        icon = {
                            Text(
                                text = tab.iconGlyph,
                                fontSize = if (selected) 21.sp else 18.sp
                            )
                        },
                        label = {
                            Text(
                                text = tab.label,
                                fontSize = 12.sp,
                                fontWeight = if (selected) FontWeight.Bold else FontWeight.Medium
                            )
                        },
                        colors = NavigationBarItemDefaults.colors(
                            selectedIconColor = Color(0xFF38BDF8),
                            selectedTextColor = Color(0xFF38BDF8),
                            unselectedIconColor = Color(0xFF94A3B8),
                            unselectedTextColor = Color(0xFF94A3B8),
                            indicatorColor = Color(0xFF1E3A5F)
                        )
                    )
                }
            }
        }
    ) { innerPadding ->
        AnimatedContent(
            targetState = currentTab,
            modifier = Modifier
                .fillMaxSize()
                .padding(innerPadding),
            transitionSpec = {
                val dir = if (targetState.index >= initialState.index) 1 else -1
                (slideInHorizontally(animationSpec = tween(220)) { it / 4 * dir } +
                    fadeIn(animationSpec = tween(220)))
                    .togetherWith(
                        slideOutHorizontally(animationSpec = tween(180)) { -it / 4 * dir } +
                            fadeOut(animationSpec = tween(160))
                    )
            },
            label = "TabTransition"
        ) { tab ->
            when (tab) {
                AndroidTab.BLUETOOTH -> BluetoothScanTab(
                    state = state,
                    onToggleBleScan = onToggleBleScan,
                    onClearBleDevices = onClearBleDevices
                )
                AndroidTab.QR_CONNECT -> QrConnectTab(
                    state = state,
                    onPairUri = onPairUri,
                    onDisconnect = onDisconnect,
                    onReconnect = onReconnect
                )
                AndroidTab.AUDIO -> AudioTab(
                    state = state,
                    onToggleMic = onToggleMic,
                    onMediaKey = onMediaKey
                )
            }
        }
    }
}

@Composable
private fun ConnectionStatusPill(state: BridgeUiState) {
    val dotColor = if (state.isConnected) Color(0xFF4ADE80) else Color(0xFFFBBF24)
    val label = if (state.isConnected) state.pcName.ifBlank { "Đã nối PC" } else "Chưa kết nối"
    Row(
        modifier = Modifier
            .clip(RoundedCornerShape(20.dp))
            .background(Color(0xFF162033))
            .border(1.dp, Color(0xFF263552), RoundedCornerShape(20.dp))
            .padding(horizontal = 10.dp, vertical = 5.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp)
    ) {
        Box(
            modifier = Modifier
                .size(8.dp)
                .clip(CircleShape)
                .background(dotColor)
        )
        Text(
            text = label,
            color = Color.White,
            fontSize = 12.sp,
            fontWeight = FontWeight.SemiBold,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
    }
}

// =====================================================================
// CENTER TAB (DEFAULT): KẾT NỐI QR
// =====================================================================
@Composable
private fun QrConnectTab(
    state: BridgeUiState,
    onPairUri: (String) -> Unit,
    onDisconnect: () -> Unit,
    onReconnect: () -> Unit
) {
    var scanningQr by remember { mutableStateOf(false) }
    var showManualUri by remember { mutableStateOf(false) }
    var manualUri by remember { mutableStateOf("") }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp)
    ) {
        Card(
            colors = CardDefaults.cardColors(containerColor = Color(0xFF162033)),
            shape = RoundedCornerShape(16.dp),
            modifier = Modifier.fillMaxWidth()
        ) {
            Column(
                modifier = Modifier.padding(18.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                Text("Kết nối với PC", fontSize = 17.sp, fontWeight = FontWeight.Bold, color = Color.White)

                if (state.isConnected) {
                    val linkLabel = if (state.activeEndpoint == "127.0.0.1") {
                        "Cáp USB (~1ms)"
                    } else {
                        "Wi-Fi/LAN (${String.format("%.1f", state.rttMs)} ms)"
                    }
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(12.dp))
                            .background(Color(0xFF0F172A))
                            .padding(14.dp)
                    ) {
                        Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            Text("Đang kết nối với ${state.pcName}", color = Color(0xFF4ADE80), fontWeight = FontWeight.Bold, fontSize = 15.sp)
                            Text("Địa chỉ: ${state.activeEndpoint} • $linkLabel", color = Color(0xFF94A3B8), fontSize = 13.sp)
                        }
                    }

                    OutlinedButton(
                        onClick = onDisconnect,
                        modifier = Modifier.fillMaxWidth(),
                        shape = RoundedCornerShape(10.dp)
                    ) {
                        Text("Ngắt kết nối tạm thời")
                    }
                } else {
                    Text(
                        text = "Quét mã QR hiển thị trên màn hình PC để ghép đôi nhanh và bảo mật.",
                        color = Color(0xFF94A3B8),
                        fontSize = 13.sp
                    )

                    if (state.discoveredHosts.isNotEmpty()) {
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clip(RoundedCornerShape(10.dp))
                                .background(Color(0xFF0F172A))
                                .padding(10.dp)
                        ) {
                            Text(
                                text = "PC trong mạng: " + state.discoveredHosts.joinToString { "${it.pcName} (${it.ips.firstOrNull() ?: ""})" },
                                color = Color(0xFF7DD3FC),
                                fontSize = 12.sp
                            )
                        }
                    }

                    if (scanningQr) {
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(260.dp)
                                .clip(RoundedCornerShape(12.dp))
                                .background(Color.Black)
                        ) {
                            QrScannerView { detectedUri ->
                                scanningQr = false
                                onPairUri(detectedUri)
                            }
                        }
                        OutlinedButton(
                            onClick = { scanningQr = false },
                            modifier = Modifier.fillMaxWidth(),
                            shape = RoundedCornerShape(10.dp)
                        ) {
                            Text("Đóng Camera")
                        }
                    } else {
                        Button(
                            onClick = { scanningQr = true },
                            colors = ButtonDefaults.buttonColors(containerColor = Color(0xFF0284C7)),
                            shape = RoundedCornerShape(12.dp),
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(50.dp)
                        ) {
                            Text("Quét Mã QR Trên PC", color = Color.White, fontWeight = FontWeight.Bold, fontSize = 15.sp)
                        }

                        OutlinedButton(
                            onClick = onReconnect,
                            modifier = Modifier.fillMaxWidth(),
                            shape = RoundedCornerShape(10.dp)
                        ) {
                            Text("Kết nối lại PC đã lưu")
                        }
                    }

                    Text(
                        text = if (showManualUri) "Ẩn nhập URI thủ công" else "Nhập URI ghép đôi thủ công",
                        color = Color(0xFF38BDF8),
                        fontSize = 12.5.sp,
                        fontWeight = FontWeight.SemiBold,
                        modifier = Modifier
                            .clickable { showManualUri = !showManualUri }
                            .padding(vertical = 4.dp)
                    )

                    if (showManualUri) {
                        OutlinedTextField(
                            value = manualUri,
                            onValueChange = { manualUri = it },
                            label = { Text("Dán URI ghép đôi") },
                            singleLine = true,
                            modifier = Modifier.fillMaxWidth()
                        )
                        if (manualUri.isNotBlank()) {
                            Button(
                                onClick = { onPairUri(manualUri) },
                                modifier = Modifier.fillMaxWidth(),
                                shape = RoundedCornerShape(10.dp)
                            ) {
                                Text("Kết nối")
                            }
                        }
                    }
                }

                state.lastError?.let { err ->
                    Text("Lưu ý: $err", color = Color(0xFFF87171), fontSize = 12.5.sp)
                }
            }
        }
    }
}

// =====================================================================
// LEFT TAB: BLUETOOTH & TIẾN TRÌNH QUÉT / DANH SÁCH KHẢ DỤNG
// =====================================================================
@Composable
private fun BluetoothScanTab(
    state: BridgeUiState,
    onToggleBleScan: () -> Unit,
    onClearBleDevices: () -> Unit
) {
    var hideUnnamed by remember { mutableStateOf(true) }
    val filteredDevices = remember(state.bleDevices, hideUnnamed) {
        if (hideUnnamed) {
            state.bleDevices.filter { it.name.isNotBlank() && it.connectable }
        } else {
            state.bleDevices
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        Card(
            colors = CardDefaults.cardColors(containerColor = Color(0xFF162033)),
            shape = RoundedCornerShape(16.dp),
            modifier = Modifier.fillMaxWidth()
        ) {
            Column(
                modifier = Modifier.padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text("Quét Bluetooth BLE", fontSize = 17.sp, fontWeight = FontWeight.Bold, color = Color.White)
                        Text(
                            text = when {
                                state.bleScanning -> "Đang quét sóng 2.4GHz (${state.bleDevices.size} thiết bị)"
                                state.bleScanError != null -> "Lỗi quét BLE"
                                else -> "Sẵn sàng quét thiết bị xung quanh"
                            },
                            color = if (state.bleScanning) Color(0xFF38BDF8) else Color(0xFF94A3B8),
                            fontSize = 12.5.sp
                        )
                    }
                    Text(
                        text = if (state.bleAdvertising) "Đang phát BLE" else "Chờ phát",
                        color = if (state.bleAdvertising) Color(0xFF4ADE80) else Color(0xFF64748B),
                        fontSize = 11.5.sp,
                        fontWeight = FontWeight.SemiBold
                    )
                }

                if (state.bleScanning) {
                    LinearProgressIndicator(
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(4.dp)
                            .clip(RoundedCornerShape(2.dp)),
                        color = Color(0xFF38BDF8),
                        trackColor = Color(0xFF0F172A)
                    )
                }

                state.bleScanError?.let { error ->
                    Text(error, color = Color(0xFFF87171), fontSize = 12.sp)
                }

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    Button(
                        onClick = onToggleBleScan,
                        colors = ButtonDefaults.buttonColors(
                            containerColor = if (state.bleScanning) Color(0xFFDC2626) else Color(0xFF0284C7)
                        ),
                        shape = RoundedCornerShape(10.dp),
                        modifier = Modifier.weight(1f)
                    ) {
                        Text(
                            text = if (state.bleScanning) "Dừng quét" else "Quét Bluetooth",
                            color = Color.White,
                            fontWeight = FontWeight.Bold
                        )
                    }
                    OutlinedButton(
                        onClick = onClearBleDevices,
                        shape = RoundedCornerShape(10.dp)
                    ) {
                        Text("Xóa")
                    }
                }
            }
        }

        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 4.dp),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                text = "Thiết bị khả dụng (${filteredDevices.size})",
                color = Color(0xFF94A3B8),
                fontSize = 12.5.sp,
                fontWeight = FontWeight.SemiBold
            )
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                modifier = Modifier.clickable { hideUnnamed = !hideUnnamed }
            ) {
                Text(
                    text = "Ẩn không tên / quảng bá",
                    color = Color(0xFF94A3B8),
                    fontSize = 12.sp
                )
                Switch(checked = hideUnnamed, onCheckedChange = { hideUnnamed = it })
            }
        }

        if (filteredDevices.isEmpty()) {
            Card(
                colors = CardDefaults.cardColors(containerColor = Color(0xFF162033)),
                shape = RoundedCornerShape(14.dp),
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f)
            ) {
                Box(modifier = Modifier.fillMaxSize().padding(24.dp), contentAlignment = Alignment.Center) {
                    Text(
                        text = if (state.bleScanning) {
                            "Đang tìm kiếm thiết bị Bluetooth gần bạn (tự dừng sau 15 giây)..."
                        } else {
                            "Chưa có thiết bị nào. Nhấn Quét Bluetooth hoặc yêu cầu quét từ PC."
                        },
                        color = Color(0xFF94A3B8),
                        fontSize = 13.5.sp
                    )
                }
            }
        } else {
            LazyColumn(
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f),
                contentPadding = PaddingValues(bottom = 8.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                items(filteredDevices, key = { it.address }) { dev ->
                    BleDeviceCard(dev)
                }
            }
        }
    }
}

@Composable
private fun BleDeviceCard(dev: ScannedBleDevice) {
    val rssiColor = when {
        dev.rssi >= -65 -> Color(0xFF4ADE80)
        dev.rssi >= -80 -> Color(0xFFFBBF24)
        else -> Color(0xFFF87171)
    }

    Card(
        colors = CardDefaults.cardColors(containerColor = Color(0xFF162033)),
        shape = RoundedCornerShape(12.dp),
        modifier = Modifier.fillMaxWidth()
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 14.dp, vertical = 12.dp),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = dev.name.ifBlank { "(Thiết bị BLE ẩn tên)" },
                    color = Color.White,
                    fontWeight = FontWeight.SemiBold,
                    fontSize = 14.sp,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                Spacer(modifier = Modifier.height(2.dp))
                Text(
                    text = "${dev.address} • ${if (dev.connectable) "Kết nối được" else "Quảng bá"}",
                    color = Color(0xFF94A3B8),
                    fontFamily = FontFamily.Monospace,
                    fontSize = 11.5.sp
                )
            }
            Spacer(modifier = Modifier.width(8.dp))
            Box(
                modifier = Modifier
                    .clip(RoundedCornerShape(8.dp))
                    .background(Color(0xFF0F172A))
                    .padding(horizontal = 10.dp, vertical = 5.dp)
            ) {
                Text(
                    text = "${dev.rssi} dBm",
                    color = rssiColor,
                    fontFamily = FontFamily.Monospace,
                    fontWeight = FontWeight.Bold,
                    fontSize = 12.sp
                )
            }
        }
    }
}

// =====================================================================
// RIGHT TAB: ÂM THANH, LOA, MIC
// =====================================================================
@Composable
private fun AudioTab(
    state: BridgeUiState,
    onToggleMic: (Boolean) -> Unit,
    onMediaKey: (String) -> Unit
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp)
    ) {
        // Speaker / Headset Output Card
        Card(
            colors = CardDefaults.cardColors(containerColor = Color(0xFF162033)),
            shape = RoundedCornerShape(16.dp),
            modifier = Modifier.fillMaxWidth()
        ) {
            Column(modifier = Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("Loa & Tai nghe", fontSize = 17.sp, fontWeight = FontWeight.Bold, color = Color.White)
                Text(
                    text = if (state.headset.connected) {
                        "Đang phát qua: ${state.headset.name}"
                    } else {
                        "Đầu ra: Loa điện thoại (đeo tai nghe BT để tự chuyển)"
                    },
                    color = Color(0xFF4ADE80),
                    fontWeight = FontWeight.SemiBold,
                    fontSize = 14.sp
                )
                Text(
                    text = "Chế độ: ${if (state.audioMode == "GamingOpus") "Gaming Opus 5ms (48kHz Stereo)" else "Windows Native A2DP"}",
                    color = Color(0xFF94A3B8),
                    fontSize = 12.5.sp
                )
                if (state.isConnected) {
                    val netOneWay = if (state.activeEndpoint == "127.0.0.1") 1.0 else (state.rttMs / 2.0).coerceAtLeast(1.0)
                    val totalAudioMs = netOneWay + 30.0
                    Text(
                        text = "Độ trễ ước tính: ~${totalAudioMs.toInt()} ms (Mạng ${String.format("%.1f", netOneWay)}ms + Đệm 30ms)",
                        color = Color(0xFF38BDF8),
                        fontSize = 12.5.sp,
                        fontWeight = FontWeight.SemiBold
                    )
                }
            }
        }

        // Microphone Card
        Card(
            colors = CardDefaults.cardColors(containerColor = Color(0xFF162033)),
            shape = RoundedCornerShape(16.dp),
            modifier = Modifier.fillMaxWidth()
        ) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(18.dp),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text("Microphone về PC", fontSize = 16.sp, fontWeight = FontWeight.Bold, color = Color.White)
                    Spacer(modifier = Modifier.height(3.dp))
                    Text(
                        text = if (state.micEnabled) "Đang bật — truyền giọng nói về PC" else "Đang tắt tiếng (Muted)",
                        color = if (state.micEnabled) Color(0xFF4ADE80) else Color(0xFF94A3B8),
                        fontSize = 12.5.sp
                    )
                }
                Switch(checked = state.micEnabled, onCheckedChange = onToggleMic)
            }
        }

        // Media Controls Card
        Card(
            colors = CardDefaults.cardColors(containerColor = Color(0xFF162033)),
            shape = RoundedCornerShape(16.dp),
            modifier = Modifier.fillMaxWidth()
        ) {
            Column(modifier = Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("Điều khiển phát nhạc PC", fontSize = 16.sp, fontWeight = FontWeight.Bold, color = Color.White)
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    OutlinedButton(
                        onClick = { onMediaKey("PREV") },
                        modifier = Modifier.weight(1f),
                        shape = RoundedCornerShape(10.dp)
                    ) { Text("⏮", fontSize = 18.sp) }
                    Button(
                        onClick = { onMediaKey("PLAY_PAUSE") },
                        colors = ButtonDefaults.buttonColors(containerColor = Color(0xFF0284C7)),
                        modifier = Modifier.weight(1.2f),
                        shape = RoundedCornerShape(10.dp)
                    ) { Text("⏯", fontSize = 18.sp, color = Color.White) }
                    OutlinedButton(
                        onClick = { onMediaKey("NEXT") },
                        modifier = Modifier.weight(1f),
                        shape = RoundedCornerShape(10.dp)
                    ) { Text("⏭", fontSize = 18.sp) }
                }
            }
        }
    }
}
