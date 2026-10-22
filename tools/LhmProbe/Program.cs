using LibreHardwareMonitor.Hardware;

var computer = new Computer
{
    IsCpuEnabled = true,
    IsGpuEnabled = true,
    IsMotherboardEnabled = true,
    IsControllerEnabled = true,
};
computer.Open();
computer.Accept(new UpdateVisitor());
computer.Accept(new UpdateVisitor());

foreach (var hw in computer.Hardware)
    Dump(hw, 0);

computer.Close();

static void Dump(IHardware hw, int indent)
{
    string pad = new(' ', indent);
    Console.WriteLine($"{pad}{hw.HardwareType}: {hw.Name} (sensors={hw.Sensors.Count()})");
    foreach (var s in hw.Sensors)
        Console.WriteLine($"{pad}  [{s.SensorType}] {s.Name} = {s.Value?.ToString() ?? "null"}");
    foreach (var sub in hw.SubHardware)
        Dump(sub, indent + 2);
}

sealed class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) => computer.Traverse(this);
    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware)
            sub.Accept(this);
    }
    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}
