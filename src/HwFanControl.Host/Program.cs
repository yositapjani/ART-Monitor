using HwFanControl.Core;
using HwFanControl.Core.Curves;
using HwFanControl.Hardware;
using HwFanControl.Hardware.Sensors;
using HwFanControl.Host;

const int PollIntervalMs = 1000;

Console.WriteLine("HwFanControl host (headless). Prefer HwFanControl.App for UI. Ctrl+C restores BIOS.");

using var monitor = new HardwareMonitor();
using var safety = new SafetyManager(monitor);
var fanLogic = new FanLogicController();

safety.RegisterFailSafeHooks();

try
{
    monitor.Open();
    Console.WriteLine("Hardware open. RPM={0} PWM={1}", monitor.FanRpmChannelCount, monitor.FanControlChannelCount);
}
catch (Exception ex)
{
    Console.Error.WriteLine("Failed to open hardware: {0}", ex.Message);
    return 1;
}

var snapshot = SensorSnapshot.Create();
float[] pwmOut = new float[FanLogicController.MaxFanChannels];

using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollIntervalMs));
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    while (await timer.WaitForNextTickAsync(cts.Token))
    {
        monitor.Poll(ref snapshot);
        if (snapshot.Buffer.IsStale)
            continue;

        fanLogic.Evaluate(snapshot.Buffer, pwmOut.AsSpan(), DateTimeOffset.UtcNow);
        snapshot.Buffer.TryGet(HwFanControl.Core.Sensors.SensorSlot.CpuPackageTemp, out float cpu);
        snapshot.Buffer.TryGet(HwFanControl.Core.Sensors.SensorSlot.GpuEdgeTemp, out float gpu);
        Console.WriteLine("[{0:HH:mm:ss}] CPU={1:0.0} GPU={2:0.0}", DateTime.Now, cpu, gpu);
    }
}
catch (OperationCanceledException)
{
}
finally
{
    safety.EmergencyRestoreBiosControl();
}

return 0;
