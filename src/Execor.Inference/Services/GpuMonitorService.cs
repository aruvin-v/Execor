using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace Execor.Inference.Services;

public class GpuMonitorService
{
    public async Task<(string gpuName, int usage, int usedMB, int totalMB, string status)> GetGpuStatsAsync()
    {
        // NVIDIA first (all OS if available)
        var nvidia = await TryGetNvidiaStatsAsync();
        if (nvidia != null)
            return nvidia.Value;

        // Windows fallback
        if (OperatingSystem.IsWindows())
        {
            var win = TryGetWindowsGpuStats();
            if (win != null)
                return win.Value;
        }

        // Linux fallback
        if (OperatingSystem.IsLinux())
        {
            var linux = await TryGetLinuxGpuStatsAsync();
            if (linux != null)
                return linux.Value;
        }

        // macOS fallback
        if (OperatingSystem.IsMacOS())
        {
            var mac = await TryGetMacGpuStatsAsync();
            if (mac != null)
                return mac.Value;
        }

        return ("No GPU Detected", 0, 0, 0, "Unavailable");
    }

    // ================= NVIDIA =================
    private async Task<(string gpuName, int usage, int usedMB, int totalMB, string status)?> TryGetNvidiaStatsAsync()
    {
        try
        {
            var process = new Process();
            process.StartInfo.FileName = "nvidia-smi";
            process.StartInfo.Arguments =
                "--query-gpu=name,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;

            process.Start();

            string output = await process.StandardOutput.ReadToEndAsync();
            process.WaitForExit();

            var parts = output.Trim().Split(',');

            return (
                parts[0].Trim(),
                int.Parse(parts[1].Trim()),
                int.Parse(parts[2].Trim()),
                int.Parse(parts[3].Trim()),
                "CUDA Active"
            );
        }
        catch
        {
            return null;
        }
    }

    // ================= WINDOWS =================
    [SupportedOSPlatform("windows")]
    private (string gpuName, int usage, int usedMB, int totalMB, string status)? TryGetWindowsGpuStats()
    {
        try
        {
            using var searcher =
                new ManagementObjectSearcher(
                    "SELECT Name, AdapterRAM FROM Win32_VideoController");

            foreach (var obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "Unknown GPU";

                ulong ramBytes = obj["AdapterRAM"] != null
                    ? Convert.ToUInt64(obj["AdapterRAM"])
                    : 0;

                int totalMB = (int)(ramBytes / 1024 / 1024);

                return (
                    name,
                    0,
                    0,
                    totalMB,
                    "Generic GPU Active"
                );
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    // ================= LINUX =================
    private async Task<(string gpuName, int usage, int usedMB, int totalMB, string status)?> TryGetLinuxGpuStatsAsync()
    {
        try
        {
            var process = new Process();
            process.StartInfo.FileName = "bash";
            process.StartInfo.Arguments = "-c \"lspci | grep VGA\"";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.UseShellExecute = false;

            process.Start();

            string output = await process.StandardOutput.ReadToEndAsync();
            process.WaitForExit();

            string gpu = output.Trim();

            if (!string.IsNullOrWhiteSpace(gpu))
            {
                return (
                    gpu,
                    0,
                    0,
                    0,
                    "Linux GPU Active"
                );
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    // ================= MAC =================
    private async Task<(string gpuName, int usage, int usedMB, int totalMB, string status)?> TryGetMacGpuStatsAsync()
    {
        try
        {
            var process = new Process();
            process.StartInfo.FileName = "system_profiler";
            process.StartInfo.Arguments = "SPDisplaysDataType";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.UseShellExecute = false;

            process.Start();

            string output = await process.StandardOutput.ReadToEndAsync();
            process.WaitForExit();

            var lines = output.Split('\n');

            foreach (var line in lines)
            {
                if (line.Trim().StartsWith("Chipset Model:"))
                {
                    string gpu = line.Replace("Chipset Model:", "").Trim();

                    return (
                        gpu,
                        0,
                        0,
                        0,
                        "macOS GPU Active"
                    );
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}