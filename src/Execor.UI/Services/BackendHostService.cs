using System;
using System.Diagnostics;
using System.IO;

namespace Execor.UI.Services;

public class BackendHostService
{
    private Process? _apiProcess;

    public void StartBackend()
    {
        var solutionRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\"));

        var apiDllPath = Path.Combine(
            solutionRoot,
            "Execor.API",
            "bin",
            "Debug",
            "net8.0",
            "Execor.API.dll");

        _apiProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName        = "dotnet",
                Arguments       = $"\"{apiDllPath}\"",
                UseShellExecute = false,
                CreateNoWindow  = false
            }
        };

        _apiProcess.Start();
    }

    public void StopBackend()
    {
        if (_apiProcess != null && !_apiProcess.HasExited)
            _apiProcess.Kill(true);
    }
}
