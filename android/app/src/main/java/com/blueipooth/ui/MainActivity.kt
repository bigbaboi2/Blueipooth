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
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
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
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationBarItemDefaults
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
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
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import com.blueipooth.service.BlueipoothBridgeService
import com.blueipooth.service.BridgeUiState
import com.blueipooth.service.ScannedBleDevice

// Unified iOS 26 Frosted Milky Glass & Midnight Slate Palette (matches PC App)
private val BgMidnight = Color(0xFF0E1118)
private val BarFrosted = Color(0xFF141924)
private val CardFrosted = Color(0xCC1B2130)
private val SubCardFrosted = Color(0x9E252D40)
private val RimGlass = Color(0x24FFFFFF)
private val BtnGlass = Color(0x1FFFFFFF)
private val AccentCyan = Color(0xFF7DD3FC)
private val AccentCyberPurple = Color(0xFFC4B5FD)
private val AccentPeriwinkle = Color(0xFFA8C7FA)
private val AccentSage = Color(0xFF86EFAC)
private val AccentAmber = Color(0xFFFDE68A)
private val TextPrimary = Color(0xFFEEF2FA)
private val TextMuted = Color(0xFF98A3B8)
private val DarkInk = Color(0xFF0F172A)

private enum class AndroidTab(val index: Int, val label: String) {
    BLUETOOTH(0, "Bluetooth"),
    QR_CONNECT(1, "Kết nối PC"),
    AUDIO(2, "Âm thanh")
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
        window.statusBarColor = 0xFF141924.toInt()
        window.navigationBarColor = 0xFF141924.toInt()

        val svcIntent = Intent(this, BlueipoothBridgeService::class.java)
        ContextCompat.startForegroundService(this, svcIntent)
        bindService(svcIntent, connection, Context.BIND_AUTO_CREATE)

        setContent {
            val scheme = darkColorScheme(
                primary = AccentPeriwinkle,
                secondary = AccentSage,
                background = BgMidnight,
                surface = CardFrosted
            )
            MaterialTheme(colorScheme = scheme) {
                Surface(modifier = Modifier.fillMaxSize(), color = BgMidnight) {
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
                            Text("Đang khởi động Blueipooth...", color = TextPrimary, fontSize = 15.sp)
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
private fun FrostedCard(
    modifier: Modifier = Modifier,
    contentPadding: PaddingValues = PaddingValues(18.dp),
    content: @Composable ColumnScope.() -> Unit
) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(20.dp))
            .background(CardFrosted)
            .border(1.dp, RimGlass, RoundedCornerShape(20.dp))
            .padding(contentPadding),
        content = content
    )
}

@Composable
private fun BrandAppIconBadge(modifier: Modifier = Modifier) {
    Box(
        modifier = modifier
            .size(36.dp)
            .clip(RoundedCornerShape(10.dp))
            .background(Color(0xFF161D2C))
            .border(1.2.dp, Color(0x457DD3FC), RoundedCornerShape(10.dp)),
        contentAlignment = Alignment.Center
    ) {
        Canvas(modifier = Modifier.size(22.dp)) {
            val w = size.width
            val h = size.height
            val stroke = Stroke(width = 1.9.dp.toPx(), cap = StrokeCap.Round, join = StrokeJoin.Round)

            // Bluetooth Rune (#7DD3FC)
            val btPath = Path().apply {
                moveTo(w * 0.16f, h * 0.32f)
                lineTo(w * 0.48f, h * 0.66f)
                lineTo(w * 0.32f, h * 0.84f)
                lineTo(w * 0.32f, h * 0.16f)
                lineTo(w * 0.48f, h * 0.34f)
                lineTo(w * 0.16f, h * 0.68f)
            }
            drawPath(btPath, color = AccentCyan, style = stroke)

            // Musical Note (#A8C7FA)
            val notePath = Path().apply {
                moveTo(w * 0.76f, h * 0.70f)
                lineTo(w * 0.76f, h * 0.24f)
                lineTo(w * 0.92f, h * 0.34f)
            }
            drawPath(notePath, color = AccentPeriwinkle, style = stroke)
            drawOval(
                color = AccentPeriwinkle,
                topLeft = Offset(w * 0.56f, h * 0.60f),
                size = Size(w * 0.24f, h * 0.20f)
            )
        }
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
    var currentTab by remember { mutableStateOf(AndroidTab.QR_CONNECT) }

    Scaffold(
        containerColor = BgMidnight,
        topBar = {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .background(BarFrosted)
                    .padding(horizontal = 16.dp, vertical = 13.dp),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    BrandAppIconBadge()
                    Column {
                        Row(
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(6.dp)
                        ) {
                            Text(
                                text = buildAnnotatedString {
                                    withStyle(SpanStyle(color = AccentCyan)) { append("BLUE") }
                                    withStyle(SpanStyle(color = AccentCyberPurple)) { append("IP") }
                                    withStyle(SpanStyle(color = AccentPeriwinkle)) { append("OOTH") }
                                },
                                fontSize = 18.sp,
                                fontWeight = FontWeight.ExtraBold,
                                fontFamily = FontFamily.Monospace,
                                letterSpacing = 0.8.sp
                            )
                            Box(
                                modifier = Modifier
                                    .clip(RoundedCornerShape(5.dp))
                                    .background(Color(0x1F7DD3FC))
                                    .border(1.dp, Color(0x407DD3FC), RoundedCornerShape(5.dp))
                                    .padding(horizontal = 5.dp, vertical = 1.dp)
                            ) {
                                Text(
                                    text = "5.2",
                                    color = AccentCyan,
                                    fontSize = 9.sp,
                                    fontFamily = FontFamily.Monospace,
                                    fontWeight = FontWeight.Bold
                                )
                            }
                        }
                        Text(
                            text = "Cầu nối Bluetooth & Âm thanh",
                            color = TextMuted,
                            fontSize = 11.sp
                        )
                    }
                }
                ConnectionStatusPill(state = state)
            }
        },
        bottomBar = {
            NavigationBar(
                containerColor = BarFrosted,
                contentColor = TextPrimary,
                tonalElevation = 0.dp
            ) {
                AndroidTab.entries.forEach { tab ->
                    val selected = currentTab == tab
                    NavigationBarItem(
                        selected = selected,
                        onClick = { currentTab = tab },
                        icon = {
                            TabVectorIcon(tab = tab, tint = if (selected) AccentPeriwinkle else TextMuted)
                        },
                        label = {
                            Text(
                                text = tab.label,
                                fontSize = 12.sp,
                                fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Medium
                            )
                        },
                        colors = NavigationBarItemDefaults.colors(
                            selectedIconColor = AccentPeriwinkle,
                            selectedTextColor = AccentPeriwinkle,
                            unselectedIconColor = TextMuted,
                            unselectedTextColor = TextMuted,
                            indicatorColor = Color(0x2E7DD3FC)
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
                (slideInHorizontally(animationSpec = tween(210)) { it / 4 * dir } +
                    fadeIn(animationSpec = tween(210)))
                    .togetherWith(
                        slideOutHorizontally(animationSpec = tween(170)) { -it / 4 * dir } +
                            fadeOut(animationSpec = tween(150))
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
private fun TabVectorIcon(tab: AndroidTab, tint: Color) {
    Canvas(modifier = Modifier.size(20.dp)) {
        val w = size.width
        val h = size.height
        val stroke = Stroke(width = 1.7.dp.toPx(), cap = StrokeCap.Round, join = StrokeJoin.Round)
        when (tab) {
            AndroidTab.BLUETOOTH -> {
                val p = Path().apply {
                    moveTo(w * 0.28f, h * 0.30f)
                    lineTo(w * 0.72f, h * 0.70f)
                    lineTo(w * 0.50f, h * 0.90f)
                    lineTo(w * 0.50f, h * 0.10f)
                    lineTo(w * 0.72f, h * 0.30f)
                    lineTo(w * 0.28f, h * 0.70f)
                }
                drawPath(p, color = tint, style = stroke)
            }
            AndroidTab.QR_CONNECT -> {
                drawRoundRect(color = tint, topLeft = Offset(w * 0.14f, h * 0.14f), size = Size(w * 0.30f, h * 0.30f), style = stroke)
                drawRoundRect(color = tint, topLeft = Offset(w * 0.56f, h * 0.14f), size = Size(w * 0.30f, h * 0.30f), style = stroke)
                drawRoundRect(color = tint, topLeft = Offset(w * 0.14f, h * 0.56f), size = Size(w * 0.30f, h * 0.30f), style = stroke)
                drawCircle(color = tint, radius = w * 0.12f, center = Offset(w * 0.71f, h * 0.71f))
            }
            AndroidTab.AUDIO -> {
                val p = Path().apply {
                    moveTo(w * 0.65f, h * 0.72f)
                    lineTo(w * 0.65f, h * 0.18f)
                    lineTo(w * 0.86f, h * 0.30f)
                }
                drawPath(p, color = tint, style = stroke)
                drawOval(color = tint, topLeft = Offset(w * 0.34f, h * 0.58f), size = Size(w * 0.32f, h * 0.26f))
            }
        }
    }
}

@Composable
private fun ConnectionStatusPill(state: BridgeUiState) {
    val dotColor = if (state.isConnected) AccentSage else AccentAmber
    val label = if (state.isConnected) state.pcName.ifBlank { "Đã nối PC" } else "Chờ kết nối"
    Row(
        modifier = Modifier
            .clip(RoundedCornerShape(20.dp))
            .background(SubCardFrosted)
            .border(1.dp, RimGlass, RoundedCornerShape(20.dp))
            .padding(horizontal = 11.dp, vertical = 5.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp)
    ) {
        Box(
            modifier = Modifier
                .size(7.5.dp)
                .clip(CircleShape)
                .background(dotColor)
        )
        Text(
            text = label,
            color = TextPrimary,
            fontSize = 11.5.sp,
            fontWeight = FontWeight.SemiBold,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
    }
}

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
        FrostedCard {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text("Thiết bị & đường truyền", fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = TextMuted)
                Text("Kết nối với máy tính", fontSize = 18.sp, fontWeight = FontWeight.SemiBold, color = TextPrimary)

                if (state.isConnected) {
                    val linkLabel = if (state.activeEndpoint == "127.0.0.1") {
                        "Cáp USB (~1ms)"
                    } else {
                        "Wi-Fi/LAN (${String.format("%.1f", state.rttMs)} ms)"
                    }
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(15.dp))
                            .background(SubCardFrosted)
                            .border(1.dp, RimGlass, RoundedCornerShape(15.dp))
                            .padding(14.dp)
                    ) {
                        Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            Text("Đã kết nối với ${state.pcName}", color = AccentSage, fontWeight = FontWeight.SemiBold, fontSize = 15.sp)
                            Text("Địa chỉ: ${state.activeEndpoint} • $linkLabel", color = TextMuted, fontSize = 12.5.sp)
                        }
                    }

                    Button(
                        onClick = onDisconnect,
                        colors = ButtonDefaults.buttonColors(containerColor = BtnGlass, contentColor = TextPrimary),
                        modifier = Modifier
                            .fillMaxWidth()
                            .border(1.dp, RimGlass, RoundedCornerShape(13.dp)),
                        shape = RoundedCornerShape(13.dp)
                    ) {
                        Text("Tạm ngừng kết nối", fontWeight = FontWeight.SemiBold)
                    }
                } else {
                    Text(
                        text = "Quét mã QR hiển thị trên màn hình PC hoặc cắm cáp USB để tự động ghép nối bảo mật.",
                        color = TextMuted,
                        fontSize = 13.sp
                    )

                    if (state.discoveredHosts.isNotEmpty()) {
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clip(RoundedCornerShape(13.dp))
                                .background(SubCardFrosted)
                                .border(1.dp, RimGlass, RoundedCornerShape(13.dp))
                                .padding(11.dp)
                        ) {
                            Text(
                                text = "PC phát hiện: " + state.discoveredHosts.joinToString { "${it.pcName} (${it.ips.firstOrNull() ?: ""})" },
                                color = AccentCyan,
                                fontSize = 12.sp
                            )
                        }
                    }

                    if (scanningQr) {
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(260.dp)
                                .clip(RoundedCornerShape(16.dp))
                                .background(Color.Black)
                        ) {
                            QrScannerView { detectedUri ->
                                scanningQr = false
                                onPairUri(detectedUri)
                            }
                        }
                        Button(
                            onClick = { scanningQr = false },
                            colors = ButtonDefaults.buttonColors(containerColor = BtnGlass, contentColor = TextPrimary),
                            modifier = Modifier.fillMaxWidth(),
                            shape = RoundedCornerShape(13.dp)
                        ) {
                            Text("Đóng Camera")
                        }
                    } else {
                        Button(
                            onClick = { scanningQr = true },
                            colors = ButtonDefaults.buttonColors(containerColor = AccentPeriwinkle, contentColor = DarkInk),
                            shape = RoundedCornerShape(14.dp),
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(48.dp)
                        ) {
                            Text("Quét mã QR trên PC", color = DarkInk, fontWeight = FontWeight.SemiBold, fontSize = 14.5.sp)
                        }

                        Button(
                            onClick = onReconnect,
                            colors = ButtonDefaults.buttonColors(containerColor = BtnGlass, contentColor = TextPrimary),
                            modifier = Modifier
                                .fillMaxWidth()
                                .border(1.dp, RimGlass, RoundedCornerShape(13.dp)),
                            shape = RoundedCornerShape(13.dp)
                        ) {
                            Text("Kết nối lại PC đã lưu", fontWeight = FontWeight.SemiBold)
                        }
                    }

                    Text(
                        text = if (showManualUri) "Ẩn nhập URI thủ công" else "Nhập URI ghép đôi thủ công",
                        color = AccentPeriwinkle,
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
                                colors = ButtonDefaults.buttonColors(containerColor = AccentPeriwinkle, contentColor = DarkInk),
                                modifier = Modifier.fillMaxWidth(),
                                shape = RoundedCornerShape(12.dp)
                            ) {
                                Text("Kết nối", fontWeight = FontWeight.SemiBold)
                            }
                        }
                    }
                }

                state.lastError?.let { err ->
                    Text("Lưu ý: $err", color = Color(0xFFFCA5A5), fontSize = 12.5.sp)
                }
            }
        }
    }
}

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
        FrostedCard(contentPadding = PaddingValues(16.dp)) {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text("Quét Bluetooth BLE", fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = TextPrimary)
                        Text(
                            text = when {
                                state.bleScanning -> "Đang quét sóng 2.4GHz (${state.bleDevices.size} thiết bị)"
                                state.bleScanError != null -> "Lỗi quét BLE"
                                else -> "Sẵn sàng quét thiết bị xung quanh"
                            },
                            color = if (state.bleScanning) AccentPeriwinkle else TextMuted,
                            fontSize = 12.5.sp
                        )
                    }
                    Text(
                        text = if (state.bleAdvertising) "Đang phát BLE" else "Chờ phát",
                        color = if (state.bleAdvertising) AccentSage else TextMuted,
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
                        color = AccentPeriwinkle,
                        trackColor = SubCardFrosted
                    )
                }

                state.bleScanError?.let { error ->
                    Text(error, color = Color(0xFFFCA5A5), fontSize = 12.sp)
                }

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    Button(
                        onClick = onToggleBleScan,
                        colors = ButtonDefaults.buttonColors(
                            containerColor = if (state.bleScanning) Color(0xFFE11D48) else AccentPeriwinkle,
                            contentColor = if (state.bleScanning) Color.White else DarkInk
                        ),
                        shape = RoundedCornerShape(12.dp),
                        modifier = Modifier.weight(1f)
                    ) {
                        Text(
                            text = if (state.bleScanning) "Dừng quét" else "Quét Bluetooth",
                            fontWeight = FontWeight.SemiBold
                        )
                    }
                    Button(
                        onClick = onClearBleDevices,
                        colors = ButtonDefaults.buttonColors(containerColor = BtnGlass, contentColor = TextPrimary),
                        modifier = Modifier.border(1.dp, RimGlass, RoundedCornerShape(12.dp)),
                        shape = RoundedCornerShape(12.dp)
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
                color = TextMuted,
                fontSize = 12.5.sp,
                fontWeight = FontWeight.SemiBold
            )
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                modifier = Modifier.clickable { hideUnnamed = !hideUnnamed }
            ) {
                Text(
                    text = "Ẩn không tên",
                    color = TextMuted,
                    fontSize = 12.sp
                )
                Switch(
                    checked = hideUnnamed,
                    onCheckedChange = { hideUnnamed = it },
                    colors = SwitchDefaults.colors(
                        checkedThumbColor = DarkInk,
                        checkedTrackColor = AccentPeriwinkle
                    )
                )
            }
        }

        if (filteredDevices.isEmpty()) {
            FrostedCard(modifier = Modifier.weight(1f)) {
                Box(modifier = Modifier.fillMaxSize().padding(18.dp), contentAlignment = Alignment.Center) {
                    Text(
                        text = if (state.bleScanning) {
                            "Đang tìm kiếm thiết bị Bluetooth gần bạn (tự dừng sau 15 giây)..."
                        } else {
                            "Chưa có thiết bị nào. Nhấn Quét Bluetooth hoặc yêu cầu quét từ PC."
                        },
                        color = TextMuted,
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
        dev.rssi >= -65 -> AccentSage
        dev.rssi >= -80 -> AccentAmber
        else -> Color(0xFFFCA5A5)
    }

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(15.dp))
            .background(CardFrosted)
            .border(1.dp, RimGlass, RoundedCornerShape(15.dp))
            .padding(horizontal = 14.dp, vertical = 12.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = dev.name.ifBlank { "(Thiết bị BLE ẩn tên)" },
                color = TextPrimary,
                fontWeight = FontWeight.SemiBold,
                fontSize = 14.sp,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
            Spacer(modifier = Modifier.height(2.dp))
            Text(
                text = "${dev.address} • ${if (dev.connectable) "Kết nối được" else "Quảng bá"}",
                color = TextMuted,
                fontFamily = FontFamily.Monospace,
                fontSize = 11.5.sp
            )
        }
        Spacer(modifier = Modifier.width(8.dp))
        Box(
            modifier = Modifier
                .clip(RoundedCornerShape(9.dp))
                .background(SubCardFrosted)
                .border(1.dp, RimGlass, RoundedCornerShape(9.dp))
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
        FrostedCard {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("Đầu ra âm thanh", fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = TextMuted)
                Text(
                    text = if (state.headset.connected) state.headset.name else "Loa điện thoại",
                    color = AccentSage,
                    fontWeight = FontWeight.SemiBold,
                    fontSize = 18.sp
                )
                Text(
                    text = "Chế độ: ${if (state.audioMode == "GamingOpus") "Low-Latency Opus 48kHz Stereo (FastTrack F1)" else "Windows Native A2DP"}",
                    color = TextMuted,
                    fontSize = 12.5.sp
                )
                if (state.isConnected) {
                    val netOneWay = if (state.activeEndpoint == "127.0.0.1") 0.8 else (state.rttMs / 2.0).coerceAtLeast(1.0)
                    val totalAudioMs = netOneWay + 26.0
                    Box(
                        modifier = Modifier
                            .padding(top = 4.dp)
                            .clip(RoundedCornerShape(10.dp))
                            .background(SubCardFrosted)
                            .border(1.dp, RimGlass, RoundedCornerShape(10.dp))
                            .padding(horizontal = 12.dp, vertical = 7.dp)
                    ) {
                        Text(
                            text = "Độ trễ thực tế: ~${totalAudioMs.toInt()} ms (Mạng ${String.format("%.1f", netOneWay)}ms + FastTrack 16ms + Opus 10ms)",
                            color = AccentPeriwinkle,
                            fontSize = 12.sp,
                            fontWeight = FontWeight.SemiBold
                        )
                    }
                }
            }
        }

        // Microphone Card
        FrostedCard {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text("Microphone về PC", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = TextPrimary)
                    Spacer(modifier = Modifier.height(3.dp))
                    Text(
                        text = if (state.micEnabled) "Đang bật — truyền giọng nói 48kHz về PC" else "Đang tắt tiếng (Muted)",
                        color = if (state.micEnabled) AccentSage else TextMuted,
                        fontSize = 12.5.sp
                    )
                }
                Switch(
                    checked = state.micEnabled,
                    onCheckedChange = onToggleMic,
                    colors = SwitchDefaults.colors(
                        checkedThumbColor = DarkInk,
                        checkedTrackColor = AccentPeriwinkle
                    )
                )
            }
        }

        // Sleek Vector Media Controls Card
        FrostedCard {
            Column(verticalArrangement = Arrangement.spacedBy(14.dp)) {
                Text("Điều khiển nhạc trên PC", fontSize = 15.5.sp, fontWeight = FontWeight.SemiBold, color = TextPrimary)
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.Center,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    // Thin Vector Previous Button
                    Box(
                        modifier = Modifier
                            .size(44.dp)
                            .clip(CircleShape)
                            .background(BtnGlass)
                            .border(1.dp, RimGlass, CircleShape)
                            .clickable { onMediaKey("PREV") },
                        contentAlignment = Alignment.Center
                    ) {
                        Canvas(modifier = Modifier.size(18.dp)) {
                            val w = size.width; val h = size.height
                            val s = Stroke(width = 1.6.dp.toPx(), cap = StrokeCap.Round, join = StrokeJoin.Round)
                            drawLine(TextPrimary, Offset(w * 0.22f, h * 0.22f), Offset(w * 0.22f, h * 0.78f), strokeWidth = s.width, cap = StrokeCap.Round)
                            val p = Path().apply {
                                moveTo(w * 0.78f, h * 0.22f)
                                lineTo(w * 0.34f, h * 0.50f)
                                lineTo(w * 0.78f, h * 0.78f)
                                close()
                            }
                            drawPath(p, TextPrimary, style = s)
                        }
                    }

                    Spacer(modifier = Modifier.width(18.dp))

                    // Center Circular Play/Pause Button
                    Box(
                        modifier = Modifier
                            .size(54.dp)
                            .clip(CircleShape)
                            .background(AccentPeriwinkle)
                            .clickable { onMediaKey("PLAY_PAUSE") },
                        contentAlignment = Alignment.Center
                    ) {
                        Canvas(modifier = Modifier.size(22.dp)) {
                            val w = size.width; val h = size.height
                            val s = Stroke(width = 1.8.dp.toPx(), cap = StrokeCap.Round, join = StrokeJoin.Round)
                            val p = Path().apply {
                                moveTo(w * 0.30f, h * 0.22f)
                                lineTo(w * 0.78f, h * 0.50f)
                                lineTo(w * 0.30f, h * 0.78f)
                                close()
                            }
                            drawPath(p, DarkInk, style = s)
                        }
                    }

                    Spacer(modifier = Modifier.width(18.dp))

                    // Thin Vector Next Button
                    Box(
                        modifier = Modifier
                            .size(44.dp)
                            .clip(CircleShape)
                            .background(BtnGlass)
                            .border(1.dp, RimGlass, CircleShape)
                            .clickable { onMediaKey("NEXT") },
                        contentAlignment = Alignment.Center
                    ) {
                        Canvas(modifier = Modifier.size(18.dp)) {
                            val w = size.width; val h = size.height
                            val s = Stroke(width = 1.6.dp.toPx(), cap = StrokeCap.Round, join = StrokeJoin.Round)
                            val p = Path().apply {
                                moveTo(w * 0.22f, h * 0.22f)
                                lineTo(w * 0.66f, h * 0.50f)
                                lineTo(w * 0.22f, h * 0.78f)
                                close()
                            }
                            drawPath(p, TextPrimary, style = s)
                            drawLine(TextPrimary, Offset(w * 0.78f, h * 0.22f), Offset(w * 0.78f, h * 0.78f), strokeWidth = s.width, cap = StrokeCap.Round)
                        }
                    }
                }
            }
        }
    }
}
