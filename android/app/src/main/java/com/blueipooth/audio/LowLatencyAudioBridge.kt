package com.blueipooth.audio

import android.annotation.SuppressLint
import android.content.Context
import android.media.AudioAttributes
import android.media.AudioDeviceCallback
import android.media.AudioDeviceInfo
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.media.AudioTrack
import android.media.MediaRecorder
import android.os.Handler
import android.os.Looper
import java.util.TreeMap
import io.github.jaredmdobson.concentus.OpusApplication
import io.github.jaredmdobson.concentus.OpusDecoder
import io.github.jaredmdobson.concentus.OpusEncoder

data class ConnectedHeadsetInfo(
    val connected: Boolean,
    val name: String,
    val mac: String,
    val hasMic: Boolean
)

/**
 * Low-Latency Dual-Direction Audio Bridge on Android:
 * - Detects AirPods / Bluetooth Speakers / BLE Audio headsets via AudioDeviceCallback.
 * - Decodes 48kHz Stereo 5ms Opus frames from PC via Adaptive Jitter Buffer -> AudioTrack (LOW_LATENCY).
 * - Captures 48kHz Mono VOICE_COMMUNICATION mic (with hardware Acoustic Echo Cancellation) -> Opus 5ms -> PC!
 */
@SuppressLint("MissingPermission")
class LowLatencyAudioBridge(
    context: Context,
    private val onHeadsetChanged: (ConnectedHeadsetInfo) -> Unit,
    private val onMicOpusFrameReady: (ByteArray, Int) -> Unit
) {
    companion object {
        const val SAMPLE_RATE = 48000
        const val SAMPLES_PER_5MS = 480
        const val STEREO_SAMPLES_PER_5MS = SAMPLES_PER_5MS * 2
    }

    private val audioManager = context.getSystemService(Context.AUDIO_SERVICE) as AudioManager
    private val mainHandler = Handler(Looper.getMainLooper())

    private val stereoDecoder = OpusDecoder(SAMPLE_RATE, 2)
    private val micEncoder = OpusEncoder(SAMPLE_RATE, 1, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY).apply {
        bitrate = 64000
        complexity = 4
    }

    private val jitterLock = Any()
    private val jitterFrames = TreeMap<Long, ByteArray>()
    private var jitterPrimed = false
    private var expectedSeq = 0L
    @Volatile
    private var targetJitterFrames = 6 // 6 * 5ms = 30ms default

    private var audioTrack: AudioTrack? = null
    private var playbackThread: Thread? = null

    private var audioRecord: AudioRecord? = null
    private var micThread: Thread? = null

    @Volatile
    private var running = false

    @Volatile
    var micCaptureEnabled: Boolean = false
        private set

    @Volatile
    var currentHeadset: ConnectedHeadsetInfo = ConnectedHeadsetInfo(false, "Phone Speaker", "", true)
        private set

    private val deviceCallback = object : AudioDeviceCallback() {
        override fun onAudioDevicesAdded(addedDevices: Array<out AudioDeviceInfo>?) = refreshBluetoothHeadset()
        override fun onAudioDevicesRemoved(removedDevices: Array<out AudioDeviceInfo>?) = refreshBluetoothHeadset()
    }

    fun start() {
        if (running) return
        running = true
        audioManager.registerAudioDeviceCallback(deviceCallback, mainHandler)
        refreshBluetoothHeadset()
        startLowLatencyPlaybackThread()
        updateMicCaptureThread()
    }

    fun stop() {
        running = false
        try {
            audioManager.unregisterAudioDeviceCallback(deviceCallback)
        } catch (_: Exception) {
        }
        playbackThread?.interrupt()
        playbackThread = null
        micThread?.interrupt()
        micThread = null

        try {
            audioTrack?.stop()
            audioTrack?.release()
        } catch (_: Exception) {
        }
        audioTrack = null

        try {
            audioRecord?.stop()
            audioRecord?.release()
        } catch (_: Exception) {
        }
        audioRecord = null
    }

    fun applyAudioConfig(micEnabled: Boolean, jitterBufferMs: Int = 30) {
        targetJitterFrames = (jitterBufferMs / 5).coerceIn(2, 16)
        if (micCaptureEnabled != micEnabled) {
            micCaptureEnabled = micEnabled
            updateMicCaptureThread()
        }
    }

    fun pushIncomingPcStereoPacket(seq: Long, opusPayload: ByteArray) {
        synchronized(jitterLock) {
            if (!jitterPrimed) {
                jitterFrames[seq] = opusPayload
                if (jitterFrames.size >= targetJitterFrames) {
                    jitterPrimed = true
                    expectedSeq = jitterFrames.firstKey()
                    (jitterLock as Object).notifyAll()
                }
                return
            }

            if (seq < expectedSeq && (expectedSeq - seq) < 10000L) {
                return
            }
            jitterFrames[seq] = opusPayload
            val maxFrames = (targetJitterFrames + 8).coerceAtLeast(10)
            while (jitterFrames.size > maxFrames) {
                jitterFrames.pollFirstEntry()
                if (jitterFrames.isNotEmpty()) {
                    expectedSeq = jitterFrames.firstKey()
                }
            }
            (jitterLock as Object).notifyAll()
        }
    }

    fun refreshBluetoothHeadset() {
        val outputs = audioManager.getDevices(AudioManager.GET_DEVICES_OUTPUTS)
        val btOut = outputs.firstOrNull {
            it.type == AudioDeviceInfo.TYPE_BLUETOOTH_A2DP ||
                it.type == AudioDeviceInfo.TYPE_BLE_HEADSET ||
                it.type == AudioDeviceInfo.TYPE_BLE_SPEAKER ||
                it.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO
        }

        val inputs = audioManager.getDevices(AudioManager.GET_DEVICES_INPUTS)
        val hasBtMic = inputs.any {
            it.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO ||
                it.type == AudioDeviceInfo.TYPE_BLE_HEADSET ||
                it.type == AudioDeviceInfo.TYPE_BUILTIN_MIC
        }

        val info = if (btOut != null) {
            val productName = btOut.productName?.toString()?.takeIf { it.isNotBlank() } ?: "Bluetooth Audio"
            val addr = try {
                btOut.address ?: productName
            } catch (_: Exception) {
                productName
            }
            ConnectedHeadsetInfo(true, productName, addr, hasBtMic)
        } else {
            ConnectedHeadsetInfo(false, "Phone Speaker", "", true)
        }

        if (info != currentHeadset) {
            currentHeadset = info
            onHeadsetChanged(info)
        }
    }

    private fun startLowLatencyPlaybackThread() {
        val minBuf = AudioTrack.getMinBufferSize(
            SAMPLE_RATE,
            AudioFormat.CHANNEL_OUT_STEREO,
            AudioFormat.ENCODING_PCM_16BIT
        ).coerceAtLeast(STEREO_SAMPLES_PER_5MS * 16)

        val track = AudioTrack.Builder()
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                    .build()
            )
            .setAudioFormat(
                AudioFormat.Builder()
                    .setSampleRate(SAMPLE_RATE)
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
                    .build()
            )
            .setBufferSizeInBytes(minBuf)
            .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_NONE)
            .setTransferMode(AudioTrack.MODE_STREAM)
            .build()

        track.play()
        audioTrack = track

        playbackThread = Thread({
            android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_AUDIO)
            val pcmShort = ShortArray(STEREO_SAMPLES_PER_5MS)

            while (running && !Thread.currentThread().isInterrupted) {
                var nextOpus: ByteArray? = null
                var doPlc = false

                synchronized(jitterLock) {
                    if (!jitterPrimed) {
                        try {
                            (jitterLock as Object).wait(15L)
                        } catch (_: InterruptedException) {
                            return@Thread
                        }
                    }
                    if (jitterPrimed) {
                        val exact = jitterFrames.remove(expectedSeq)
                        if (exact != null) {
                            nextOpus = exact
                            expectedSeq++
                        } else if (jitterFrames.isEmpty()) {
                            jitterPrimed = false
                        } else {
                            doPlc = true
                            expectedSeq++
                        }
                    }
                }

                if (nextOpus != null || doPlc) {
                    try {
                        val decoded = if (nextOpus != null) {
                            stereoDecoder.decode(nextOpus, 0, nextOpus!!.size, pcmShort, 0, SAMPLES_PER_5MS, false)
                        } else {
                            stereoDecoder.decode(null, 0, 0, pcmShort, 0, SAMPLES_PER_5MS, false)
                        }
                        if (decoded > 0) {
                            track.write(pcmShort, 0, decoded * 2, AudioTrack.WRITE_BLOCKING)
                        }
                    } catch (_: Exception) {
                    }
                }
            }
        }, "Blueipooth-Playback").apply { start() }
    }

    private fun updateMicCaptureThread() {
        micThread?.interrupt()
        micThread = null
        try {
            audioRecord?.stop()
            audioRecord?.release()
        } catch (_: Exception) {
        }
        audioRecord = null

        if (!running || !micCaptureEnabled) return

        try {
            val minRecBuf = AudioRecord.getMinBufferSize(
                SAMPLE_RATE,
                AudioFormat.CHANNEL_IN_MONO,
                AudioFormat.ENCODING_PCM_16BIT
            ).coerceAtLeast(SAMPLES_PER_5MS * 8)

            val rec = AudioRecord(
                MediaRecorder.AudioSource.VOICE_COMMUNICATION,
                SAMPLE_RATE,
                AudioFormat.CHANNEL_IN_MONO,
                AudioFormat.ENCODING_PCM_16BIT,
                minRecBuf
            )
            if (rec.state != AudioRecord.STATE_INITIALIZED) {
                rec.release()
                return
            }
            rec.startRecording()
            audioRecord = rec

            micThread = Thread({
                android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_AUDIO)
                val pcmIn = ShortArray(SAMPLES_PER_5MS)
                val opusOut = ByteArray(256)

                while (running && micCaptureEnabled && !Thread.currentThread().isInterrupted) {
                    val read = rec.read(pcmIn, 0, SAMPLES_PER_5MS)
                    if (read == SAMPLES_PER_5MS) {
                        try {
                            val encodedLen = micEncoder.encode(pcmIn, 0, SAMPLES_PER_5MS, opusOut, 0, opusOut.size)
                            if (encodedLen > 0) {
                                onMicOpusFrameReady(opusOut, encodedLen)
                            }
                        } catch (_: Exception) {
                        }
                    }
                }
            }, "Blueipooth-Mic").apply { start() }
        } catch (_: SecurityException) {
            // RECORD_AUDIO permission not granted yet
        } catch (_: Exception) {
        }
    }
}
