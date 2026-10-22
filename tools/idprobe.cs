using HwFanControl.Hardware.Cpu;
var id = CpuIdentity.Read();
Console.WriteLine($"CPUID={id.CpuIdHex}");
Console.WriteLine($"Revision={id.Revision}");
Console.WriteLine($"Vendor={id.Vendor}");
Console.WriteLine($"Brand={id.BrandHint}");
Console.WriteLine($"AMD={id.IsAmd} Intel={id.IsIntel}");
