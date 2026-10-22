namespace HwFanControl.Core.Math;

/// <summary>
/// Lightweight ring buffer for temperature averaging (e.g. 5–30 s windows at 1 Hz).
/// Fixed capacity; no allocations after construction.
/// </summary>
public sealed class RollingAverageBuffer
{
    private readonly float[] _samples;
    private int _count;
    private int _head;
    private double _sum;

    public RollingAverageBuffer(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be >= 1.");

        _samples = new float[capacity];
    }

    public int Capacity => _samples.Length;
    public int Count => _count;

    public void Push(float sample)
    {
        if (_count == _samples.Length)
        {
            _sum -= _samples[_head];
            _samples[_head] = sample;
            _sum += sample;
            _head = (_head + 1) % _samples.Length;
            return;
        }

        int index = (_head + _count) % _samples.Length;
        _samples[index] = sample;
        _sum += sample;
        _count++;
    }

    public float Average => _count == 0 ? 0f : (float)(_sum / _count);

    public void Clear()
    {
        Array.Clear(_samples, 0, _samples.Length);
        _count = 0;
        _head = 0;
        _sum = 0;
    }
}
