using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Execor.Models;

namespace Execor.Inference.Services;

public class McpClientService : IDisposable
{
    private Process? _serverProcess;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests = new();

    public List<McpTool> AvailableTools { get; private set; } = new();
    public bool IsConnected => _serverProcess != null && !_serverProcess.HasExited;

    public async Task ConnectAsync(string executable, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _serverProcess = new Process { StartInfo = startInfo };
        _serverProcess.Start();

        _writer = _serverProcess.StandardInput;
        _reader = _serverProcess.StandardOutput;

        // Start listening to the stdout pipe in the background
        _ = Task.Run(ListenLoop);

        // 1. Initialize Handshake
        var initReq = new JsonRpcRequest
        {
            Method = "initialize",
            Params = new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "Execor", version = "1.0.0" } }
        };
        await SendRequestAsync(initReq);

        // 2. Send initialized notification
        await SendNotificationAsync("notifications/initialized");

        // 3. Fetch Available Tools
        var toolsReq = new JsonRpcRequest { Method = "tools/list" };
        var toolsResponse = await SendRequestAsync(toolsReq);

        if (toolsResponse.TryGetProperty("tools", out var toolsArray))
        {
            AvailableTools = JsonSerializer.Deserialize<List<McpTool>>(toolsArray.GetRawText()) ?? new();
        }
    }

    public async Task<string> CallToolAsync(string toolName, Dictionary<string, object> arguments)
    {
        var req = new JsonRpcRequest
        {
            Method = "tools/call",
            Params = new McpCallToolRequest { Name = toolName, Arguments = arguments }
        };

        var response = await SendRequestAsync(req);

        if (response.TryGetProperty("content", out var contentArray) && contentArray.GetArrayLength() > 0)
        {
            return contentArray[0].GetProperty("text").GetString() ?? "Success (No output)";
        }

        if (response.TryGetProperty("isError", out var isError) && isError.GetBoolean())
        {
            return "Tool Execution Error.";
        }

        return response.GetRawText();
    }

    private async Task<JsonElement> SendRequestAsync(JsonRpcRequest request)
    {
        var tcs = new TaskCompletionSource<JsonElement>();
        _pendingRequests[request.Id] = tcs;

        string json = JsonSerializer.Serialize(request);
        await _writer!.WriteLineAsync(json);
        await _writer.FlushAsync();

        return await tcs.Task;
    }

    private async Task SendNotificationAsync(string method)
    {
        var notification = new { jsonrpc = "2.0", method = method };
        await _writer!.WriteLineAsync(JsonSerializer.Serialize(notification));
        await _writer.FlushAsync();
    }

    private async Task ListenLoop()
    {
        try
        {
            while (_reader != null && !_reader.EndOfStream)
            {
                string? line = await _reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var response = JsonSerializer.Deserialize<JsonRpcResponse>(line);
                if (response != null && response.Id != null && _pendingRequests.TryRemove(response.Id, out var tcs))
                {
                    if (response.Error != null)
                        tcs.SetException(new Exception(response.Error.Value.GetRawText()));
                    else
                        tcs.SetResult(response.Result ?? default);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MCP Pipe Error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _serverProcess?.Kill();
        _serverProcess?.Dispose();
    }
}