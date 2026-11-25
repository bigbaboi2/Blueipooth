package com.blueipooth.protocol

import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.net.URI
import java.net.URLDecoder
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

object ControlMessageType {
    const val PAIR_HELLO: Byte = 0x01
    const val PAIR_ACCEPT: Byte = 0x02
    const val PING: Byte = 0x03
    const val PONG: Byte = 0x04

    const val BLE_SCAN_CMD: Byte = 0x10
    const val BLE_ADV_REPORT: Byte = 0x11
    const val BLE_ADV_CMD: Byte = 0x12
    const val BLE_GATT_REQ: Byte = 0x13
    const val BLE_GATT_RSP: Byte = 0x14

    const val HEADSET_STATE: Byte = 0x20
    const val AUDIO_CONFIG: Byte = 0x21
    const val MEDIA_CONTROL: Byte = 0x22
    const val DEVICE_STATUS: Byte = 0x23
}

data class QrPairingInfo(
    val version: Int,
    val pcName: String,
    val candidateIps: List<String>,
    val controlPort: Int,
    val mediaUdpPort: Int,
    val certSha256Hex: String,
    val sessionKeyAes128: ByteArray,
    val oneTimeToken: String
) {
    companion object {
        fun parse(uriString: String): QrPairingInfo {
            val uri = URI(uriString.trim())
            require(uri.scheme.equals("blueipooth", ignoreCase = true) && uri.host.equals("pair", ignoreCase = true)) {
                "Invalid Blueipooth QR URI"
            }
            val params = mutableMapOf<String, String>()
            val rawQuery = uri.rawQuery ?: ""
            for (part in rawQuery.split("&")) {
                if (part.isBlank()) continue
                val idx = part.indexOf('=')
                if (idx > 0) {
                    val k = URLDecoder.decode(part.substring(0, idx), "UTF-8")
                    val v = URLDecoder.decode(part.substring(idx + 1), "UTF-8")
                    params[k] = v
                }
            }
            val v = params["v"]?.toIntOrNull() ?: 1
            val name = params["name"] ?: "PC"
            val ips = (params["ips"] ?: "").split(",").map { it.trim() }.filter { it.isNotEmpty() }
            val port = params["port"]?.toIntOrNull() ?: 49550
            val uport = params["uport"]?.toIntOrNull() ?: 49551
            val fp = (params["fp"] ?: "").uppercase()
            val keyB64 = params["key"] ?: throw IllegalArgumentException("Missing key")
            val key = base64UrlDecode(keyB64)
            require(key.size == 16) { "AES-128 key must be 16 bytes" }
            val tok = params["tok"] ?: throw IllegalArgumentException("Missing tok")

            return QrPairingInfo(v, name, ips, port, uport, fp, key, tok)
        }

        fun base64UrlDecode(input: String): ByteArray {
            var padded = input.replace('-', '+').replace('_', '/')
            when (padded.length % 4) {
                2 -> padded += "=="
                3 -> padded += "="
            }
            return Base64.getDecoder().decode(padded)
        }
    }
}

object ControlFrameCodec {
    private const val MAX_PAYLOAD = 256 * 1024

    fun writeFrame(out: DataOutputStream, type: Byte, jsonUtf8: String) {
        val bytes = jsonUtf8.toByteArray(Charsets.UTF_8)
        synchronized(out) {
            out.writeInt(bytes.size) // Big-Endian UInt32
            out.writeByte(type.toInt())
            out.write(bytes)
            out.flush()
        }
    }

    fun readFrame(input: DataInputStream): Pair<Byte, String>? {
        val length = try {
            input.readInt()
        } catch (_: EOFException) {
            return null
        }
        require(length in 0..MAX_PAYLOAD) { "Invalid frame length: $length" }
        val type = input.readByte()
        val buf = ByteArray(length)
        if (length > 0) {
            input.readFully(buf)
        }
        return type to String(buf, Charsets.UTF_8)
    }
}

data class MediaPacketHeader(
    val streamId: Byte,
    val flags: Byte,
    val sequenceNumber: Long,
    val timestampUs: Long
) {
    companion object {
        const val MAGIC: Int = 0xB17E
        const val HEADER_SIZE = 16
        const val TAG_SIZE = 16
        const val OVERHEAD_BYTES = HEADER_SIZE + TAG_SIZE
        const val STREAM_PC_STEREO: Byte = 0x01
        const val STREAM_PHONE_MIC: Byte = 0x02

        fun readFrom(bytes: ByteArray, length: Int): MediaPacketHeader? {
            if (length < HEADER_SIZE) return null
            val magic = ((bytes[0].toInt() and 0xFF) shl 8) or (bytes[1].toInt() and 0xFF)
            if (magic != MAGIC) return null

            val le = ByteBuffer.wrap(bytes, 4, 12).order(ByteOrder.LITTLE_ENDIAN)
            val seq = le.int.toLong() and 0xFFFFFFFFL
            val ts = le.long
            return MediaPacketHeader(bytes[2], bytes[3], seq, ts)
        }
    }

    fun writeTo(dest16: ByteArray) {
        dest16[0] = ((MAGIC ushr 8) and 0xFF).toByte()
        dest16[1] = (MAGIC and 0xFF).toByte()
        dest16[2] = streamId
        dest16[3] = flags
        ByteBuffer.wrap(dest16, 4, 12).order(ByteOrder.LITTLE_ENDIAN).apply {
            putInt(sequenceNumber.toInt())
            putLong(timestampUs)
        }
    }

    fun deriveNonce12(): ByteArray {
        val nonce = ByteArray(12)
        nonce[0] = streamId
        nonce[1] = flags
        nonce[2] = 0
        nonce[3] = 0
        ByteBuffer.wrap(nonce, 4, 8).order(ByteOrder.LITTLE_ENDIAN).putLong(timestampUs)

        val seqBytes = ByteArray(4)
        ByteBuffer.wrap(seqBytes).order(ByteOrder.LITTLE_ENDIAN).putInt(sequenceNumber.toInt())
        for (i in 0 until 4) {
            nonce[i] = (nonce[i].toInt() xor seqBytes[i].toInt()).toByte()
        }
        return nonce
    }
}

/**
 * Wire-compatible AES-128-GCM cipher matching .NET MediaFrameCipher layout:
 * [Header 16B][GCM Tag 16B][Ciphertext NB]
 */
class MediaFrameCipher(key16: ByteArray) {
    private val keySpec = SecretKeySpec(key16.copyOf(16), "AES")
    private val lock = Any()

    fun encrypt(header: MediaPacketHeader, opusPlaintext: ByteArray, plainLen: Int): ByteArray {
        val out = ByteArray(MediaPacketHeader.OVERHEAD_BYTES + plainLen)
        header.writeTo(out)
        val nonce = header.deriveNonce12()

        val cipherPlusTag = synchronized(lock) {
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.ENCRYPT_MODE, keySpec, GCMParameterSpec(128, nonce))
            cipher.updateAAD(out, 0, MediaPacketHeader.HEADER_SIZE)
            cipher.doFinal(opusPlaintext, 0, plainLen) // Java returns [Ciphertext NB][Tag 16B]
        }

        // Convert [Ciphertext NB][Tag 16B] -> wire [Header 16B][Tag 16B][Ciphertext NB]
        System.arraycopy(cipherPlusTag, plainLen, out, MediaPacketHeader.HEADER_SIZE, MediaPacketHeader.TAG_SIZE)
        if (plainLen > 0) {
            System.arraycopy(cipherPlusTag, 0, out, MediaPacketHeader.OVERHEAD_BYTES, plainLen)
        }
        return out
    }

    fun decrypt(packet: ByteArray, packetLen: Int): Pair<MediaPacketHeader, ByteArray>? {
        if (packetLen < MediaPacketHeader.OVERHEAD_BYTES) return null
        val header = MediaPacketHeader.readFrom(packet, packetLen) ?: return null
        val cipherLen = packetLen - MediaPacketHeader.OVERHEAD_BYTES

        // Rearrange wire [Tag 16B][Ciphertext NB] -> Java [Ciphertext NB][Tag 16B]
        val jceInput = ByteArray(cipherLen + MediaPacketHeader.TAG_SIZE)
        if (cipherLen > 0) {
            System.arraycopy(packet, MediaPacketHeader.OVERHEAD_BYTES, jceInput, 0, cipherLen)
        }
        System.arraycopy(packet, MediaPacketHeader.HEADER_SIZE, jceInput, cipherLen, MediaPacketHeader.TAG_SIZE)

        val nonce = header.deriveNonce12()
        return try {
            val plain = synchronized(lock) {
                val cipher = Cipher.getInstance("AES/GCM/NoPadding")
                cipher.init(Cipher.DECRYPT_MODE, keySpec, GCMParameterSpec(128, nonce))
                cipher.updateAAD(packet, 0, MediaPacketHeader.HEADER_SIZE)
                cipher.doFinal(jceInput)
            }
            header to plain
        } catch (_: Exception) {
            null
        }
    }
}
