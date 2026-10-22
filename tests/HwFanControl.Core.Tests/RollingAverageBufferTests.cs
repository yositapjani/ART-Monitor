using HwFanControl.Core.Math;

namespace HwFanControl.Core.Tests;

public class RollingAverageBufferTests
{
    [Fact]
    public void Average_tracks_ring_without_allocating_beyond_capacity()
    {
        var buffer = new RollingAverageBuffer(3);
        buffer.Push(10);
        buffer.Push(20);
        buffer.Push(30);
        Assert.Equal(20f, buffer.Average);

        buffer.Push(40); // drops 10
        Assert.Equal(30f, buffer.Average);
        Assert.Equal(3, buffer.Count);
    }
}
