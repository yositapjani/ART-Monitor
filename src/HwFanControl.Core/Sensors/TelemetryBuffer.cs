namespace HwFanControl.Core.Sensors;

/// <summary>
/// Fixed-capacity float buffer for one poll tick. Designed for reuse across the 1000 ms loop
/// to avoid GC pressure (no heap allocations on the hot path).
/// </summary>
public sealed class TelemetryBuffer
{
    private readonly float[] _values = new float[(int)SensorSlot.Capacity];
    private readonly bool[] _valid = new bool[(int)SensorSlot.Capacity];

    public long TimestampUtcTicks { get; set; }
    public bool IsStale { get; set; }
    public int Sequence { get; set; }

    public Span<float> Values => _values;
    public Span<bool> Valid => _valid;

    public void Clear()
    {
        Array.Clear(_values, 0, _values.Length);
        Array.Clear(_valid, 0, _valid.Length);
        IsStale = false;
    }

    public bool TryGet(SensorSlot slot, out float value)
    {
        int i = (int)slot;
        if ((uint)i >= (uint)_values.Length || !_valid[i])
        {
            value = default;
            return false;
        }

        value = _values[i];
        return true;
    }

    public void Set(SensorSlot slot, float value)
    {
        int i = (int)slot;
        if ((uint)i >= (uint)_values.Length)
            return;

        _values[i] = value;
        _valid[i] = true;
    }
}
