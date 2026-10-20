using System.Diagnostics;
using System.Runtime.CompilerServices;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Engine;

/// <summary>
/// Reference-counted, reusable Program frame buffers. The cadence rents one buffer per output
/// frame and every holder that uses the frame after the cadence tick (the output worker) adds a
/// reference. A buffer is reused only after all holders release it. When every slot is busy a
/// temporary buffer is allocated instead, so the cadence never waits for an output.
/// </summary>
public sealed class ProgramFrameRing
{
    private sealed class Slot
    {
        public byte[] Buffer = [];
        public int References;
    }

    private readonly object _sync = new();
    private readonly Slot[] _slots;
    private readonly ConditionalWeakTable<byte[], Slot> _owners = new();
    private long _overflowAllocations;

    public ProgramFrameRing(int depth = 4)
    {
        _slots = new Slot[Math.Clamp(depth, 2, 16)];
        for (var i = 0; i < _slots.Length; i++) _slots[i] = new Slot();
    }

    /// <summary>Number of times every slot was busy and a temporary buffer had to be allocated.</summary>
    public long OverflowAllocations => Interlocked.Read(ref _overflowAllocations);

    /// <summary>Rents a buffer of exactly <paramref name="length"/> bytes holding one reference for the caller.</summary>
    public byte[] Rent(int length)
    {
        lock (_sync)
        {
            foreach (var slot in _slots)
            {
                if (slot.References != 0) continue;
                if (slot.Buffer.Length != length)
                {
                    if (slot.Buffer.Length > 0) _owners.Remove(slot.Buffer);
                    slot.Buffer = GC.AllocateUninitializedArray<byte>(length, pinned: true);
                    _owners.Add(slot.Buffer, slot);
                }
                slot.References = 1;
                return slot.Buffer;
            }
        }
        Interlocked.Increment(ref _overflowAllocations);
        return GC.AllocateUninitializedArray<byte>(length);
    }

    /// <summary>Adds a holder to a rented buffer. Buffers this ring does not own are ignored.</summary>
    public void AddReference(byte[] buffer)
    {
        lock (_sync)
        {
            if (_owners.TryGetValue(buffer, out var slot) && slot.References > 0) slot.References++;
        }
    }

    /// <summary>Releases one holder. Buffers this ring does not own are ignored.</summary>
    public void Release(byte[] buffer)
    {
        lock (_sync)
        {
            if (!_owners.TryGetValue(buffer, out var slot) || slot.References <= 0) return;
            slot.References--;
            Debug.Assert(slot.References >= 0);
        }
    }
}

/// <summary>
/// Single-consumer latest-frame mailbox that copies each posted frame into one of three buffers
/// it owns. The producer may reuse its own buffer as soon as <see cref="Post"/> returns, and the
/// consumer's current frame is never overwritten until it takes a newer one.
/// </summary>
public sealed class FrameMailbox
{
    private readonly object _sync = new();
    private readonly byte[]?[] _buffers = new byte[]?[3];
    private readonly long _minIntervalTicks;
    private long _lastPostTicks;
    private int _pendingSlot = -1;
    private int _currentSlot = -1;
    private VideoFrameData? _pending;
    private VideoFrameData? _current;

    /// <param name="maxFps">Upper bound on copies per second; 0 copies every posted frame.</param>
    public FrameMailbox(double maxFps = 0)
    {
        _minIntervalTicks = maxFps > 0 ? (long)(Stopwatch.Frequency / maxFps * 0.85) : 0;
    }

    /// <summary>Copies the frame in. Returns true when the mailbox went from empty to holding a pending frame.</summary>
    public bool Post(VideoFrameData frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var now = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            if (_minIntervalTicks > 0 && _lastPostTicks != 0 && now - _lastPostTicks < _minIntervalTicks) return false;
            _lastPostTicks = now;
            var length = Math.Min(frame.Bgra.Length, checked(frame.Stride * frame.Height));
            var slot = 0;
            while (slot == _pendingSlot || slot == _currentSlot) slot++;
            var buffer = _buffers[slot];
            if (buffer is null || buffer.Length != length) _buffers[slot] = buffer = GC.AllocateUninitializedArray<byte>(length);
            Buffer.BlockCopy(frame.Bgra, 0, buffer, 0, length);
            var wasEmpty = _pending is null;
            _pendingSlot = slot;
            _pending = frame with { Bgra = buffer };
            return wasEmpty;
        }
    }

    public bool HasPending { get { lock (_sync) return _pending is not null; } }

    /// <summary>Returns the newest pending frame, or null when nothing new arrived. It stays valid until the next Take/Latest call.</summary>
    public VideoFrameData? Take()
    {
        lock (_sync)
        {
            if (_pending is null) return null;
            _currentSlot = _pendingSlot;
            _current = _pending;
            _pendingSlot = -1;
            _pending = null;
            return _current;
        }
    }

    /// <summary>Returns the newest frame received so far (pending or already taken), or null.</summary>
    public VideoFrameData? Latest()
    {
        lock (_sync)
        {
            if (_pending is not null)
            {
                _currentSlot = _pendingSlot;
                _current = _pending;
                _pendingSlot = -1;
                _pending = null;
            }
            return _current;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _pending = null;
            _current = null;
            _pendingSlot = _currentSlot = -1;
            _lastPostTicks = 0;
        }
    }
}
