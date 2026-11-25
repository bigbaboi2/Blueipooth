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
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
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
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import com.blueipooth.service.BlueipoothBridgeService
import com.blueipooth.service.BridgeUiState

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
                        DashboardScreen(
                            state = state,
                            onPairUri = { svc.pairWithQrUri(it) },
                            onToggleMic = { enabled -> svc.setMicMuted(!enabled) },
                            onMediaKey = { svc.sendMediaKey(it) },
                            onDisconnect = { svc.disconnectFromPc() },
                            onReconnect = { svc.reconnectToPc() }
                        )
                    } else {
                        Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                            Text("Đang khởi động dịch vụ cầu nối Blueipooth...", color = Color.White)
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
private fun DashboardScreen(
    state: BridgeUiState,
    onPairUri: (String) -> Unit,
    onToggleMic: (Boolean) -> Unit,
    onMediaKey: (String) -> Unit,
    onDisconnect: () -> Unit,
    onReconnect: () -> Unit
) {
    var scanningQr by remember { mutableStateOf(false) }
    var manualUri by remember { mutableStateOf("") }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp)
    ) {
        Text(
            text = "BLUEIPOOTH BRIDGE",
            fontSize = 22.sp,
            fontWeight = FontWeight.ExtraBold,
            color = Color(0xFF38BDF8)
        )
        Text(
            text = "Trạm thu phát sóng BLE 2.4GHz & Cầu nối Tai nghe Bluetooth siêu trễ thấp cho PC",
            fontSize = 13.sp,
            color = Color(0xFF94A3B8)
        )

        // Card 1: Connection & QR Scanner
        Card(
            colors = CardDefaults.cardColors(containerColor = Color(0xFF162033)),
            shape = RoundedCornerShape(12.dp),
            modifier = Modifier.fillMaxWidth()
        ) {
            Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("1. Kết nối PC (LAN / Wi-Fi / Cáp USB)", fontWeight = FontWeight.Bold, color = Color.White)

                if (state.isConnected) {
                    val linkLabel = if (state.activeEndpoint == "127.0.0.1") {
                        "Cáp USB (~1ms)"
                    } else {
                        "Wi-Fi/LAN (${String.format("%.1f", state.rttMs)} ms)"
                    }
                    Text(
                        text = "ĐÃ KẾT NỐI: ${state.pcName} • $linkLabel",
                        color = Color(0xFF4ADE80),
                        fontWeight = FontWeight.Bold
                    )
                    OutlinedButton(onClick = onDisconnect, modifier = Modifier.fillMaxWidth()) {
                        Text("Ngắt kết nối tạm thời")
                    }
                } else {
                    Text(
                        text = "Chưa kết nối — Quét mã QR trên màn hình PC lần đầu (tự nối ngầm ở các lần sau).",
                        color = Color(0xFFFBBF24),
                        fontSize = 13.sp
                    )

                    if (state.discoveredHosts.isNotEmpty()) {
                        Text(
                            text = "Phát hiện PC trong mạng: " + state.discoveredHosts.joinToString { "${it.pcName} (${it.ips.firstOrNull() ?: ""})" },
                            color = Color(0xFF7DD3FC),
                            fontSize = 12.sp
                        )
                    }

                    if (scanningQr) {
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(240.dp)
                                .clip(RoundedCornerShape(10.dp))
                                .background(Color.Black)
                        ) {
                            QrScannerView { detectedUri ->
                                scanningQr = false
                                onPairUri(detectedUri)
                            }
                        }
                        OutlinedButton(onClick = { scanningQr = false }, modifier = Modifier.fillMaxWidth()) {
                            Text("Đóng Camera")
                        }
                    } else {
                        Button(
                            onClick = { scanningQr = true },
                            colors = ButtonDefaults.buttonColors(containerColor = Color(0xFF0284C7)),
                            modifier = Modifier.fillMaxWidth()
                        ) {
                            Text("Quét Mã QR Trên Màn Hình PC", color = Color.White, fontWeight = FontWeight.Bold)
                        }
                        OutlinedButton(onClick = onReconnect, modifier = Modifier.fillMaxWidth()) {
                            Text("Kết nối lại với PC đã lưu")
                        }
                    }

                    OutlinedTextField(
                        value = manualUri,
                        onValueChange = { manualUri = it },
                        label = { Text("Hoặc dán URI blueipooth://pair?...") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth()
                    )
                    if (manualUri.isNotBlank()) {
                        Button(onClick = { onPairUri(manualUri) }, modifier = Modifier.fillMaxWidth()) {
                            Text("Kết nối bằng URI")
                        }
                    }
                }

                state.lastError?.let { err ->
                    Text("Lưu ý: $err", color = Color(0xFFF87171), fontSize = 12.sp)
                }
            }
        }

        // Card 2: Bluetooth Headset & Low-Latency Audio Bridge
        Card(
            colors = CardDefaults.cardColors(containerColor = Color(0xFF162033)),
            shape = RoundedCornerShape(12.dp),
            modifier = Modifier.fillMaxWidth()
        ) {
            Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("2. Cầu Nối Tai Nghe / Loa / Mic Bluetooth", fontWeight = FontWeight.Bold, color = Color.White)
                Text(
                    text = if (state.headset.connected) {
                        "Đang bắc cầu ra: ${state.headset.name}"
                    } else {
                        "Đầu ra hiện tại: Loa điện thoại (Hãy đeo AirPods / Loa BT để tự chuyển)"
                    },
                    color = Color(0xFF4ADE80),
                    fontWeight = FontWeight.SemiBold
                )
                Text(
                    text = "Chế độ: ${if (state.audioMode == "GamingOpus") "Gaming Opus 5ms (48kHz Stereo)" else "Windows Native A2DP"}",
                    color = Color(0xFF94A3B8),
                    fontSize = 12.sp
                )
                if (state.isConnected) {
                    val netOneWay = if (state.activeEndpoint == "127.0.0.1") 1.0 else (state.rttMs / 2.0).coerceAtLeast(1.0)
                    val totalAudioMs = netOneWay + 30.0
                    Text(
                        text = "Độ trễ âm thanh: ~${totalAudioMs.toInt()} ms (Mạng ${String.format("%.1f", netOneWay)}ms + Đệm 30ms)",
                        color = Color(0xFF38BDF8),
                        fontSize = 12.sp,
                        fontWeight = FontWeight.SemiBold
                    )
                }

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text("Truyền Mic điện thoại/tai nghe về PC", color = Color.White, fontSize = 14.sp)
                    Switch(checked = state.micEnabled, onCheckedChange = onToggleMic)
                }

                Spacer(modifier = Modifier.height(4.dp))
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    OutlinedButton(onClick = { onMediaKey("PREV") }, modifier = Modifier.weight(1f)) { Text("⏮") }
                    OutlinedButton(onClick = { onMediaKey("PLAY_PAUSE") }, modifier = Modifier.weight(1f)) { Text("⏯") }
                    OutlinedButton(onClick = { onMediaKey("NEXT") }, modifier = Modifier.weight(1f)) { Text("⏭") }
                }
            }
        }

        // Card 3: 2.4GHz BLE RF Proxy Status
        Card(
            colors = CardDefaults.cardColors(containerColor = Color(0xFF162033)),
            shape = RoundedCornerShape(12.dp),
            modifier = Modifier.fillMaxWidth()
        ) {
            Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Text("3. Trạm Thu Phát Sóng BLE 2.4GHz Thật", fontWeight = FontWeight.Bold, color = Color.White)
                Text(
                    text = "Quét BLE hộ Windows: ${if (state.bleScanning) "ĐANG QUÉT (LOW_LATENCY)" else "Nghỉ"}",
                    color = if (state.bleScanning) Color(0xFF38BDF8) else Color(0xFF94A3B8)
                )
                Text(
                    text = "Phát sóng BLE hộ Windows: ${if (state.bleAdvertising) "ĐANG PHÁT SÓNG" else "Nghỉ"}",
                    color = if (state.bleAdvertising) Color(0xFF4ADE80) else Color(0xFF94A3B8)
                )
                Text(
                    text = "Tổng gói sóng BLE thật đã chuyển tiếp về PC: ${state.blePacketsCount}",
                    color = Color(0xFF94A3B8),
                    fontSize = 12.sp
                )
            }
        }
    }
}
