package com.blueipooth.net

import android.content.Context
import android.os.Build
import com.blueipooth.protocol.ControlFrameCodec
import com.blueipooth.protocol.ControlMessageType
import com.blueipooth.protocol.MediaFrameCipher
import com.blueipooth.protocol.MediaPacketHeader
import com.blueipooth.protocol.QrPairingInfo
import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.util.Base64
import java.util.UUID
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicLong
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager
import org.json.JSONArray
import org.json.JSONObject

data class DiscoveredPcHost(
    val pcName: String,
    val ips: List<String>,
    val controlPort: Int,
    val mediaPort: Int,
    val certFpHex: String,
    val hasUsb: Boolean
)

/**
 * Handles LAN / USB Tethering / ADB Discovery, Certificate-Pinned TLS 1.3 Control Channel,
 * Auto-Reconnect via saved token, and Encrypted AES-128-GCM UDP Media transport.
 */
class SecureLanClient(
    context: Context,
    private val onConnectionState: (connected: Boolean, pcName: String, endpoint: String, rttMs: Double) -> Unit,
    private val onBleScanCommand: (Boolean) -> Unit,
    private val onBleAdvCommand: (Boolean, String) -> Unit,
    private val onAudioConfig: (mode: String, micEnabled: Boolean) -> Unit,
    private val onStereoOpusPacket: (seq: Long, opus: ByteArray) -> Unit,
    private val onDiscoveredHostsUpdated: (List<DiscoveredPcHost>) -> Unit
) {
    private val prefs = context.getSharedPreferences("blueipooth_pairing", Context.MODE_PRIVATE)
    private val peerId: String = prefs.getString("peer_id", null) ?: UUID.randomUUID().toString().also {
        prefs.edit().putString("peer_id", it).apply()
    }

    private val ioPool = Executors.newCachedThreadPool()

    @Volatile
    private var activeSslSocket: SSLSocket? = null

    @Volatile
    private var controlOut: DataOutputStream? = null

    @Volatile
    private var udpSocket: DatagramSocket? = null

    @Volatile
    private var remoteMediaAddress: InetSocketAddress? = null

    @Volatile
    private var mediaCipher: MediaFrameCipher? = null

    @Volatile
    private var running = false

    @Volatile
    private var userPausedReconnect = false

    private val micSeq = AtomicLong(1L)

    fun startDiscoveryAndAutoReconnect() {
        if (running) return
        running = true
        userPausedReconnect = false
        ioPool.execute { discoveryLoop() }
    }

    fun resumeAutoReconnect() {
        userPausedReconnect = false
    }

    fun stop() {
        running = false
        disconnectSession(byUser = true)
    }

    fun connectWithQr(qrUri: String, onError: (String) -> Unit) {
        userPausedReconnect = false
        ioPool.execute {
            try {
                val qr = QrPairingInfo.parse(qrUri)
                // Always prepend 127.0.0.1 in case USB ADB reverse tunnel is plugged in!
                val candidates = (listOf("127.0.0.1") + qr.candidateIps).distinct()
                connectInternal(
                    pcNameHint = qr.pcName,
                    candidateIps = candidates,
                    controlPort = qr.controlPort,
                    mediaPort = qr.mediaUdpPort,
                    expectedCertFpHex = qr.certSha256Hex,
                    token = qr.oneTimeToken,
                    initialKey16 = qr.sessionKeyAes128,
                    isReconnect = false
                )
            } catch (e: Exception) {
                onError(e.message ?: "QR connection failed")
            }
        }
    }

    fun sendControlJson(type: Byte, json: String) {
        val out = controlOut ?: return
        ioPool.execute {
            try {
                ControlFrameCodec.writeFrame(out, type, json)
            } catch (_: Exception) {
            }
        }
    }

    fun sendMicOpusFrame(opusBuf: ByteArray, length: Int) {
        val sock = udpSocket ?: return
        val dest = remoteMediaAddress ?: return
        val cipher = mediaCipher ?: return

        val seq = micSeq.getAndIncrement()
        val tsUs = System.nanoTime() / 1000L
        val hdr = MediaPacketHeader(MediaPacketHeader.STREAM_PHONE_MIC, 0x01, seq, tsUs)
        val wire = cipher.encrypt(hdr, opusBuf, length)
        try {
            sock.send(DatagramPacket(wire, wire.size, dest))
        } catch (_: Exception) {
        }
    }

    fun disconnectSession(byUser: Boolean = false) {
        if (byUser) {
            userPausedReconnect = true
        }
        try {
            activeSslSocket?.close()
        } catch (_: Exception) {
        }
        activeSslSocket = null
        controlOut = null

        try {
            udpSocket?.close()
        } catch (_: Exception) {
        }
        udpSocket = null
    }

    private fun connectInternal(
        pcNameHint: String,
        candidateIps: List<String>,
        controlPort: Int,
        mediaPort: Int,
        expectedCertFpHex: String,
        token: String,
        initialKey16: ByteArray?,
        isReconnect: Boolean
    ) {
        disconnectSession()

        val trustPinned = arrayOf<TrustManager>(object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {}
            override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                require(!chain.isNullOrEmpty()) { "Empty server certificate chain" }
                val sha256 = MessageDigest.getInstance("SHA-256").digest(chain[0].encoded)
                val actualHex = sha256.joinToString("") { "%02X".format(it) }
                require(actualHex.equals(expectedCertFpHex, ignoreCase = true)) {
                    "TLS Certificate Fingerprint mismatch!"
                }
            }

            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
        })

        val sslContext = SSLContext.getInstance("TLS")
        sslContext.init(null, trustPinned, SecureRandom())

        var connectedSocket: SSLSocket? = null
        var chosenIp = ""

        for (ip in candidateIps) {
            try {
                val rawSock = sslContext.socketFactory.createSocket() as SSLSocket
                rawSock.tcpNoDelay = true
                rawSock.connect(InetSocketAddress(ip, controlPort), 1800)
                rawSock.startHandshake()
                connectedSocket = rawSock
                chosenIp = ip
                break
            } catch (_: Exception) {
            }
        }

        val ssl = connectedSocket ?: throw IllegalStateException("Không thể kết nối tới PC ($candidateIps:$controlPort)")
        activeSslSocket = ssl

        val dOut = DataOutputStream(ssl.outputStream)
        val dIn = DataInputStream(ssl.inputStream)
        controlOut = dOut

        val udp = DatagramSocket()
        udp.trafficClass = 0xB8 // DSCP EF QoS
        udpSocket = udp

        val devName = "${Build.MANUFACTURER} ${Build.MODEL}".trim()
        val helloJson = JSONObject()
            .put("peerId", peerId)
            .put("deviceName", devName)
            .put("token", token)
            .put("isReconnect", isReconnect)
            .put("udpPort", udp.localPort)
            .put("androidSdk", Build.VERSION.SDK_INT)
            .toString()

        ControlFrameCodec.writeFrame(dOut, ControlMessageType.PAIR_HELLO, helloJson)

        val acceptFrame = ControlFrameCodec.readFrame(dIn)
            ?: throw IllegalStateException("PC rejected pairing handshake")
        require(acceptFrame.first == ControlMessageType.PAIR_ACCEPT) { "Unexpected handshake response" }

        val acceptObj = JSONObject(acceptFrame.second)
        val pcName = acceptObj.optString("pcName", pcNameHint)
        val reconnectToken = acceptObj.getString("reconnectToken")
        val mediaKeyB64 = acceptObj.getString("mediaKeyBase64")
        val serverUdpPort = acceptObj.optInt("udpPort", mediaPort)
        val sessionKey = Base64.getDecoder().decode(mediaKeyB64).takeIf { it.size == 16 }
            ?: initialKey16
            ?: throw IllegalStateException("Missing media key")

        // Persist trusted PC for seamless auto-reconnect!
        prefs.edit()
            .putString("saved_fp", expectedCertFpHex)
            .putString("saved_pc_name", pcName)
            .putString("saved_reconnect_token", reconnectToken)
            .putString("saved_last_ip", chosenIp)
            .putInt("saved_ctrl_port", controlPort)
            .putInt("saved_udp_port", serverUdpPort)
            .apply()

        mediaCipher = MediaFrameCipher(sessionKey)
        val mediaIp = if (chosenIp == "127.0.0.1") {
            candidateIps.firstOrNull { it != "127.0.0.1" } ?: chosenIp
        } else {
            chosenIp
        }
        remoteMediaAddress = InetSocketAddress(mediaIp, serverUdpPort)

        // Send an authenticated 0-byte UDP keepalive so PC immediately learns our Wi-Fi IP
        // even when Control is running over USB Cable (127.0.0.1)!
        try {
            val punchHdr = MediaPacketHeader(MediaPacketHeader.STREAM_PHONE_MIC, 0x00, 0L, System.nanoTime() / 1000L)
            val punchWire = mediaCipher!!.encrypt(punchHdr, ByteArray(0), 0)
            udp.send(DatagramPacket(punchWire, punchWire.size, remoteMediaAddress))
        } catch (_: Exception) {
        }

        onConnectionState(true, pcName, chosenIp, 0.0)

        // Start UDP Media Receiver loop
        ioPool.execute { udpReceiveLoop(udp) }

        // Read Control Frame loop
        try {
            while (running && !ssl.isClosed) {
                val frame = ControlFrameCodec.readFrame(dIn) ?: break
                handleControlFrame(frame.first, frame.second, pcName, chosenIp)
            }
        } catch (e: Exception) {
            android.util.Log.w("BlueipoothNet", "Control loop closed: ${e.message}", e)
        } finally {
            disconnectSession()
            onConnectionState(false, pcName, chosenIp, 0.0)
        }
    }

    private fun handleControlFrame(type: Byte, json: String, pcName: String, ip: String) {
        when (type) {
            ControlMessageType.PING -> {
                val obj = JSONObject(json)
                val clientTs = obj.optLong("clientTimestampUs", 0L)
                val pong = JSONObject()
                    .put("clientTimestampUs", clientTs)
                    .put("remoteTimestampUs", System.nanoTime() / 1000L)
                    .toString()
                sendControlJson(ControlMessageType.PONG, pong)
            }
            ControlMessageType.PONG -> {
                val obj = JSONObject(json)
                val sentTs = obj.optLong("clientTimestampUs", 0L)
                val rttMs = ((System.nanoTime() / 1000L - sentTs) / 1000.0).coerceAtLeast(0.2)
                onConnectionState(true, pcName, ip, rttMs)
            }
            ControlMessageType.BLE_SCAN_CMD -> {
                val obj = JSONObject(json)
                onBleScanCommand(obj.optBoolean("enable", false))
            }
            ControlMessageType.BLE_ADV_CMD -> {
                val obj = JSONObject(json)
                onBleAdvCommand(obj.optBoolean("enable", false), obj.optString("advDataHex", ""))
            }
            ControlMessageType.AUDIO_CONFIG -> {
                val obj = JSONObject(json)
                onAudioConfig(
                    obj.optString("mode", "GamingOpus"),
                    obj.optBoolean("micEnabled", true)
                )
            }
        }
    }

    private fun udpReceiveLoop(sock: DatagramSocket) {
        val buf = ByteArray(2048)
        val pkt = DatagramPacket(buf, buf.size)
        while (running && !sock.isClosed) {
            try {
                sock.receive(pkt)
                val cipher = mediaCipher ?: continue
                val res = cipher.decrypt(pkt.data, pkt.length) ?: continue
                if (res.first.streamId == MediaPacketHeader.STREAM_PC_STEREO) {
                    onStereoOpusPacket(res.first.sequenceNumber, res.second)
                }
            } catch (_: Exception) {
                if (sock.isClosed) break
            }
        }
    }

    private fun discoveryLoop() {
        val discovered = linkedMapOf<String, DiscoveredPcHost>()
        var sock: DatagramSocket? = null
        try {
            sock = DatagramSocket(null).apply {
                reuseAddress = true
                broadcast = true
                soTimeout = 1200
                bind(InetSocketAddress(49549))
            }
        } catch (_: Exception) {
            try {
                sock = DatagramSocket().apply {
                    broadcast = true
                    soTimeout = 1200
                }
            } catch (_: Exception) {
            }
        }

        val probeBytes = "BLUEIPOOTH_DISCOVER".toByteArray(Charsets.UTF_8)
        val recvBuf = ByteArray(2048)

        while (running) {
            try {
                if (activeSslSocket == null) {
                    // Send active broadcast discovery probe
                    sock?.let { s ->
                        try {
                            s.send(DatagramPacket(probeBytes, probeBytes.size, InetAddress.getByName("255.255.255.255"), 49549))
                        } catch (_: Exception) {
                        }

                        val rp = DatagramPacket(recvBuf, recvBuf.size)
                        try {
                            s.receive(rp)
                            val txt = String(rp.data, 0, rp.length, Charsets.UTF_8)
                            if (txt.startsWith("{")) {
                                val obj = JSONObject(txt)
                                val fp = obj.optString("fp", "")
                                if (fp.isNotEmpty()) {
                                    val ipsArr = obj.optJSONArray("ips") ?: JSONArray()
                                    val ips = mutableListOf<String>()
                                    rp.address?.hostAddress?.let { ips.add(it) }
                                    for (i in 0 until ipsArr.length()) {
                                        ips.add(ipsArr.getString(i))
                                    }
                                    val host = DiscoveredPcHost(
                                        pcName = obj.optString("pcName", "Windows PC"),
                                        ips = ips.distinct(),
                                        controlPort = obj.optInt("controlPort", 49550),
                                        mediaPort = obj.optInt("mediaPort", 49551),
                                        certFpHex = fp,
                                        hasUsb = obj.optBoolean("hasUsb", false)
                                    )
                                    discovered[fp] = host
                                    onDiscoveredHostsUpdated(discovered.values.toList())
                                }
                            }
                        } catch (_: Exception) {
                        }
                    }

                    // Auto-reconnect if we have a previously paired PC (unless user explicitly tapped Disconnect)!
                    val savedFp = prefs.getString("saved_fp", null)
                    val savedTok = prefs.getString("saved_reconnect_token", null)
                    if (!userPausedReconnect && !savedFp.isNullOrBlank() && !savedTok.isNullOrBlank()) {
                        val matchedHost = discovered[savedFp]
                        val savedIp = prefs.getString("saved_last_ip", null)
                        val candidates = buildList {
                            add("127.0.0.1") // Instant USB ADB reverse tunnel priority!
                            if (matchedHost != null) addAll(matchedHost.ips)
                            if (!savedIp.isNullOrBlank()) add(savedIp)
                        }.distinct()

                        try {
                            connectInternal(
                                pcNameHint = prefs.getString("saved_pc_name", "PC") ?: "PC",
                                candidateIps = candidates,
                                controlPort = matchedHost?.controlPort ?: prefs.getInt("saved_ctrl_port", 49550),
                                mediaPort = matchedHost?.mediaPort ?: prefs.getInt("saved_udp_port", 49551),
                                expectedCertFpHex = savedFp,
                                token = savedTok,
                                initialKey16 = null,
                                isReconnect = true
                            )
                        } catch (_: Exception) {
                        }
                    }
                } else {
                    // Send periodic Ping when connected
                    val ping = JSONObject().put("clientTimestampUs", System.nanoTime() / 1000L).toString()
                    sendControlJson(ControlMessageType.PING, ping)
                }

                Thread.sleep(2000L)
            } catch (_: InterruptedException) {
                break
            } catch (_: Exception) {
            }
        }
        sock?.close()
    }
}
