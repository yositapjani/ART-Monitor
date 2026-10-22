using System.Diagnostics;
using Microsoft.Win32;

namespace HwFanControl.Hardware.Cpu;

/// <summary>
/// LibreHardwareMonitorLib 0.9.5+ uses PawnIO (not WinRing0) for AMD CPU temps / SuperIO.
/// Elevation alone does not enable readings — the PawnIO driver must be installed.
/// </summary>
public static class PawnIoStatus
{
    public const string DownloadUrl = "https://github.com/namazso/PawnIO/releases";

    public static bool IsInstalled()
    {
        try
        {
            string[] paths =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PawnIO"),
                Path.Combine(Environment.SystemDirectory, "drivers", "PawnIO.sys"),
                Path.Combine(Environment.SystemDirectory, "drivers", "pawnio.sys"),
            ];
            if (paths.Any(p => Directory.Exists(p) || File.Exists(p)))
                return true;
        }
        catch
        {
            // ignore
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO")
                            ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PawnIO")
                            ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\PawnIO");
            if (key is not null)
                return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }

    public static string GetGuidance(bool elevated, bool cpuTempLooksDead)
    {
        bool pawn = IsInstalled();
        if (cpuTempLooksDead && !pawn)
        {
            return "Suhu AMD = 0°C: install driver PawnIO (LHM 0.9.5+ tidak pakai WinRing0). " +
                   "Menu Tools → Pasang PawnIO… lalu restart app.";
        }

        if (cpuTempLooksDead && pawn)
        {
            return "PawnIO terdeteksi tapi suhu masih 0°C. Restart PC setelah install, " +
                   "atau reinstall PawnIO (versi standard, bukan unrestricted).";
        }

        if (!elevated && !pawn)
            return "Opsional: PawnIO untuk suhu CPU/SuperIO penuh.";

        return pawn ? "PawnIO: OK" : "PawnIO: tidak terpasang";
    }

    public static void OpenDownloadPage()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = DownloadUrl,
            UseShellExecute = true,
        });
    }
}
