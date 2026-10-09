using System.Numerics;
using K4os.Compression.LZ4;

namespace Arcade.Libretro;

/// <summary>
/// Remembers recent save states so play can be run backwards. Only the newest state is kept whole;
/// each older one is stored as the difference (XOR) from the state after it, compressed with LZ4.
/// Consecutive frames differ in few bytes, so a difference is mostly zeros and compresses to a few KB.
/// Stepping back undoes one difference at a time. Everything lives in one preallocated arena used
/// as a ring, so recording a frame allocates nothing; the oldest steps are dropped to make room.
/// </summary>
public sealed class RewindBuffer
{
    readonly byte[] _arena;
    readonly int _maxSteps;
    byte[] _current = [];
    byte[] _scratch = [];
    byte[] _compressed = [];
    bool _hasCurrent;

    // Stored differences, oldest first, as a ring of (offset, length) in the arena.
    (int Offset, int Length)[] _entries = new (int, int)[1024];
    int _first, _count;
    int _writePos;

    /// <param name="capacityBytes">Arena size for compressed differences.</param>
    /// <param name="maxSteps">Most steps kept (e.g. seconds × frame rate); older ones are dropped.</param>
    public RewindBuffer(int capacityBytes, int maxSteps)
    {
        _arena = GC.AllocateUninitializedArray<byte>(capacityBytes); // pages are only committed once written
        _maxSteps = Math.Max(1, maxSteps);
    }

    /// <summary>Steps that can be undone.</summary>
    public int Count => _count;

    /// <summary>Bytes of the arena holding differences (the whole newest state comes on top).</summary>
    public long BytesUsed
    {
        get
        {
            long total = 0;
            for (var i = 0; i < _count; i++)
                total += Entry(i).Length;
            return total;
        }
    }

    public int Capacity => _arena.Length;

    /// <summary>Records the state after a frame.</summary>
    public void Push(ReadOnlySpan<byte> state)
    {
        if (!_hasCurrent || state.Length != _current.Length)
        {
            // First state, or the core's state size changed: start over.
            Clear();
            _current = new byte[state.Length];
            _scratch = new byte[state.Length];
            _compressed = new byte[LZ4Codec.MaximumOutputSize(state.Length)];
            state.CopyTo(_current);
            _hasCurrent = true;
            return;
        }

        Xor(state, _current, _scratch);
        var length = LZ4Codec.Encode(_scratch, _compressed, LZ4Level.L00_FAST);
        state.CopyTo(_current);
        if (length <= 0 || length > _arena.Length)
        {
            DropAll(); // can't hold even one step; keep only the newest state
            return;
        }

        if (_count == _maxSteps)
            DropOldest();
        if (_writePos + length > _arena.Length)
        {
            // Wrap to the start. Anything still stored past here is older than everything before it, so it goes first.
            while (_count > 0 && Entry(0).Offset >= _writePos)
                DropOldest();
            _writePos = 0;
        }
        while (_count > 0 && Overlaps(Entry(0), _writePos, length))
            DropOldest();

        _compressed.AsSpan(0, length).CopyTo(_arena.AsSpan(_writePos));
        Add((_writePos, length));
        _writePos += length;
    }

    /// <summary>
    /// Goes back one step and returns that state (valid until the next call). False when there is
    /// nothing older; the newest state is then still available via <see cref="Peek"/>.
    /// </summary>
    public bool TryStepBack(out ReadOnlySpan<byte> state)
    {
        if (_count == 0)
        {
            state = default;
            return false;
        }
        var (offset, length) = Entry(_count - 1);
        _count--;
        _writePos = offset; // the space is free again
        var decoded = LZ4Codec.Decode(_arena.AsSpan(offset, length), _scratch);
        if (decoded != _scratch.Length)
            throw new InvalidOperationException("Rewind data is corrupt.");
        Xor(_current, _scratch, _current);
        state = _current;
        return true;
    }

    /// <summary>The newest recorded state, or empty if nothing was recorded.</summary>
    public ReadOnlySpan<byte> Peek() => _hasCurrent ? _current : default;

    public void Clear()
    {
        DropAll();
        _hasCurrent = false;
    }

    void DropAll()
    {
        _first = _count = 0;
        _writePos = 0;
    }

    static bool Overlaps((int Offset, int Length) entry, int start, int length) =>
        entry.Offset < start + length && entry.Offset + entry.Length > start;

    (int Offset, int Length) Entry(int index) => _entries[(_first + index) % _entries.Length];

    void Add((int, int) entry)
    {
        if (_count == _entries.Length)
        {
            var bigger = new (int, int)[_entries.Length * 2];
            for (var i = 0; i < _count; i++)
                bigger[i] = Entry(i);
            _entries = bigger;
            _first = 0;
        }
        _entries[(_first + _count) % _entries.Length] = entry;
        _count++;
    }

    void DropOldest()
    {
        _first = (_first + 1) % _entries.Length;
        _count--;
    }

    static void Xor(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> result)
    {
        var i = 0;
        var width = Vector<byte>.Count;
        for (; i <= a.Length - width; i += width)
            (new Vector<byte>(a[i..]) ^ new Vector<byte>(b[i..])).CopyTo(result[i..]);
        for (; i < a.Length; i++)
            result[i] = (byte)(a[i] ^ b[i]);
    }
}
