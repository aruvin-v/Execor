using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace Execor.Inference.Services;

public class SystemMonitorService
{
    private TimeSpan _prevCpuTime = TimeSpan.Zero;
    private DateTime _prevSampleTime = DateTime.UtcNow;

    public async Task<(float cpuUsage, float usedRamGB, float totalRamGB)> GetSystemStatsAsync()
    {
        await Task.Delay(200);

        float cpu = GetCpuUsage();
        var (usedRam, totalRam) = GetAccurateRamUsage();

        return (cpu, usedRam, totalRam);
    }

    private float GetCpuUsage()
    {
        var process = Process.GetCurrentProcess();

        var currentCpuTime = process.TotalProcessorTime;
        var currentTime = DateTime.UtcNow;

        var cpuUsedMs = (currentCpuTime - _prevCpuTime).TotalMilliseconds;
        var elapsedMs = (currentTime - _prevSampleTime).TotalMilliseconds;

        float cpuUsage = 0;

        if (elapsedMs > 0)
        {
            cpuUsage = (float)(
                cpuUsedMs /
                (Environment.ProcessorCount * elapsedMs) * 100
            );
        }

        _prevCpuTime = currentCpuTime;
        _prevSampleTime = currentTime;

        return cpuUsage;
    }

    private (float usedRamGB, float totalRamGB) GetAccurateRamUsage()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return GetWindowsRam();

            if (OperatingSystem.IsLinux())
                return GetLinuxRam();

            if (OperatingSystem.IsMacOS())
                return GetMacRam();
        }
        catch
        {
        }

        return (0, 0);
    }

    // ================= WINDOWS =================
    [SupportedOSPlatform("windows")]
    private (float usedRamGB, float totalRamGB) GetWindowsRam()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");

            foreach (var obj in searcher.Get())
            {
                ulong totalKb = Convert.ToUInt64(obj["TotalVisibleMemorySize"]);
                ulong freeKb = Convert.ToUInt64(obj["FreePhysicalMemory"]);

                float totalGB = totalKb / 1024f / 1024f;
                float usedGB = (totalKb - freeKb) / 1024f / 1024f;

                return (usedGB, totalGB);
            }
        }
        catch
        {
        }

        return (0, 0);
    }

    // ================= LINUX =================
    private (float usedRamGB, float totalRamGB) GetLinuxRam()
    {
        var lines = File.ReadAllLines("/proc/meminfo");

        ulong totalKb = 0;
        ulong freeKb = 0;

        foreach (var line in lines)
        {
            if (line.StartsWith("MemTotal:"))
                totalKb = ParseKb(line);

            if (line.StartsWith("MemAvailable:"))
                freeKb = ParseKb(line);
        }

        float totalGB = totalKb / 1024f / 1024f;
        float usedGB = (totalKb - freeKb) / 1024f / 1024f;

        return (usedGB, totalGB);
    }

    // ================= MAC =================
    private (float usedRamGB, float totalRamGB) GetMacRam()
    {
        ulong totalBytes = GetMacTotalRamBytes();
        ulong freeBytes = GetMacFreeRamBytes();

        float totalGB = totalBytes / 1024f / 1024f / 1024f;
        float usedGB = (totalBytes - freeBytes) / 1024f / 1024f / 1024f;

        return (usedGB, totalGB);
    }

    private ulong GetMacTotalRamBytes()
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "sysctl",
            Arguments = "-n hw.memsize",
            RedirectStandardOutput = true,
            UseShellExecute = false
        });

        string output = process!.StandardOutput.ReadToEnd();
        process.WaitForExit();

        return ulong.Parse(output.Trim());
    }

    private ulong GetMacFreeRamBytes()
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "vm_stat",
            RedirectStandardOutput = true,
            UseShellExecute = false
        });

        string output = process!.StandardOutput.ReadToEnd();
        process.WaitForExit();

        // Approximation fallback
        return 0;
    }

    private ulong ParseKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return ulong.Parse(parts[1]);
    }
}