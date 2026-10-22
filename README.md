# ART-Monitor

Windows hardware monitor + fan control — gabungan gaya Argus Monitor + Core Temp:
dashboard **Overview** live (CPU / GPU / disks / network) plus kontrol kurva fan penuh.
(Dikembangkan awalnya dengan nama HwFanControl.)

Fan control Windows ala [Argus Monitor](https://www.argusmonitor.com/overview_fan_control.php?language=en): kurva suhu→PWM, multi-controller, min/kickstart/rate-limit, tray, restore BIOS saat exit.

Dibangun dengan **.NET 8**, **WPF**, Win32 P/Invoke, dan [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0).

## Fitur MVP

- Monitor suhu CPU / GPU / ambient
- Fan channel: **Disabled / Manual / Software**
- Editor kurva (linear / stepwise), drag titik
- **Multiple controllers** (hingga 4 → ambil PWM tertinggi)
- Min PWM (default 40%), kickstart 60%/2s, rate limit, hysteresis
- Profile JSON di `%AppData%\HwFanControl\profile.json`
- System tray; exit → `ReleaseControlToBios`
- Host console headless untuk debug

Belum termasuk: S.M.A.R.T., AIO Corsair/NZXT, halaman GPU fan khusus.

## Monitoring — halaman Overview

- Seksi **CPU / GPU / Disks / Network** dengan divider; disk ditandai huruf drive (C:–J:)
- Tabel per-core (load / clock / suhu, TjMax asli per CPU) + grafik gaya OCCT
- Grafik **Network In / Out** + kecepatan; panel quick-info: jam besar + tanggal, uptime, IP, traffic sesi (down/up), fan RPM live
- Peringatan suhu asli, logging CSV, dark mode (font seleksi putih)
- Start maximized; selector disk 3 baris alfabetis; launcher portable (winget auto-install runtime bila perlu)

## Layout

```
src/HwFanControl.Core/       Engine kurva + safety + profile
src/HwFanControl.Hardware/   HardwareMonitor + LHM + IOCTL boundary
src/HwFanControl.App/        WPF UI (entry utama)
src/HwFanControl.Host/       Console headless
```

## Jalankan

**Suhu AMD / SuperIO fan:** LibreHardwareMonitorLib 0.9.5+ memakai **PawnIO** (bukan WinRing0).
Install dari [PawnIO Releases](https://github.com/namazso/PawnIO/releases), restart PC, lalu buka app.
Admin saja **tidak** cukup — tanpa PawnIO, Tdie sering tetap 0°C.

```powershell
dotnet build HwFanControl.sln -c Release
dotnet run --project src/HwFanControl.App -c Release
```

Exe publish:

```powershell
dotnet publish src/HwFanControl.App/HwFanControl.App.csproj -c Release -r win-x64 --self-contained false -o publish
.\publish\HwFanControl.exe
```

## Cara pakai singkat

1. Buka app (elevated).
2. Di panel Fan: pilih **Manual**, geser slider, **Terapkan**.
3. Atau **Software** → pilih sumber suhu → **Kurva…** → simpan.
4. Untuk CPU+GPU: **Multi…**, aktifkan 2 controller, masing-masing kurva sendiri.
5. Minimize = tray; **Keluar** dari tray menu untuk restore BIOS.

## Lisensi

Kode app: milik Anda. LibreHardwareMonitorLib tetap **MPL-2.0**.
