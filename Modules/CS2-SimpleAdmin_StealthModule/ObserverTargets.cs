using CounterStrikeSharp.API.Core;

namespace CS2_SimpleAdmin_StealthModule;

/// <summary>Game-thread cache. The dense prefix contains at most one observer target per owner.</summary>
internal sealed class ObserverTargets
{
    // Source 2's transmit vector has 14 index bits. Older CSS releases report the
    // larger server entity limit here and their bit-vector writer trusts it.
    internal static int NetworkEdictLimit(int cssMaxEdicts) => Math.Min(cssMaxEdicts, 1 << 14);

    internal const int MaxSlots = 128;
    private readonly int[] _indices = new int[MaxSlots];
    private readonly int[] _owners = new int[MaxSlots];
    private readonly int[] _positions = new int[MaxSlots];
    private readonly uint[] _handles = new uint[MaxSlots];
    private readonly Dictionary<int, int> _indexCounts = new(MaxSlots);

    internal int Count { get; private set; }

    internal ObserverTargets() => Reset();

    internal void Reset()
    {
        Count = 0;
        Array.Fill(_positions, -1);
        _indexCounts.Clear();
    }

    internal void Set(int slot, int index, uint handle)
    {
        var position = _positions[slot];
        if (position >= 0 && _handles[slot] == handle && _indices[position] == index)
            return;

        Remove(slot);
        position = Count++;
        _positions[slot] = position;
        _indices[position] = index;
        _owners[position] = slot;
        _handles[slot] = handle;
        _indexCounts.TryGetValue(index, out var count);
        _indexCounts[index] = count + 1;
    }

    internal void Remove(int slot)
    {
        var position = _positions[slot];
        if (position < 0) return;

        var index = _indices[position];
        if (_indexCounts[index] == 1)
            _indexCounts.Remove(index);
        else
            _indexCounts[index]--;

        var last = --Count;
        if (position != last)
        {
            var moved = _owners[last];
            _positions[moved] = position;
            _owners[position] = moved;
            _indices[position] = _indices[last];
        }

        _positions[slot] = -1;
    }

    internal bool ContainsIndex(int index) => _indexCounts.ContainsKey(index);

    internal bool RemoveDeleted(int slot, uint handle)
    {
        if (_positions[slot] < 0 || _handles[slot] != handle) return false;
        Remove(slot);
        return true;
    }

    internal void Hide(CFixedBitVecBase entities, int recipientSlot)
    {
        var own = (uint)recipientSlot < MaxSlots ? _positions[recipientSlot] : -1;
        var indices = _indices;
        var count = Count;
        if (own < 0)
        {
            for (var i = 0; i < count; i++)
                entities.Remove(indices[i]);
            return;
        }

        for (var i = 0; i < own; i++)
            entities.Remove(indices[i]);
        for (var i = own + 1; i < count; i++)
            entities.Remove(indices[i]);
    }
}
