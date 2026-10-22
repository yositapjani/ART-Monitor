using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;

namespace ArtMonitor.Bootstrapper;

/// <summary>
/// Portable ART-Monitor launcher. Verifies the two prerequisites of the
/// framework-dependent app build (.NET 8 Desktop Runtime + PawnIO driver),
/// installs whichever is missing via winget (elevated), then extracts the
/// embedded app to %LocalAppData%\ART-Monitor\app and starts it.
/// Set ARTMON_SIMULATE_MISSING=1 to exercise the install flow on a machine
/// that already has both prerequisites.
/// </summary>
internal static class Program
{
    private const string DotNetWingetId = "Microsoft.DotNet.DesktopRuntime.8";
    private const string PawnIoWingetId = "namazso.PawnIO";
    private const uint MbIconError = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--install-deps"))
            return InstallDeps();

        bool simulate = Environment.GetEnvironmentVariable("ARTMON_SIMULATE_MISSING") == "1";
        bool dotnetOk = !simulate && HasDotNet8Desktop();
        bool pawnOk = !simulate && HasPawnIo();

        if (!dotnetOk || !pawnOk)
        {
            if (!RunInstallStep())
                return 1;

            dotnetOk = HasDotNet8Desktop();
            pawnOk = HasPawnIo();
            if (!dotnetOk || !pawnOk)
            {
                string missing =
                    (!dotnetOk ? ".NET Desktop Runtime 8 belum terinstall. " : "") +
                    (!pawnOk ? "PawnIO belum terinstall." : "");
                MessageBoxW(IntPtr.Zero,
                    "ART-Monitor belum bisa jalan.\n" + missing +
                    "\n\nCoba jalankan sekali lagi sebagai Administrator.",
                    "ART-Monitor", MbIconError);
                return 1;
            }
        }

        string exe = ExtractAppExe();
        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        });
        return 0;
    }

    /// <summary>Runs the install step, elevating via UAC when needed.</summary>
    private static bool RunInstallStep()
    {
        if (IsAdmin())
            return InstallDeps() == 0;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = "--install-deps",
                UseShellExecute = true,
                Verb = "runas",
            };
            using var p = Process.Start(psi);
            p?.WaitForExit();
            return true; // Actual success is judged by the caller's re-check.
        }
        catch
        {
            MessageBoxW(IntPtr.Zero,
                "Instalasi komponen (.NET / PawnIO) dibatalkan.\nART-Monitor butuh komponen itu untuk jalan.",
                "ART-Monitor", MbIconError);
            return false;
        }
    }

    /// <summary>Elevated helper mode: installs whatever is missing via winget.</summary>
    private static int InstallDeps()
    {
        AllocConsole();
        Console.Title = "ART-Monitor - Install komponen";
        bool simulate = Environment.GetEnvironmentVariable("ARTMON_SIMULATE_MISSING") == "1";

        if (simulate || !HasDotNet8Desktop())
        {
            Console.WriteLine("Menginstall .NET Desktop Runtime 8 via winget...");
            RunWinget(DotNetWingetId);
        }

        if (simulate || !HasPawnIo())
        {
            Console.WriteLine("Menginstall PawnIO via winget...");
            RunWinget(PawnIoWingetId);
        }

        Console.WriteLine("Selesai. ART-Monitor akan dijalankan...");
        Thread.Sleep(1500);
        return 0;
    }

    private static void RunWinget(string id)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = $"install --id {id} -e --silent --accept-package-agreements --accept-source-agreements",
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit();
            Console.WriteLine($"winget {id}: exit code {p?.ExitCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"winget gagal dijalankan untuk {id}: {ex.Message}");
        }
    }

    private static bool HasDotNet8Desktop()
    {
        try
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string dir = Path.Combine(pf, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
            if (!Directory.Exists(dir))
                return false;
            foreach (string sub in Directory.GetDirectories(dir))
            {
                if (Path.GetFileName(sub).StartsWith("8.", StringComparison.Ordinal))
                    return true;
            }
        }
        catch
        {
            // fall through
        }

        return false;
    }

    private static bool HasPawnIo()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            if (key is not null)
                return true;
        }
        catch
        {
            // fall through
        }

        try
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (File.Exists(Path.Combine(pf, "PawnIO", "PawnIOLib.dll")))
                return true;
        }
        catch
        {
            // fall through
        }

        return false;
    }

    /// <summary>Extracts the embedded app exe once per version (SHA-256 gated).</summary>
    private static string ExtractAppExe()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ART-Monitor", "app");
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, "ART-Monitor.exe");
        string hashFile = Path.Combine(dir, "app.hash");

        using Stream res = Assembly.GetExecutingAssembly().GetManifestResourceStream("AppExe")
            ?? throw new InvalidOperationException("Embedded app payload not found.");

        res.Position = 0;
        string embeddedHash = Convert.ToHexString(SHA256.HashData(res));

        bool upToDate = File.Exists(target) && File.Exists(hashFile)
            && string.Equals(File.ReadAllText(hashFile).Trim(), embeddedHash, StringComparison.Ordinal);

        if (!upToDate)
        {
            res.Position = 0;
            try
            {
                using var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                res.CopyTo(fs);
                File.WriteAllText(hashFile, embeddedHash);
            }
            catch (IOException)
            {
                // The app is probably already running from this path; use the copy on disk.
                if (!File.Exists(target))
                    throw;
            }
        }

        return target;
    }

    private static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
