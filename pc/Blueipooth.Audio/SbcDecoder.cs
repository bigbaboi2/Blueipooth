using System.Buffers.Binary;

namespace Blueipooth.Audio;

/// <summary>
/// Pure C# A2DP SBC (Subband Coding) frame decoder per Bluetooth A2DP spec v1.3 Appendix B.
/// Decodes incoming Windows BthA2dp.sys SBC RTP payloads into 16-bit/float stereo PCM.
/// </summary>
public sealed class SbcDecoder
{
    private static readonly int[] SampleRates = [16000, 32000, 44100, 48000];
    private static readonly int[] BlocksTable = [4, 8, 12, 16];

    private static readonly int[,] Offset4 =
    {
        { -1, 0, 0, 0 },
        { -2, 0, 0, 1 },
        { -2, 0, 0, 1 },
        { -2, 0, 0, 1 }
    };

    private static readonly int[,] Offset8 =
    {
        { -2, 0, 0, 0, 0, 0, 0, 1 },
        { -3, 0, 0, 0, 0, 0, 1, 2 },
        { -4, 0, 0, 0, 0, 0, 1, 2 },
        { -4, 0, 0, 0, 0, 0, 1, 2 }
    };

    // Standard 4-subband & 8-subband synthesis proto filter coefficients (scaled to float)
    private static readonly float[] Proto4 =
    [
        0.00000000e+00f, 5.36548976e-04f, 1.49188357e-03f, 2.73370904e-03f,
        3.83720193e-03f, 3.89205149e-03f, 1.86581691e-03f, -3.06012286e-03f,
        1.09137620e-02f, 2.04385087e-02f, 2.88757392e-02f, 3.21939290e-02f,
        2.58767811e-02f, 6.13245186e-03f, -2.88217274e-02f, -7.76463494e-02f,
        1.35593274e-01f, 1.94987841e-01f, 2.46636662e-01f, 2.81828203e-01f,
        2.94315332e-01f, 2.81828203e-01f, 2.46636662e-01f, 1.94987841e-01f,
        -1.35593274e-01f, -7.76463494e-02f, -2.88217274e-02f, 6.13245186e-03f,
        2.58767811e-02f, 3.21939290e-02f, 2.88757392e-02f, 2.04385087e-02f,
        -1.09137620e-02f, -3.06012286e-03f, 1.86581691e-03f, 3.89205149e-03f,
        3.83720193e-03f, 2.73370904e-03f, 1.49188357e-03f, 5.36548976e-04f
    ];

    private static readonly float[] Proto8 =
    [
        0.00000000e+00f, 1.56575398e-04f, 3.43256425e-04f, 5.54620202e-04f,
        8.23919506e-04f, 1.13992507e-03f, 1.47640169e-03f, 1.78371725e-03f,
        2.01182542e-03f, 2.10371989e-03f, 1.99454554e-03f, 1.61656283e-03f,
        9.02154502e-04f, -1.78805361e-04f, -1.64973098e-03f, -3.49717454e-03f,
        5.65949473e-03f, 8.02941163e-03f, 1.04584443e-02f, 1.27472335e-02f,
        1.46525263e-02f, 1.59045603e-02f, 1.62208471e-02f, 1.53184106e-02f,
        1.29371806e-02f, 8.85757540e-03f, 2.92408442e-03f, -4.91578024e-03f,
        -1.46404076e-02f, -2.61098752e-02f, -3.90751381e-02f, -5.31873032e-02f,
        6.79989431e-02f, 8.29847578e-02f, 9.75753918e-02f, 1.11196689e-01f,
        1.23264548e-01f, 1.33264415e-01f, 1.40753505e-01f, 1.45389847e-01f,
        1.46955068e-01f, 1.45389847e-01f, 1.40753505e-01f, 1.33264415e-01f,
        1.23264548e-01f, 1.11196689e-01f, 9.75753918e-02f, 8.29847578e-02f,
        -6.79989431e-02f, -5.31873032e-02f, -3.90751381e-02f, -2.61098752e-02f,
        -1.46404076e-02f, -4.91578024e-03f, 2.92408442e-03f, 8.85757540e-03f,
        1.29371806e-02f, 1.53184106e-02f, 1.62208471e-02f, 1.59045603e-02f,
        1.46525263e-02f, 1.27472335e-02f, 1.04584443e-02f, 8.02941163e-03f,
        -5.65949473e-03f, -3.49717454e-03f, -1.64973098e-03f, -1.78805361e-04f,
        9.02154502e-04f, 1.61656283e-03f, 1.99454554e-03f, 2.10371989e-03f,
        2.01182542e-03f, 1.78371725e-03f, 1.47640169e-03f, 1.13992507e-03f,
        8.23919506e-04f, 5.54620202e-04f, 3.43256425e-04f, 1.56575398e-04f
    ];

    private readonly float[,] _v = new float[2, 160];
    private readonly float[,] _cos4 = new float[4, 8];
    private readonly float[,] _cos8 = new float[8, 16];

    public int LastSampleRate { get; private set; } = 44100;

    public SbcDecoder()
    {
        for (int i = 0; i < 4; i++)
            for (int k = 0; k < 8; k++)
                _cos4[i, k] = MathF.Cos((i + 0.5f) * (k - 2) * (MathF.PI / 4f));

        for (int i = 0; i < 8; i++)
            for (int k = 0; k < 16; k++)
                _cos8[i, k] = MathF.Cos((i + 0.5f) * (k - 4) * (MathF.PI / 8f));
    }

    /// <summary>
    /// Decodes an AVDTP SBC RTP payload ([1B numFrames/flags][SBC frames...]) into interleaved stereo float PCM.
    /// </summary>
    public float[] DecodeRtpPayload(ReadOnlySpan<byte> rtpPayload)
    {
        if (rtpPayload.Length < 5) return [];

        // A2DP SBC RTP Media Payload Header (1 byte): bit 0..3 = number of SBC frames
        int offset = 1;
        var output = new List<float>(2048);

        while (offset + 4 <= rtpPayload.Length)
        {
            if (rtpPayload[offset] != 0x9C) break; // SBC Syncword

            int consumed = DecodeFrame(rtpPayload[offset..], output);
            if (consumed <= 0) break;
            offset += consumed;
        }

        return [.. output];
    }

    private int DecodeFrame(ReadOnlySpan<byte> frame, List<float> stereoOut)
    {
        byte h1 = frame[1];
        int freqIdx = (h1 >> 6) & 0x03;
        int blocks = BlocksTable[(h1 >> 4) & 0x03];
        int chMode = (h1 >> 2) & 0x03; // 0=Mono, 1=Dual, 2=Stereo, 3=JointStereo
        int allocMethod = (h1 >> 1) & 0x01; // 0=Loudness, 1=SNR
        int subbands = ((h1 & 0x01) != 0) ? 8 : 4;
        int bitpool = frame[2];
        // frame[3] = crc

        LastSampleRate = SampleRates[freqIdx];
        int channels = chMode == 0 ? 1 : 2;
        int headerBytes = 4;

        int bitPos = headerBytes * 8;

        int joinMask = 0;
        if (chMode == 3) // Joint Stereo
        {
            for (int sb = 0; sb < subbands - 1; sb++)
            {
                if (ReadBits(frame, ref bitPos, 1) != 0)
                    joinMask |= 1 << sb;
            }
            ReadBits(frame, ref bitPos, 1); // RFA bit
        }

        int[,] scaleFactor = new int[2, 8];
        for (int ch = 0; ch < channels; ch++)
            for (int sb = 0; sb < subbands; sb++)
                scaleFactor[ch, sb] = ReadBits(frame, ref bitPos, 4);

        int[,] bits = new int[2, 8];
        ComputeBitAllocation(freqIdx, chMode, allocMethod, subbands, channels, bitpool, scaleFactor, bits);

        float[,,] sbSample = new float[2, 16, 8];
        for (int blk = 0; blk < blocks; blk++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                for (int sb = 0; sb < subbands; sb++)
                {
                    int b = bits[ch, sb];
                    if (b <= 0)
                    {
                        sbSample[ch, blk, sb] = 0f;
                        continue;
                    }
                    int raw = ReadBits(frame, ref bitPos, b);
                    float levels = (1 << b) - 1;
                    float q = ((raw * 2.0f + 1.0f) / levels) - 1.0f;
                    sbSample[ch, blk, sb] = q * (1 << (scaleFactor[ch, sb] + 1));
                }
            }
        }

        if (chMode == 3)
        {
            for (int blk = 0; blk < blocks; blk++)
            {
                for (int sb = 0; sb < subbands; sb++)
                {
                    if ((joinMask & (1 << sb)) != 0)
                    {
                        float s0 = sbSample[0, blk, sb];
                        float s1 = sbSample[1, blk, sb];
                        sbSample[0, blk, sb] = s0 + s1;
                        sbSample[1, blk, sb] = s0 - s1;
                    }
                }
            }
        }

        // Synthesis filterbank -> interleaved stereo float [-1, 1]
        Span<float> pcmL = stackalloc float[subbands];
        Span<float> pcmR = stackalloc float[subbands];
        for (int blk = 0; blk < blocks; blk++)
        {
            Synthesize(0, subbands, sbSample, blk, pcmL);
            if (channels == 2)
                Synthesize(1, subbands, sbSample, blk, pcmR);
            else
                pcmL.CopyTo(pcmR);

            for (int i = 0; i < subbands; i++)
            {
                stereoOut.Add(Math.Clamp(pcmL[i] / 32768f, -1f, 1f));
                stereoOut.Add(Math.Clamp(pcmR[i] / 32768f, -1f, 1f));
            }
        }

        int frameBytes = (bitPos + 7) / 8;
        return frameBytes;
    }

    private void Synthesize(int ch, int subbands, float[,,] sbSample, int blk, Span<float> pcmOut)
    {
        int n2 = subbands * 2;
        // Shift V buffer right by 2*subbands
        for (int i = subbands * 20 - 1; i >= n2; i--)
            _v[ch, i] = _v[ch, i - n2];

        if (subbands == 4)
        {
            for (int k = 0; k < 8; k++)
            {
                float sum = 0f;
                for (int i = 0; i < 4; i++)
                    sum += sbSample[ch, blk, i] * _cos4[i, k];
                _v[ch, k] = sum;
            }
            for (int i = 0; i < 4; i++)
            {
                float s = 0f;
                for (int j = 0; j < 10; j++)
                {
                    int vIdx = j * 8 + ((j & 1) == 0 ? i : i + 4);
                    s += _v[ch, vIdx] * Proto4[j * 4 + i];
                }
                pcmOut[i] = s * -4f;
            }
        }
        else
        {
            for (int k = 0; k < 16; k++)
            {
                float sum = 0f;
                for (int i = 0; i < 8; i++)
                    sum += sbSample[ch, blk, i] * _cos8[i, k];
                _v[ch, k] = sum;
            }
            for (int i = 0; i < 8; i++)
            {
                float s = 0f;
                for (int j = 0; j < 10; j++)
                {
                    int vIdx = j * 16 + ((j & 1) == 0 ? i : i + 8);
                    s += _v[ch, vIdx] * Proto8[j * 8 + i];
                }
                pcmOut[i] = s * -8f;
            }
        }
    }

    private static void ComputeBitAllocation(
        int freqIdx, int chMode, int allocMethod, int subbands, int channels,
        int bitpool, int[,] scaleFactor, int[,] bits)
    {
        int[,] bitneed = new int[2, 8];
        int maxBitneed = 0;

        for (int ch = 0; ch < channels; ch++)
        {
            for (int sb = 0; sb < subbands; sb++)
            {
                int sf = scaleFactor[ch, sb];
                int bn;
                if (allocMethod == 1) // SNR
                {
                    bn = sf;
                }
                else // Loudness
                {
                    if (sf == 0)
                    {
                        bn = -5;
                    }
                    else
                    {
                        int loudness = sf - (subbands == 4 ? Offset4[freqIdx, sb] : Offset8[freqIdx, sb]);
                        bn = loudness > 0 ? loudness / 2 : loudness;
                    }
                }
                bitneed[ch, sb] = bn;
                if (bn > maxBitneed) maxBitneed = bn;
            }
        }

        // Dual channel allocates independently per channel; Stereo/JointStereo shares bitpool
        int passes = chMode == 1 ? 2 : 1;
        for (int pass = 0; pass < passes; pass++)
        {
            int chStart = chMode == 1 ? pass : 0;
            int chEnd = chMode == 1 ? pass + 1 : channels;

            int bitcount = 0;
            int slicecount = 0;
            int bitslice = maxBitneed + 1;

            do
            {
                bitslice--;
                bitcount += slicecount;
                slicecount = 0;
                for (int ch = chStart; ch < chEnd; ch++)
                {
                    for (int sb = 0; sb < subbands; sb++)
                    {
                        if (bitneed[ch, sb] > bitslice + 1 && bitneed[ch, sb] < bitslice + 16)
                            slicecount++;
                        else if (bitneed[ch, sb] == bitslice + 1)
                            slicecount += 2;
                    }
                }
            } while (bitcount + slicecount < bitpool);

            if (bitcount + slicecount == bitpool)
            {
                bitcount += slicecount;
                bitslice--;
            }

            for (int ch = chStart; ch < chEnd; ch++)
            {
                for (int sb = 0; sb < subbands; sb++)
                {
                    if (bitneed[ch, sb] < bitslice + 2)
                        bits[ch, sb] = 0;
                    else
                        bits[ch, sb] = Math.Min(16, bitneed[ch, sb] - bitslice);
                }
            }

            // Distribute remaining bits
            int sbIdx = 0;
            int chIdx = chStart;
            while (bitcount < bitpool && sbIdx < subbands)
            {
                if (bits[chIdx, sbIdx] >= 2 && bits[chIdx, sbIdx] < 16)
                {
                    bits[chIdx, sbIdx]++;
                    bitcount++;
                }
                else if (bitneed[chIdx, sbIdx] == bitslice + 1 && bitpool > bitcount + 1)
                {
                    bits[chIdx, sbIdx] = 2;
                    bitcount += 2;
                }

                chIdx++;
                if (chIdx >= chEnd)
                {
                    chIdx = chStart;
                    sbIdx++;
                }
            }

            sbIdx = 0;
            chIdx = chStart;
            while (bitcount < bitpool && sbIdx < subbands)
            {
                if (bits[chIdx, sbIdx] < 16)
                {
                    bits[chIdx, sbIdx]++;
                    bitcount++;
                }
                chIdx++;
                if (chIdx >= chEnd)
                {
                    chIdx = chStart;
                    sbIdx++;
                }
            }
        }
    }

    private static int ReadBits(ReadOnlySpan<byte> data, ref int bitPos, int count)
    {
        int val = 0;
        for (int i = 0; i < count; i++)
        {
            int byteIdx = bitPos >> 3;
            if (byteIdx >= data.Length) break;
            int bitIdx = 7 - (bitPos & 7);
            val = (val << 1) | ((data[byteIdx] >> bitIdx) & 1);
            bitPos++;
        }
        return val;
    }
}
