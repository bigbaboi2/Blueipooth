namespace Blueipooth.Audio;

/// <summary>
/// Ultra-thin Adaptive Jitter Buffer (10-20ms target depth) for real-time 5ms/10ms UDP Opus streams.
/// Handles packet reordering, clock-drift bounding, and signals Opus PLC on missing packets.
/// </summary>
public sealed class AdaptiveJitterBuffer
{
    private readonly SortedDictionary<uint, byte[]> _frames = new();
    private readonly Lock _sync = new();
    private readonly int _targetDepth;
    private readonly int _maxDepth;

    private bool _primed;
    private uint _expectedSeq;

    public long TotalPacketsPushed { get; private set; }
    public long TotalPlcFrames { get; private set; }
    public int CurrentDepth { get { lock (_sync) return _frames.Count; } }

    public AdaptiveJitterBuffer(int targetDepthFrames = 3, int maxDepthFrames = 8)
    {
        _targetDepth = Math.Max(1, targetDepthFrames);
        _maxDepth = Math.Max(_targetDepth + 2, maxDepthFrames);
    }

    public void Push(uint sequenceNumber, ReadOnlySpan<byte> opusPayload)
    {
        lock (_sync)
        {
            TotalPacketsPushed++;

            if (!_primed)
            {
                _frames[sequenceNumber] = opusPayload.ToArray();
                if (_frames.Count >= _targetDepth)
                {
                    _primed = true;
                    _expectedSeq = _frames.Keys.First();
                }
                return;
            }

            // Drop stale packets older than expectedSeq (accounting for wrap-around distance)
            int delta = unchecked((int)(sequenceNumber - _expectedSeq));
            if (delta < 0)
            {
                return;
            }

            _frames[sequenceNumber] = opusPayload.ToArray();

            // Prevent latency buildup due to clock drift: trim oldest if exceeding maxDepth
            while (_frames.Count > _maxDepth)
            {
                uint firstKey = _frames.Keys.First();
                _frames.Remove(firstKey);
                _expectedSeq = _frames.Keys.First();
            }
        }
    }

    /// <summary>
    /// Pops the next sequential Opus frame, or returns null (with isMissingForPlc = true) when a packet dropped.
    /// Returns (null, false) when still priming silence.
    /// </summary>
    public (byte[]? OpusFrame, bool IsMissingForPlc) PopNext()
    {
        lock (_sync)
        {
            if (!_primed)
            {
                return (null, false);
            }

            if (_frames.Remove(_expectedSeq, out byte[]? frame))
            {
                _expectedSeq = unchecked(_expectedSeq + 1);
                return (frame, false);
            }

            if (_frames.Count == 0)
            {
                // Underrun: re-prime on next burst
                _primed = false;
                TotalPlcFrames++;
                return (null, true);
            }

            // Packet gap detected -> advance expectedSeq and instruct Opus decoder to run PLC
            _expectedSeq = unchecked(_expectedSeq + 1);
            TotalPlcFrames++;
            return (null, true);
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _frames.Clear();
            _primed = false;
            _expectedSeq = 0;
        }
    }
}
