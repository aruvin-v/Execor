// File: src/Execor.Inference/Services/McpClientService.cs

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Execor.Models;

namespace Execor.Inference.Services;

public class McpClientService : IDisposable
{
    private readonly ConcurrentDictionary<string, McpServerInstance> _servers = new();
    public List<McpTool> AvailableTools => _servers.Values.SelectMany(s => s.Tools).ToList();

    public async Task ConnectAsync(string serverName, string executable, string arguments)
    {
        var instance = new McpServerInstance();
        await instance.StartAsync(executable, arguments);
        _servers[serverName] = instance;
    }

    public async Task<string> CallToolAsync(string toolName, Dictionary<string, object> arguments)
    {
        // Find which server owns this tool
        var server = _servers.Values.FirstOrDefault(s => s.Tools.Any(t => t.Name == toolName));
        if (server == null) return $"❌ Error: Tool '{toolName}' not found on any connected MCP server.";

        return await server.CallToolAsync(toolName, arguments);
    }

    public void Dispose()
    {
        foreach (var server in _servers.Values) server.Dispose();
        _servers.Clear();
    }
}

internal class McpServerInstance : IDisposable
{
    private Process? _process;
    private StreamWriter? _writer;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    public List<McpTool> Tools { get; private set; } = new();

    public async Task StartAsync(string executable, string arguments)
    {
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        _process.Start();
        _writer = _process.StandardInput;

        _ = Task.Run(ListenLoop);

        // Handshake
        await SendRequestAsync("initialize", new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "Execor", version = "1.0.0" } });
        await SendNotificationAsync("notifications/initialized");

        // Load Tools
        var response = await SendRequestAsync("tools/list", new { });
        if (response.TryGetProperty("tools", out var toolsArray))
        {
            Tools = JsonSerializer.Deserialize<List<McpTool>>(toolsArray.GetRawText()) ?? new();
        }
    }

    public async Task<string> CallToolAsync(string name, Dictionary<string, object> args)
    {
        var response = await SendRequestAsync("tools/call", new { name, arguments = args });
        if (response.TryGetProperty("content", out var content) && content.GetArrayLength() > 0)
        {
            return content[0].GetProperty("text").GetString() ?? "Success";
        }
        return "Tool execution returned no text.";
    }

    private async Task<JsonElement> SendRequestAsync(string method, object? @params)
    {
        var id = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<JsonElement>();
        _pendingRequests[id] = tcs;

        var request = new { jsonrpc = "2.0", id, method, @params };
        await _writer!.WriteLineAsync(JsonSerializer.Serialize(request));
        await _writer.FlushAsync();

        return await tcs.Task;
    }

    private async Task SendNotificationAsync(string method)
    {
        var note = new { jsonrpc = "2.0", method };
        await _writer!.WriteLineAsync(JsonSerializer.Serialize(note));
        await _writer.FlushAsync();
    }

    private async Task ListenLoop()
    {
        using var reader = _process!.StandardOutput;
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var resp = JsonSerializer.Deserialize<JsonRpcResponse>(line);
                if (resp?.Id != null && _pendingRequests.TryRemove(resp.Id.Value.GetRawText().Trim('"'), out var tcs))
                {
                    tcs.SetResult(resp.Result ?? default);
                }
            }
            catch { }
        }
    }

    public void Dispose() { _process?.Kill(); _process?.Dispose(); }
}