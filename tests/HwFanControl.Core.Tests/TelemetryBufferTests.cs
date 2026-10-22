using HwFanControl.Core.Sensors;

namespace HwFanControl.Core.Tests;

public class TelemetryBufferTests
{
    [Fact]
    public void Set_and_TryGet_roundtrip()
    {
        var buf = new TelemetryBuffer();
        buf.Set(SensorSlot.CpuPackageTemp, 67.5f);
        Assert.True(buf.TryGet(SensorSlot.CpuPackageTemp, out float v));
        Assert.Equal(67.5f, v);
        Assert.False(buf.TryGet(SensorSlot.GpuEdgeTemp, out _));
    }
}
