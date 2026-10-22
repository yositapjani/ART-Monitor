using LibreHardwareMonitor.Hardware;
using System;

var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true };
computer.Open();
foreach (var hw in computer.Hardware)
{
    hw.Update();
    Console.WriteLine($"HW: {hw.HardwareType} | {hw.Name} | sensors={hw.Sensors.Count()}");
    foreach (var s in hw.Sensors.Take(40))
        Console.WriteLine($"  [{s.SensorType}] {s.Name} = {s.Value}");
    foreach (var sub in hw.SubHardware)
    {
        sub.Update();
        Console.WriteLine($"  SUB: {sub.HardwareType} | {sub.Name} | sensors={sub.Sensors.Count()}");
        foreach (var s in sub.Sensors.Take(40))
            Console.WriteLine($"    [{s.SensorType}] {s.Name} = {s.Value}");
    }
}
computer.Close();
