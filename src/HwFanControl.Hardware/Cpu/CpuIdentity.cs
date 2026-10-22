using System.Management;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace HwFanControl.Hardware.Cpu;

/// <summary>
/// Reads CPUID / stepping without a kernel driver (user-mode CPUID instruction + WMI).
/// Core Temp shows similar fields; those do not require PawnIO.
/// </summary>
public static class CpuIdentity
{
    public static CpuIdentityInfo Read()
    {
        var info = new CpuIdentityInfo();

        if (X86Base.IsSupported)
        {
            var (eax1, _, _, _) = X86Base.CpuId(1, 0);
            uint ueax = unchecked((uint)eax1);
            info.CpuIdHex = $"0x{ueax:X}";

            int stepping = (int)(ueax & 0xF);
            int model = (int)((ueax >> 4) & 0xF);
            int family = (int)((ueax >> 8) & 0xF);
            int extModel = (int)((ueax >> 16) & 0xF);
            int extFamily = (int)((ueax >> 20) & 0xFF);

            int displayFamily = family == 0xF ? family + extFamily : family;
            int displayModel = (family == 0x6 || family == 0xF) ? (extModel << 4) + model : model;

            info.Family = displayFamily;
            info.Model = displayModel;
            info.Stepping = stepping;
            info.Revision = FormatRevision(displayFamily, displayModel, stepping, ReadBrandString());
            info.Vendor = ReadVendor();
        }

        // WMI fills gaps / confirms ProcessorId.
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, ProcessorId, Caption, Stepping, Family, Level, Description FROM Win32_Processor");
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                string? wmiId = obj["ProcessorId"]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(wmiId) && info.CpuIdHex == "—")
                    info.CpuIdHex = wmiId.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? wmiId : $"0x{wmiId}";

                // Prefer compact hex from leaf 1 when available; keep WMI as secondary.
                if (info.CpuIdHex == "—" && !string.IsNullOrEmpty(wmiId))
                    info.CpuIdHex = wmiId;

                string? name = obj["Name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name) && string.IsNullOrEmpty(info.BrandHint))
                    info.BrandHint = name.Trim();

                break;
            }
        }
        catch
        {
            // WMI optional
        }

        if (string.IsNullOrEmpty(info.Revision) || info.Revision == "—")
            info.Revision = FormatRevision(info.Family, info.Model, info.Stepping, info.BrandHint);

        return info;
    }

    private static string ReadVendor()
    {
        if (!X86Base.IsSupported)
            return "";

        var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
        Span<byte> bytes = stackalloc byte[12];
        BitConverter.TryWriteBytes(bytes[..4], ebx);
        BitConverter.TryWriteBytes(bytes[4..8], edx);
        BitConverter.TryWriteBytes(bytes[8..12], ecx);
        return Encoding.ASCII.GetString(bytes);
    }

    private static string ReadBrandString()
    {
        if (!X86Base.IsSupported)
            return "";

        var sb = new StringBuilder(48);
        for (uint leaf = 0x80000002; leaf <= 0x80000004; leaf++)
        {
            var (eax, ebx, ecx, edx) = X86Base.CpuId(unchecked((int)leaf), 0);
            AppendRegs(sb, eax, ebx, ecx, edx);
        }

        return sb.ToString().Trim();
    }

    private static void AppendRegs(StringBuilder sb, int eax, int ebx, int ecx, int edx)
    {
        Span<byte> buf = stackalloc byte[16];
        BitConverter.TryWriteBytes(buf[..4], eax);
        BitConverter.TryWriteBytes(buf[4..8], ebx);
        BitConverter.TryWriteBytes(buf[8..12], ecx);
        BitConverter.TryWriteBytes(buf[12..16], edx);
        for (int i = 0; i < buf.Length; i++)
        {
            if (buf[i] == 0)
                break;
            sb.Append((char)buf[i]);
        }
    }

    private static string FormatRevision(int family, int model, int stepping, string? brand)
    {
        // Human-friendly codes similar to Core Temp (e.g. GNR-B0, Vermeer B2).
        string codename = InferCodename(family, model, brand);
        string step = stepping >= 0 ? $"B{stepping}" : "";
        if (!string.IsNullOrEmpty(codename) && !string.IsNullOrEmpty(step))
            return $"{codename}-{step}";
        if (!string.IsNullOrEmpty(codename))
            return codename;
        if (family > 0)
            return $"F{family:X}h_M{model:X2}h_S{stepping}";
        return "—";
    }

    private static string InferCodename(int family, int model, string? brand)
    {
        brand ??= "";
        if (brand.Contains("9950", StringComparison.OrdinalIgnoreCase) ||
            brand.Contains("9900", StringComparison.OrdinalIgnoreCase) ||
            brand.Contains("Granite", StringComparison.OrdinalIgnoreCase))
            return "GNR";

        if (brand.Contains("7950", StringComparison.OrdinalIgnoreCase) ||
            brand.Contains("7900", StringComparison.OrdinalIgnoreCase) ||
            brand.Contains("Raphael", StringComparison.OrdinalIgnoreCase))
            return "RPL";

        // Zen 3 Vermeer — Ryzen 5000 desktop (family 19h / 25, model 0x21 etc.)
        if (brand.Contains("5600", StringComparison.OrdinalIgnoreCase) ||
            brand.Contains("5800", StringComparison.OrdinalIgnoreCase) ||
            brand.Contains("5900", StringComparison.OrdinalIgnoreCase) ||
            brand.Contains("5950", StringComparison.OrdinalIgnoreCase) ||
            brand.Contains("Vermeer", StringComparison.OrdinalIgnoreCase))
            return "VMR";

        if (family == 0x19 || family == 25)
            return "ZEN3";
        if (family == 0x17 || family == 23)
            return "ZEN2";

        return "";
    }
}

public sealed class CpuIdentityInfo
{
    public string CpuIdHex { get; set; } = "—";
    public string Revision { get; set; } = "—";
    public string Vendor { get; set; } = "";
    public string BrandHint { get; set; } = "";
    public int Family { get; set; }
    public int Model { get; set; }
    public int Stepping { get; set; }
    public bool IsAmd => Vendor.Contains("Auth", StringComparison.OrdinalIgnoreCase) ||
                         BrandHint.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                         BrandHint.Contains("Ryzen", StringComparison.OrdinalIgnoreCase);
    public bool IsIntel => Vendor.Contains("Genu", StringComparison.OrdinalIgnoreCase) ||
                           BrandHint.Contains("Intel", StringComparison.OrdinalIgnoreCase);
}
