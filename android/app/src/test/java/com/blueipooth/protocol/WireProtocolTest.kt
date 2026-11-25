package com.blueipooth.protocol

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

class WireProtocolTest {

    @Test
    fun parseQrUri_extractsAllFieldsCorrectly() {
        val uri = "blueipooth://pair?v=1&name=Viet-PC&ips=192.168.1.10%2C192.168.42.129&port=49550&uport=49551&fp=AABBCCDD&key=AAAAAAAAAAAAAAAAAAAAAA&tok=11223344"
        val info = QrPairingInfo.parse(uri)
        assertEquals(1, info.version)
        assertEquals("Viet-PC", info.pcName)
        assertEquals(listOf("192.168.1.10", "192.168.42.129"), info.candidateIps)
        assertEquals(49550, info.controlPort)
        assertEquals(49551, info.mediaUdpPort)
        assertEquals("AABBCCDD", info.certSha256Hex)
        assertEquals(16, info.sessionKeyAes128.size)
        assertEquals("11223344", info.oneTimeToken)
    }

    @Test
    fun mediaFrameCipher_encryptsInDotNetLayoutAndRejectsTamper() {
        val key = ByteArray(16) { (it + 1).toByte() }
        val cipher = MediaFrameCipher(key)

        val opusSample = byteArrayOf(0xFC.toByte(), 0x10, 0x20, 0x30, 0x40)
        val hdr = MediaPacketHeader(
            streamId = MediaPacketHeader.STREAM_PC_STEREO,
            flags = 0x01,
            sequenceNumber = 77L,
            timestampUs = 987654321L
        )

        val wire = cipher.encrypt(hdr, opusSample, opusSample.size)
        assertEquals(MediaPacketHeader.OVERHEAD_BYTES + opusSample.size, wire.size)

        val dec = cipher.decrypt(wire, wire.size)
        assertNotNull(dec)
        assertEquals(hdr, dec!!.first)
        assertArrayEquals(opusSample, dec.second)

        // Flip 1 bit in header AAD -> must fail GCM authentication!
        wire[5] = (wire[5].toInt() xor 0x01).toByte()
        assertNull(cipher.decrypt(wire, wire.size))
    }
}
