using HwFanControl.Hardware.Cpu;
var id = CpuIdentity.Read();
Console.WriteLine($"CPUID={id.CpuIdHex}");
Console.WriteLine($"Revision={id.Revision}");
Console.WriteLine($"Vendor={id.Vendor}");
Console.WriteLine($"Brand={id.BrandHint}");
Console.WriteLine($"F={id.Family} M={id.Model} S={id.Stepping} AMD={id.IsAmd}");
